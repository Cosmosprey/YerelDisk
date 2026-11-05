namespace YerelDisk.Presentation;

// A disjoint frontier of existing scan folders, not new classification or I/O.
// Known subtrees and unsafe branches split their ancestors. Selected roots with
// children are expanded once so a whole drive is not the only discovery row.
internal sealed record UnrecognizedAreas(IReadOnlyList<FolderEntry> Folders, int VisitedFolders)
{
    internal const string Notice = "Kural eşleşmesi olmayan alt ağaçlar; amaç ve kullanım bilinmiyor. Sistem, uygulama veya kişisel veri içerebilir. Bu liste silme önerisi değildir.";
    internal const string Coverage = "Hariç tutulan bir alt dalın üst klasörü listede yer alabilir; görünen boyut yalnız taranan kısmı içerir. Hariç dalın boyutu ve içeriği ölçülmüş sayılmaz. İç içe klasörler aynı listede tekrar gösterilmez; tanınan alt ağaçlar dışarıda tutulur. Seçili köklerin ve bölünen üst klasörlerin doğrudan dosyaları bu listede ayrı satır değildir; Büyük dosyalar görünümünde inceleyin. Ölçülemeyen / doğrulanamayan dallar listelenmez; yoklukları boş veya gereksiz alan anlamına gelmez. Boyut filtresi ve arama yalnız hazırlanan bu listeye uygulanır, gizlenen alt klasörleri yeniden açmaz.";
    internal const string FirstStep = "Satıra çift tıklayarak veya tablo satırı odaktayken Enter ile alt klasörleri incele; ait olduğu uygulamayı veya çalışmayı doğrula. Gereksiz olduğu sonucunu yalnız adından, yaşından veya boyutundan çıkarma.";

    internal static UnrecognizedAreas Create(ScanResult scan, IReadOnlyList<ReviewCandidate> candidates, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (scan.ScopeChanged && scan.UnverifiedPaths.Count == 0) return new([], 0);
        var roots = scan.RootPaths.Select(ReviewRules.CanonicalLocal).OfType<string>()
            .Select(p => p.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (roots.Count == 0) return new([], 0);
        bool Inside(string path)
        { foreach (string root in roots) if (ScanExclusions.SameOrBelow(path, root)) return true; return false; }
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var splitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (ReviewRules.CanonicalLocal(c.Path) is not { } path || !Inside(path) || !scan.CanUsePath(path)) continue;
            known.Add(path.TrimEnd('\\'));
            for (string? parent = Path.GetDirectoryName(path.TrimEnd('\\')); parent != null && Inside(parent); parent = Path.GetDirectoryName(parent.TrimEnd('\\')))
                splitPaths.Add(parent.TrimEnd('\\'));
        }
        var tops = new List<FolderEntry>();
        string? rootPath = ReviewRules.CanonicalLocal(scan.Root.Path);
        if (rootPath != null && roots.Contains(rootPath.TrimEnd('\\'))) tops.Add(scan.Root);
        else if (rootPath == null && scan.RootPaths.Count > 1)
            tops.AddRange(scan.Root.Children.Where(c => ReferenceEquals(c.Parent, scan.Root)
                && ReviewRules.CanonicalLocal(c.Path) is { } p && roots.Contains(p.TrimEnd('\\'))));
        var order = new List<FolderEntry>();
        var valid = new HashSet<FolderEntry>();
        var split = new HashSet<FolderEntry>();
        var pending = new Stack<FolderEntry>(tops);
        int childrenSeen = 0;
        while (pending.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if (ReviewRules.CanonicalLocal(node.Path) is not { } path || !Inside(path) || !scan.CanUsePath(path)
                || node.Measurement is MeasurementState.Unknown or MeasurementState.Previous)
            { if (node.Parent != null) split.Add(node.Parent); continue; }
            if (!valid.Add(node)) continue;
            order.Add(node);
            if (known.Contains(path.TrimEnd('\\'))) { split.Add(node); continue; }
            if (splitPaths.Contains(path.TrimEnd('\\'))) split.Add(node);
            foreach (var child in node.Children)
            {
                if ((childrenSeen++ & 1023) == 0) token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(child.Parent, node) || ReviewRules.CanonicalLocal(child.Path) is not { } childPath
                    || !string.Equals(Path.GetDirectoryName(childPath.TrimEnd('\\'))?.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                { split.Add(node); continue; }
                pending.Push(child);
            }
        }
        for (int i = order.Count - 1; i >= 0; i--)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var node = order[i];
            if (split.Contains(node) && node.Parent is { } parent && valid.Contains(parent)) split.Add(parent);
        }
        var folders = new List<FolderEntry>();
        var topSet = new HashSet<FolderEntry>(tops);
        pending = new(tops);
        while (pending.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if (!valid.Contains(node) || known.Contains(node.Path.TrimEnd('\\'))) continue;
            bool hasChildren = false;
            foreach (var child in node.Children) if (valid.Contains(child)) { hasChildren = true; break; }
            if (split.Contains(node) || (topSet.Contains(node) && hasChildren))
            { foreach (var child in node.Children) if (valid.Contains(child)) pending.Push(child); }
            else if (node.LogicalBytes > 0 || node.FileCount > 0 || node.IssueCount > 0 || node.Measurement == MeasurementState.Partial)
                folders.Add(node);
        }
        return new(folders, order.Count);
    }
}
