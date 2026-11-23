using System.IO;

namespace YerelDisk;

internal sealed record FolderChange(string Path, long? Before, long? After, bool CanEnter)
{
    public long? Difference => After - Before;
    public string BeforeText => Before is long before ? PresentationText.Size(before) : "—";
    public string AfterText => After is long after ? PresentationText.Size(after) : "—";
    public string DifferenceText => Difference is long delta
        ? (delta > 0 ? "+" : delta < 0 ? "−" : "") + PresentationText.Size(Math.Abs(delta))
        : "Karşılaştırılamadı";
}

internal sealed class FolderComparison
{
    readonly Index before;
    readonly Index after;
    internal FolderMeasurements Reference { get; }
    internal FolderMeasurements Current { get; }
    internal int UncomparableCount { get; }
    internal bool HasUnverifiedVolumeIdentity { get; }
    internal IReadOnlyList<FolderChange> Largest { get; }

    internal FolderComparison(FolderMeasurements reference, FolderMeasurements current)
    {
        Reference = reference;
        Current = current;
        before = new(reference);
        after = new(current);
        if (!before.Roots.SetEquals(after.Roots) || !before.Exclusions.SetEquals(after.Exclusions))
            throw new InvalidDataException("Tarama kapsamları farklı. İki ölçüm için aynı kökleri ve hariç tutulan yolları kullanın.");
        foreach (string root in before.Roots)
        {
            if (!before.Volumes.TryGetValue(root, out var priorVolume) || !after.Volumes.TryGetValue(root, out var currentVolume))
                HasUnverifiedVolumeIdentity = true;
            else if (priorVolume != currentVolume)
                throw new InvalidDataException($"Sürücü birimi değişmiş: {root}. Bu konum için yeni bir referans kaydedin.");
        }

        const int largestLimit = 100;
        var ranking = new PriorityQueue<Pair, Pair>(Comparer<Pair>.Create((a, b) =>
        {
            int magnitude = Math.Abs(a.Difference!.Value).CompareTo(Math.Abs(b.Difference!.Value));
            return magnitude != 0 ? magnitude : StringComparer.Ordinal.Compare(b.Path, a.Path);
        }));
        var pending = new Stack<IEnumerator<Pair>>();
        pending.Push(Roots().GetEnumerator());
        while (pending.TryPeek(out var branch))
        {
            if (!branch.MoveNext()) { pending.Pop().Dispose(); continue; }
            var pair = branch.Current;
            if (HasChildren(pair.Path)) pending.Push(ChildPairs(pair).GetEnumerator());
            if (pair.Difference is not long delta) { UncomparableCount++; continue; }
            if (delta == 0) continue;
            if (ranking.Count == largestLimit) ranking.EnqueueDequeue(pair, pair);
            else ranking.Enqueue(pair, pair);
        }
        var largest = new FolderChange[ranking.Count];
        for (int i = largest.Length - 1; i >= 0; i--) largest[i] = Row(ranking.Dequeue());
        Largest = largest;
    }

    internal IReadOnlyList<FolderChange> At(string? parent)
    {
        var pairs = parent == null ? Roots() : ChildPairs(new(parent, before.SizeAt(parent), after.SizeAt(parent)));
        return pairs.Select(Row)
            .OrderByDescending(row => row.Difference is long delta ? Math.Abs(delta) : -1)
            .ThenBy(row => row.Path, StringComparer.Ordinal).ToArray();
    }

    IEnumerable<Pair> Roots()
        => before.Roots.Select(path => new Pair(path, MeasuredSize(before.Folders[path]), MeasuredSize(after.Folders[path])));

    IEnumerable<Pair> ChildPairs(Pair parent)
    {
        var earlier = before.Children[parent.Path];
        var later = after.Children[parent.Path];
        HashSet<string>? earlierNames = null, laterNames = null;
        bool earlierHasChildren = before.Children.Contains(parent.Path), laterHasChildren = after.Children.Contains(parent.Path);
        // A verified parent proves absence only when the other scan also lacks any
        // spelling variant. A missing parent's evidence is carried down its subtree.
        foreach (string path in earlier)
        {
            long? afterSize = after.Folders.TryGetValue(path, out var match) ? MeasuredSize(match)
                : MissingSize(path, parent.After, later, laterHasChildren, ref laterNames);
            yield return new(path, MeasuredSize(before.Folders[path]), afterSize);
        }
        foreach (string path in later)
        {
            if (before.Folders.ContainsKey(path)) continue;
            long? beforeSize = MissingSize(path, parent.Before, earlier, earlierHasChildren, ref earlierNames);
            yield return new(path, beforeSize, MeasuredSize(after.Folders[path]));
        }
    }

    static long? MissingSize(string path, long? parent, IEnumerable<string> siblings, bool hasSiblings, ref HashSet<string>? names)
    {
        if (!parent.HasValue) return null;
        if (!hasSiblings) return 0;
        names ??= siblings.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains(path) ? null : 0;
    }

    static long? MeasuredSize(FolderMeasurement folder) => folder.Reliable ? folder.Bytes : null;
    bool HasChildren(string path) => before.Children.Contains(path) || after.Children.Contains(path);
    FolderChange Row(Pair pair) => new(pair.Path, pair.Before, pair.After, HasChildren(pair.Path));

