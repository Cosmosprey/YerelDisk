using System.Runtime.CompilerServices;

namespace YerelDisk;

internal enum RefreshIndexState { Captured, BudgetExceeded, IncompleteScan, MissingMetadata, IdentityUnavailable, InconsistentMetadata, Cancelled, AllocationFailed }
internal readonly record struct RefreshRoot(string Path, uint Volume, ulong Id, Guid? VolumeId);
internal readonly record struct RefreshDirectoryData(string Path, string? ParentPath, ulong Id,
    FileMetadata OwnStreams, DirectoryFingerprint Fingerprint, MeasurementState Measurement,
    bool Enumerated, long LogicalBytes, long AllocatedBytes, long FileCount, int IssueCount);
internal readonly record struct RefreshDirectory(int Number, RefreshDirectoryData Data, int FileStart, int FileCount);

// Complete capture of OBSERVATIONS, not permission to refresh a subtree. A
// later planner must still check uncertainty, identities and boundary links.
// No handles, ScanNodes, FolderEntries, full file paths or disk writes retained.
internal sealed class RefreshIndex
{
    internal const long MaxBudgetBytes = 512L * 1024 * 1024;
    const int PageLength = 64;
    const long HeaderCharge = 512;
    readonly Pages<RefreshRoot>? roots;
    readonly Pages<string>? exclusions;
    readonly Pages<RefreshDirectory>? directories;
    readonly Pages<FileObservation>? files;
    internal RefreshIndexState State { get; }
    internal bool Captured => State == RefreshIndexState.Captured;
    internal bool RequiresFullScan => !Captured;
    internal long BudgetBytes { get; }
    // Conservative retained-data accounting; not a process RAM/GC peak limit.
    // On failure no payload pages remain reachable through this descriptor.
    internal long MetadataBytes { get; }
    internal bool HasUncertainty { get; }
    internal int DirectoryCount => directories?.Count ?? 0;
    internal int FileCount => files?.Count ?? 0;
    internal IEnumerable<RefreshRoot> Roots => roots?.Values() ?? [];
    internal IEnumerable<string> Exclusions => exclusions?.Values() ?? [];
    internal IEnumerable<RefreshDirectory> Directories => directories?.Values() ?? [];
    internal IEnumerable<FileObservation> Files => files?.Values() ?? [];
    // Each slice keeps its starting page, not an array copy or a full-path list.
    // Building every slice advances one cursor: no repeated Skip(start) scans.
    internal IEnumerable<(RefreshDirectory Directory, IEnumerable<FileObservation> Files)> Owners()
    {
        if (files == null || directories == null) yield break;
        var cursor = files.NewCursor(); int position = 0;
        foreach (var directory in directories.Values())
        {
            if (directory.FileStart != position) throw new InvalidDataException("İndeks aralığı tutarsız.");
            yield return (directory, cursor.Take(directory.FileCount));
            position += directory.FileCount;
        }
        if (position != files.Count) throw new InvalidDataException("İndeks dosya sayısı tutarsız.");
    }

    RefreshIndex(RefreshIndexState state, long budget) { State = state; BudgetBytes = budget; }
    RefreshIndex(Builder b)
    {
        State = RefreshIndexState.Captured; BudgetBytes = b.Budget.Limit; MetadataBytes = b.Budget.Used;
        roots = b.Roots; exclusions = b.Exclusions; directories = b.Directories; files = b.Files;
        HasUncertainty = b.Uncertain;
    }
    internal static void ValidateBudget(long bytes)
    { if (bytes < 0 || bytes > MaxBudgetBytes) throw new ArgumentOutOfRangeException(nameof(bytes), "İndeks bütçesi 0–512 MiB aralığında olmalı."); }

