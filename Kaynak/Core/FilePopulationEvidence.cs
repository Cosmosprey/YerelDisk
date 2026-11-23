namespace YerelDisk;

internal readonly record struct FileTypePopulation(long Count, long Bytes)
{
    internal FileTypePopulation Add(long count, long bytes) => new(Sum(Count, count), Sum(Bytes, bytes));
    internal static long Sum(long a, long b) => b > long.MaxValue - a ? long.MaxValue : a + b;
}

// One bounded summary per direct owner, never another per-file snapshot.
internal sealed class FolderFilePopulation
{
    internal long Count, Bytes, Small, Empty;
    internal FileTypePopulation Other;
    string? firstExtension;
    FileTypePopulation first;
    Dictionary<string, FileTypePopulation>? rest;
    internal int TypeCount => (firstExtension == null ? 0 : 1) + (rest?.Count ?? 0);
    internal IEnumerable<KeyValuePair<string, FileTypePopulation>> Types
    {
        get
        {
            if (firstExtension != null) yield return new(firstExtension, first);
            if (rest != null) foreach (var item in rest) yield return item;
        }
    }

    internal void Add(FileObservation file, FilePopulationEvidence.Builder builder)
    {
        Count++; Bytes = FileTypePopulation.Sum(Bytes, file.Size);
        if (file.Size <= FilePopulationEvidence.SmallLimit) Small++;
        if (file.Size == 0) Empty++;
        var extension = Path.GetExtension(file.Name.AsSpan());
        if (extension.Length <= FilePopulationEvidence.MaxExtensionLength)
        {
            if (firstExtension != null && extension.Equals(firstExtension.AsSpan(), StringComparison.OrdinalIgnoreCase))
            { first = first.Add(1, file.Size); return; }
            if (rest != null && rest.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(extension, out var old))
            { rest.GetAlternateLookup<ReadOnlySpan<char>>()[extension] = old.Add(1, file.Size); return; }
            if (TypeCount < builder.MaxTypesPerOwner && builder.ReserveType())
            {
                string key = builder.ExtensionKey(extension);
                if (firstExtension == null) { firstExtension = key; first = new(1, file.Size); }
                else (rest ??= new(StringComparer.OrdinalIgnoreCase)).Add(key, new(1, file.Size));
                return;
            }
        }
        Other = Other.Add(1, file.Size);
    }
}

internal sealed class FilePopulationEvidence
{
    internal const long SmallLimit = 4096;
    internal const int MaxExtensionLength = 32;
    internal const int OwnerLimit = 100_000, TypeLimit = 100_000, TypesPerOwner = 32;
    readonly Dictionary<FolderEntry, FolderFilePopulation> owners = [];
    internal IReadOnlyDictionary<FolderEntry, FolderFilePopulation> Owners => owners;
    internal bool HasSnapshots { get; private set; }
    internal bool OwnerLimitReached { get; private set; }

    internal sealed class Builder(int maxOwners = OwnerLimit, int maxTypes = TypeLimit, int maxTypesPerOwner = TypesPerOwner)
    {
        readonly FilePopulationEvidence result = new();
        ScanResult? source;
        ScanExclusions? exclusions;
        readonly HashSet<string> uncertainOwners = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> extensionKeys = new(StringComparer.OrdinalIgnoreCase);
        FolderEntry? lastOwner;
        bool ownerUsable;
        int usedTypes;
        internal int MaxTypesPerOwner => maxTypesPerOwner;
        internal bool ReserveType() => usedTypes < maxTypes && ++usedTypes > 0;
        internal string ExtensionKey(ReadOnlySpan<char> extension)
        {
            if (extensionKeys.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(extension, out string? key)) return key;
            key = extension.ToString().ToLowerInvariant(); extensionKeys.Add(key, key); return key;
        }
        internal void BeginSource(ScanResult scan, ScanExclusions? excluded)
        {
            source = scan; exclusions = excluded; lastOwner = null;
            result.HasSnapshots = true; uncertainOwners.Clear();
            // Full paths are constructed only in owners with an uncertain entry.
            foreach (string path in scan.UnverifiedPaths)
                if (Path.GetDirectoryName(path.TrimEnd('\\')) is { } parent) uncertainOwners.Add(parent.TrimEnd('\\'));
        }
        internal void Add(FolderEntry owner, FileObservation file)
        {
            if (source == null || file.Unverified || file.Size < 0 || !CanonicalName(file.Name)) return;
            if (!IsOwnerUsable(owner) || exclusions?.SkipsEntry(owner.Path, file.Name) == true) return;
            if (uncertainOwners.Contains(owner.Path.TrimEnd('\\'))
                && source.UnverifiedPaths.Contains(Path.Combine(owner.Path, file.Name))) return;
            if (!result.owners.TryGetValue(owner, out var summary))
            {
                if (result.owners.Count >= maxOwners) { result.OwnerLimitReached = true; return; }
                result.owners.Add(owner, summary = new());
            }
            summary.Add(file, this);
        }
        internal bool IsOwnerUsable(FolderEntry owner)
        {
            if (source == null) return false;
            if (!ReferenceEquals(lastOwner, owner))
            {
                lastOwner = owner;
                ownerUsable = owner.Measurement is not (MeasurementState.Unknown or MeasurementState.Previous)
                    && ReviewRules.CanonicalLocal(owner.Path) != null
                    && WithinRoots(source.RootPaths, owner.Path)
                    && source.CanUsePath(owner.Path);
            }
            return ownerUsable;
        }
        internal FilePopulationEvidence Complete()
        {
            source = null; exclusions = null; lastOwner = null; uncertainOwners.Clear(); extensionKeys.Clear();
            return result;
        }
        static bool CanonicalName(string name) => name.Length > 0 && !name.EndsWith('.') && !name.EndsWith(' ')
            && name.AsSpan().IndexOfAny('\\', '/', ':') < 0 && !name.Contains('\0');
        static bool WithinRoots(IReadOnlyList<string> roots, string path)
        {
            for (int i = 0; i < roots.Count; i++) if (ScanExclusions.SameOrBelow(path, roots[i])) return true;
            return false;
        }
    }
}