    readonly record struct Pair(string Path, long? Before, long? After)
    {
        internal long? Difference => After - Before;
    }

    sealed class Index
    {
        internal HashSet<string> Roots { get; }
        internal HashSet<string> Exclusions { get; }
        internal Dictionary<string, Guid> Volumes { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, FolderMeasurement> Folders { get; }
        internal ILookup<string, string> Children { get; }

        internal Index(FolderMeasurements data)
        {
            if (data.Format is not (1 or FolderMeasurements.CurrentFormat)
                || (data.Format == 1) != (data.RootVolumes == null) || data.MeasuredAt == default
                || data.Roots is not { Length: > 0 } || data.Exclusions == null
                || data.Folders is not { Length: > 0 and <= FolderMeasurements.MaxFolders })
                throw Invalid();
            Roots = Paths(data.Roots);
            Exclusions = Paths(data.Exclusions, StringComparer.OrdinalIgnoreCase);
            foreach (var volume in data.RootVolumes ?? [])
                if (volume == null || volume.Root == null || volume.Id == Guid.Empty || !Roots.Contains(volume.Root)
                    || !Volumes.TryAdd(volume.Root, volume.Id))
                    throw Invalid();
            var lexicalRoots = Roots.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Roots.Count != lexicalRoots.Count) throw Invalid();
            foreach (string root in Roots)
                if (HasAncestor(root, lexicalRoots)) throw Invalid();
            foreach (string excluded in Exclusions)
                if (!HasAncestor(excluded, lexicalRoots) || HasAncestor(excluded, Exclusions) || lexicalRoots.Contains(excluded))
                    throw Invalid();

            Folders = new(data.Folders.Length, StringComparer.Ordinal);
            foreach (var folder in data.Folders)
                if (folder == null || folder.Bytes < 0 || !ValidPath(folder.Path) || !Folders.TryAdd(folder.Path, folder)
                    || Exclusions.Contains(folder.Path))
                    throw Invalid();
            if (!Roots.IsSubsetOf(Folders.Keys)) throw Invalid();

            var lookup = Folders.GetAlternateLookup<ReadOnlySpan<char>>();
            Children = data.Folders.Where(folder => !Roots.Contains(folder.Path))
                .ToLookup(folder =>
                {
                    var path = folder.Path.AsSpan();
                    int length = FolderMeasurements.ParentLength(path);
                    return length > 0 && lookup.TryGetValue(path[..length], out var parent) ? parent.Path : throw Invalid();
                }, folder => folder.Path, StringComparer.Ordinal);
            foreach (var parent in data.Folders)
            {
                if (!parent.Reliable) continue;
                long remaining = parent.Bytes;
                foreach (string child in Children[parent.Path])
                {
                    var item = Folders[child];
                    if (!item.Reliable) continue;
                    if (item.Bytes > remaining) throw Invalid();
                    remaining -= item.Bytes;
                }
            }
        }

        internal long? SizeAt(string path)
        {
            if (Folders.TryGetValue(path, out var folder)) return MeasuredSize(folder);
            var lookup = Folders.GetAlternateLookup<ReadOnlySpan<char>>();
            var missing = path.AsSpan();
            for (int length = FolderMeasurements.ParentLength(missing); length > 0; length = FolderMeasurements.ParentLength(missing))
            {
                var parent = missing[..length];
                if (lookup.TryGetValue(parent, out folder))
                {
                    if (!folder.Reliable) return null;
                    foreach (string sibling in Children[folder.Path])
                        if (missing.Equals(sibling, StringComparison.OrdinalIgnoreCase)) return null;
                    return 0;
                }
                missing = parent;
            }
            return null;
        }

        static HashSet<string> Paths(string[] paths, StringComparer? comparer = null)
        {
            var result = new HashSet<string>(comparer ?? StringComparer.Ordinal);
            foreach (string path in paths)
                if (!ValidPath(path) || !result.Add(path)) throw Invalid();
            return result;
        }

        static bool HasAncestor(string path, HashSet<string> candidates)
        {
            var lookup = candidates.GetAlternateLookup<ReadOnlySpan<char>>();
            var ancestor = path.AsSpan();
            for (int length = FolderMeasurements.ParentLength(ancestor); length > 0; length = FolderMeasurements.ParentLength(ancestor))
            {
                ancestor = ancestor[..length];
                if (lookup.Contains(ancestor)) return true;
            }
            return false;
        }

        static bool ValidPath(string? path)
        {
            if (path == null || !FolderMeasurements.IsLocalPath(path)) return false;
            if (path.Length == 3) return true;
            var relative = path.AsSpan(3);
            return relative[0] != '\\' && relative[^1] != '\\'
                && !relative.Contains("\\\\", StringComparison.Ordinal)
                && !relative.Contains("\\.\\", StringComparison.Ordinal)
                && !relative.Contains("\\..\\", StringComparison.Ordinal)
                && !relative.StartsWith(".\\", StringComparison.Ordinal) && !relative.StartsWith("..\\", StringComparison.Ordinal)
                && !relative.EndsWith("\\.", StringComparison.Ordinal) && !relative.EndsWith("\\..", StringComparison.Ordinal)
                && !relative.SequenceEqual(".") && !relative.SequenceEqual("..");
        }

        static InvalidDataException Invalid() => new("Ölçüm dosyasının biçimi veya klasör yapısı geçersiz.");
    }
}