    internal static RefreshIndex? Capture(IEnumerable<ScanResult> sources, IEnumerable<RefreshRoot> scope,
        IEnumerable<string> omitted, long budgetBytes, CancellationToken token = default)
    {
        ValidateBudget(budgetBytes);
        if (budgetBytes == 0) return null; // Do not enumerate anything by default.
        if (token.IsCancellationRequested) return new(RefreshIndexState.Cancelled, budgetBytes);
        var b = new Builder(budgetBytes);
        RefreshIndex Failed(RefreshIndexState state) { b.Clear(); return new(state, budgetBytes); }
        try
        {
            if (!b.Budget.Charge(HeaderCharge)) return Failed(RefreshIndexState.BudgetExceeded);
            foreach (var root in scope)
            {
                if (token.IsCancellationRequested) return Failed(RefreshIndexState.Cancelled);
                if (root.Id == 0 || root.VolumeId is null || root.VolumeId == Guid.Empty) return Failed(RefreshIndexState.IdentityUnavailable);
                if (!b.Budget.Text(root.Path) || !b.Roots.Add(root)) return Failed(RefreshIndexState.BudgetExceeded);
            }
            if (b.Roots.Count == 0) return Failed(RefreshIndexState.MissingMetadata);
            foreach (string path in omitted)
            {
                if (token.IsCancellationRequested) return Failed(RefreshIndexState.Cancelled);
                if (!b.Budget.Text(path) || !b.Exclusions.Add(path)) return Failed(RefreshIndexState.BudgetExceeded);
            }
            using var expectedRoots = b.Roots.Values().GetEnumerator();
            foreach (var source in sources)
            {
                if (token.IsCancellationRequested) return Failed(RefreshIndexState.Cancelled);
                if (!expectedRoots.MoveNext() || expectedRoots.Current.Path != source.Root.Path
                    || expectedRoots.Current.Id != source.Root.ObservedId) return Failed(RefreshIndexState.InconsistentMetadata);
                if (source.Cancelled || source.Failed || source.ScopeChanged || source.NeedsRetry) return Failed(RefreshIndexState.IncompleteScan);
                if (source.RefreshDirectoryCandidates == null || source.FileCandidates == null) return Failed(RefreshIndexState.MissingMetadata);
                b.Uncertain |= source.HasUnverified || source.IssueCount != 0 || source.UnknownAllocation != 0;
                int start = b.Files.Count, directoryStart = b.Directories.Count;
                using var entries = source.FileCandidates.GetEnumerator();
                bool pending = entries.MoveNext(); bool first = true;
                foreach (var (owner, metadata) in source.RefreshDirectoryCandidates)
                {
                    if (token.IsCancellationRequested) return Failed(RefreshIndexState.Cancelled);
                    if (metadata.Path != owner.Path || metadata.Id != owner.ObservedId || !ScanExclusions.SameOrBelow(metadata.Path, source.Root.Path)
                        || (!first && (owner.Parent == null || metadata.ParentPath != owner.Parent.Path))
                        || (first && (!ReferenceEquals(owner, source.Root) || metadata.ParentPath != null)))
                        return Failed(RefreshIndexState.InconsistentMetadata);
                    first = false;
                    b.Uncertain |= metadata.Measurement != MeasurementState.Measured || !metadata.Enumerated
                        || !metadata.OwnStreams.StreamsKnown || !string.IsNullOrEmpty(metadata.OwnStreams.StreamError);
                    if (!b.Budget.Text(metadata.Path) || !b.Budget.Text(metadata.ParentPath) || !b.Budget.Text(metadata.OwnStreams.StreamError)) return Failed(RefreshIndexState.BudgetExceeded);
                    int ownStart = b.Files.Count;
                    while (pending && ReferenceEquals(entries.Current.Owner, owner))
                    {
                        if (token.IsCancellationRequested) return Failed(RefreshIndexState.Cancelled);
                        var file = entries.Current.File;
                        if (!CanonicalName(file.Name)) return Failed(RefreshIndexState.InconsistentMetadata);
                        b.Uncertain |= file.Unverified || !file.Allocated.HasValue || file.Id == 0;
                        if (!b.Budget.Text(file.Name) || !b.Budget.Text(file.Note) || !b.Files.Add(file)) return Failed(RefreshIndexState.BudgetExceeded);
                        pending = entries.MoveNext();
                    }
                    if (!b.Directories.Add(new(b.Directories.Count, metadata, ownStart, b.Files.Count - ownStart))) return Failed(RefreshIndexState.BudgetExceeded);
                }
                // Never label a truncated pool, cross-root stale observation,
                // missing owner or unknown iterator order as a complete cache.
                if (first || pending || b.Files.Count - start != source.Root.FileCount || b.Directories.Count - directoryStart != source.Folders) return Failed(RefreshIndexState.InconsistentMetadata);
            }
            if (expectedRoots.MoveNext()) return Failed(RefreshIndexState.InconsistentMetadata);
            if (token.IsCancellationRequested) return Failed(RefreshIndexState.Cancelled);
            return new(b);
        }
        catch (OutOfMemoryException) { return Failed(RefreshIndexState.AllocationFailed); }
    }
    static bool CanonicalName(string name) => name.Length > 0 && name is not "." and not ".."
        && !name.EndsWith('.') && !name.EndsWith(' ') && name.AsSpan().IndexOfAny('\\', '/', ':') < 0 && !name.Contains('\0');

