namespace YerelDisk.Presentation;

internal sealed record PopulationTypeRow(string Extension, long Count, long Bytes, double Fraction)
{
    public string CountText => Count.ToString("N0");
    public string SizeText => PresentationText.Size(Bytes);
    public string ShareText => Fraction.ToString("P1");
}
internal readonly record struct PopulationAreaRow(string Path, string? Caption, long? Count, long? Bytes, bool Partial, double Fraction = 0)
{
    public string Name => Caption ?? System.IO.Path.GetFileName(Path.TrimEnd('\\'));
    public string CountText => Count is { } n ? n.ToString("N0") + (Partial ? " · kısmi" : "") : "Ölçülemedi";
    public string SizeText => Bytes is { } b ? PresentationText.Size(b) : "—";
    public string ShareText => Count.HasValue ? Fraction.ToString("P1") : "—";
}

// Works on bounded, completed session summaries. Never opens files, scans the
// disk again, or substitutes the top-K file list when evidence is unavailable.
internal sealed record FilePopulationAnalysis(string Path, bool Available, bool Complete, long Count, long Bytes,
    long Small, long Empty, string Coverage, IReadOnlyList<PopulationTypeRow> Types, IReadOnlyList<PopulationAreaRow> Areas)
{
    public string CountText => Count.ToString("N0");
    public string SmallText => Small.ToString("N0") + (Count > 0 ? " · " + ((double)Small / Count).ToString("P1") : "");
    public string EmptyText => Empty.ToString("N0");
    public string Heading => !Available ? "Analiz hazırlanamadı" : Complete ? "Taranan dosyaların yapısı" : "Kısmi dosya yapısı";
    public string Explanation => !Available ? Coverage
        : Count == 0 ? Complete ? "Taranan kapsamda dosya bulunmadı." : "Bu kapsamda analiz edilebilir doğrulanmış dosya özeti yok; boş klasör olduğu anlamına gelmez."
        : $"{Count:N0} dosya adı analiz edildi. {Small:N0} tanesi 0–4 KiB ({(double)Small / Count:P1}); buna {Empty:N0} adet 0 baytlık dosya dahildir.";
    public const string Limits = "Uzantılar dosya adından gelir; gerçek dosya biçimi, kullanım veya gereksizlik belirlenmez. Küçük dosyalar silme önerisi değildir. Hard link adları ayrı sayılır; boyutlar fiziksel veya geri kazanılabilir alan değildir. Paylar yalnız analiz edilen dosya adlarına göredir. Ana listedeki arama ve dosya listesi sınırı bu analizi filtrelemez. İçerikler okunmadı; yeniden tarama yapılmadı.";

    internal static FilePopulationAnalysis Create(ScanResult scan, FolderEntry target, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        bool member = false;
        for (FolderEntry? ancestor = target; ancestor != null; ancestor = ancestor.Parent)
        {
            if (ancestor.Measurement is MeasurementState.Unknown or MeasurementState.Previous)
                return Unavailable("Bu konumun veya üst klasörünün ölçümü doğrulanamadı. Yeniden tarayıp deneyin.");
            if (ReferenceEquals(ancestor, scan.Root)) { member = true; break; }
        }
        if (!member || !scan.CanUsePath(target.Path)) return Unavailable("Bu konum güncel taramanın doğrulanabilen kapsamında değil.");
        if (scan.ObservedEvidence?.Population is not { HasSnapshots: true } evidence)
            return Unavailable("Bu taramada dosya yapısı özeti yok. Yeni bir tarama yapın; en büyük dosyalar listesi bütün dosyaların yerine kullanılmaz.");

        var types = new Dictionary<string, FileTypePopulation>(StringComparer.OrdinalIgnoreCase);
        long count = 0, bytes = 0, small = 0, empty = 0;
        FileTypePopulation unclassified = default;
        int skipped = 0;
        var direct = AddOwner(target);
        var top = new PriorityQueue<PopulationAreaRow, PopulationAreaRow>(Comparer<PopulationAreaRow>.Create((a, b) =>
        {
            int c = Nullable.Compare(a.Count, b.Count);
            return c != 0 ? c : StringComparer.Ordinal.Compare(b.Path, a.Path);
        }));
        int childCount = 0, hiddenPartial = 0;
        long childNames = 0, childBytes = 0;
        bool childPartial = false;
        foreach (var child in target.Children)
        {
            token.ThrowIfCancellationRequested();
            var observed = Collect(child);
            bool partial = child.Measurement != MeasurementState.Measured || child.IssueCount != 0 || observed.Count != child.FileCount || observed.Skipped;
            bool known = observed.Count > 0 || (!partial && child.FileCount == 0);
            var row = new PopulationAreaRow(child.Path, null, known ? observed.Count : null, known ? observed.Bytes : null, partial);
            if (top.Count < 12) top.Enqueue(row, row);
            else if (top.EnqueueDequeue(row, row).Partial) hiddenPartial++;
            childCount++; childNames = Sum(childNames, observed.Count); childBytes = Sum(childBytes, observed.Bytes); childPartial |= partial;
        }

        bool complete = target.Measurement == MeasurementState.Measured && target.IssueCount == 0
            && count == target.FileCount && skipped == 0 && !childPartial && !scan.Cancelled && bytes != long.MaxValue;
        var typeRows = types.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(12).Select(p => new PopulationTypeRow(p.Key.Length == 0 ? "Uzantısız" : p.Key, p.Value.Count, p.Value.Bytes, Share(p.Value.Count))).ToList();
        long displayedNames = 0, displayedBytes = 0;
        foreach (var row in typeRows) { displayedNames = Sum(displayedNames, row.Count); displayedBytes = Sum(displayedBytes, row.Bytes); }
        if (count > displayedNames && bytes != long.MaxValue)
            typeRows.Add(new(unclassified.Count > 0 ? "Diğer / özetlenemeyen uzantılar" : "Diğer uzantılar", count - displayedNames, bytes - displayedBytes, Share(count - displayedNames)));

        var areas = top.UnorderedItems.Select(item => item.Element).OrderByDescending(r => r.Count.HasValue)
            .ThenByDescending(r => r.Count).ThenBy(r => r.Path, StringComparer.Ordinal)
            .Select(r => r with { Fraction = Share(r.Count ?? 0) }).ToList();
        long shownChildNames = 0, shownChildBytes = 0;
        foreach (var row in areas) { shownChildNames = Sum(shownChildNames, row.Count ?? 0); shownChildBytes = Sum(shownChildBytes, row.Bytes ?? 0); }
        if (childCount > areas.Count && childBytes != long.MaxValue)
        {
            long remaining = childNames - shownChildNames;
            bool otherPartial = hiddenPartial > 0;
            areas.Add(new("", $"Diğer {childCount - areas.Count:N0} alt klasör", remaining > 0 || !otherPartial ? remaining : null,
                remaining > 0 || !otherPartial ? childBytes - shownChildBytes : null, otherPartial, Share(remaining)));
        }
        bool directKnown = direct.Count > 0 || complete;
        areas.Add(new(target.Path, "Bu klasördeki doğrudan dosyalar", directKnown ? direct.Count : null, directKnown ? direct.Bytes : null,
            !complete, Share(direct.Count)));

        string coverage = complete ? "Analiz, taranan kapsamdaki bütün dosya adlarını içeriyor." : $"Kısmi analiz: {count:N0} dosya adı özetlendi; klasörün son/kısmi sayısı {target.FileCount:N0}. Oranlar eksik alanları kapsamaz.";
        if (target.ExcludedSubtrees > 0 || scan.ExcludedPaths.Any(path => ScanExclusions.SameOrBelow(path, target.Path)))
            coverage += " Bilerek hariç tutulan alanlar analiz edilmedi.";
        if (unclassified.Count > 0) coverage += " Bazı uzantılar özet bütçesi veya ad uzunluğu sınırı nedeniyle ayrı grupta; dosya sayıları korunur.";
        if (!complete && evidence.OwnerLimitReached) coverage += " Klasör özeti bütçesine ulaşıldı; saklanamayan girdiler eksik olabilir.";
        if (bytes == long.MaxValue) coverage += " Boyut toplamı sayısal sınıra ulaştı; tam boyut olarak değerlendirmeyin.";
        if (scan.Cancelled) coverage += " Tarama durdurulmuştu.";
        return new(target.Path, true, complete, count, bytes, small, empty, coverage, typeRows, areas);

        FilePopulationAnalysis Unavailable(string reason) => new(target.Path, false, false, 0, 0, 0, 0, reason, [], []);
        double Share(long n) => count > 0 ? (double)n / count : 0;
        FileTypePopulation AddOwner(FolderEntry folder)
        {
            if (!evidence.Owners.TryGetValue(folder, out var summary)) return default;
            count = Sum(count, summary.Count); bytes = Sum(bytes, summary.Bytes);
            small = Sum(small, summary.Small); empty = Sum(empty, summary.Empty);
            foreach (var item in summary.Types) types[item.Key] = types.GetValueOrDefault(item.Key).Add(item.Value.Count, item.Value.Bytes);
            unclassified = unclassified.Add(summary.Other.Count, summary.Other.Bytes);
            return new(summary.Count, summary.Bytes);
        }
        (long Count, long Bytes, bool Skipped) Collect(FolderEntry root)
        {
            var pending = new Stack<FolderEntry>(); pending.Push(root);
            long names = 0, total = 0; bool incomplete = false; int visits = 0;
            while (pending.TryPop(out var folder))
            {
                if ((++visits & 255) == 0) token.ThrowIfCancellationRequested();
                if (folder.Measurement is MeasurementState.Unknown or MeasurementState.Previous || !scan.CanUsePath(folder.Path))
                { skipped++; incomplete = true; continue; }
                var own = AddOwner(folder); names = Sum(names, own.Count); total = Sum(total, own.Bytes);
                foreach (var child in folder.Children) pending.Push(child);
            }
            return (names, total, incomplete);
        }
    }
    static long Sum(long a, long b) => FileTypePopulation.Sum(a, b);
}