    sealed class Budget(long limit)
    {
        internal long Limit { get; } = limit;
        internal long Used { get; private set; }
        internal bool Charge(long bytes) { if (bytes > Limit - Used) return false; Used += bytes; return true; }
        // Charge shared strings again: deliberately conservative and no global
        // interning/dedup table that could grow outside the budget.
        internal bool Text(string? text) => text == null || Charge((26L + 2L * text.Length + 7) & ~7L);
    }
    sealed class Pages<T>(Budget budget)
    {
        internal sealed class Page
        {
            internal readonly T[] Items = new T[PageLength];
            internal int Count;
            internal Page? Next;
        }
        Page? head, tail;
        internal int Count { get; private set; }
        internal bool Add(T item)
        {
            if (tail == null || tail.Count == PageLength)
            {
                long bytes = 64 + ((32L + (long)Unsafe.SizeOf<T>() * PageLength + 7) & ~7L);
                if (!budget.Charge(bytes)) return false;
                var next = new Page();
                if (tail == null) head = next; else tail.Next = next;
                tail = next;
            }
            tail.Items[tail.Count++] = item; Count++; return true;
        }
        internal IEnumerable<T> Values()
        { for (var page = head; page != null; page = page.Next) for (int i = 0; i < page.Count; i++) yield return page.Items[i]; }
        internal Cursor NewCursor() => new(head);
        internal sealed class Cursor(Page? first)
        {
            Page? page = first; int offset;
            internal IEnumerable<T> Take(int count)
            {
                if (count < 0) throw new InvalidDataException();
                Page? start = page; int at = offset, remaining = count;
                while (remaining > 0)
                {
                    if (page == null) throw new InvalidDataException("İndeks kesilmiş.");
                    int step = Math.Min(remaining, page.Count - offset);
                    remaining -= step; offset += step;
                    if (offset == page.Count) { page = page.Next; offset = 0; }
                }
                return Read(start, at, count);
            }
            static IEnumerable<T> Read(Page? page, int offset, int count)
            {
                while (count-- > 0)
                {
                    if (page == null) throw new InvalidDataException();
                    yield return page.Items[offset++];
                    if (offset == page.Count) { page = page.Next; offset = 0; }
                }
            }
        }
        internal void Clear() { head = tail = null; Count = 0; }
    }
    sealed class Builder
    {
        internal readonly Budget Budget;
        internal readonly Pages<RefreshRoot> Roots;
        internal readonly Pages<string> Exclusions;
        internal readonly Pages<RefreshDirectory> Directories;
        internal readonly Pages<FileObservation> Files;
        internal bool Uncertain;
        internal Builder(long limit)
        { Budget = new(limit); Roots = new(Budget); Exclusions = new(Budget); Directories = new(Budget); Files = new(Budget); }
        internal void Clear() { Roots.Clear(); Exclusions.Clear(); Directories.Clear(); Files.Clear(); }
    }
}
