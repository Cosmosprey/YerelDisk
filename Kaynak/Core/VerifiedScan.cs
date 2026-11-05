using System.Diagnostics;

namespace YerelDisk;

// Pin selected roots, then merge completed session snapshots. ScanRun performs
// bounded directory repairs before rebuilding totals/top-K from all observations.
internal static class VerifiedScan
{
    internal const int MaxAttempts = 3;

    internal static ScanResult Scan(IEnumerable<string> paths, IProgress<ScanProgress>? progress,
        CancellationToken token, int fileLimit, int workers, IEnumerable<string>? excludedPaths = null)
    {
        if (fileLimit < 1 || workers is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(fileLimit));
        string[] roots = MultiScan.NormalizeRootPaths(paths);
        var exclusions = ScanExclusions.Create(roots, excludedPaths);
        var states = roots.Select(path => new RootState(path)).ToArray();
        var watch = Stopwatch.StartNew();
        try
        {
            foreach (var state in states)
            {
                if (token.IsCancellationRequested) break;
                Measure(state);
            }
            foreach (var state in states)
            {
                state.Result ??= Missing(state.Path, "Taranmadı: tarama durduruldu.", cancelled: true);
                if (state.Pin != null && !state.Blocked)
                {
                    try { CheckLocalDrive(state.Path); state.Pin.VerifyBinding(state.Path); state.Pin.EnsureRootLocation(); }
                    catch (Exception ex) when (ex is ScopeChangedException || DiskScanner.IsFileError(ex)) { Block(state, ex.Message); }
                }
                var result = state.Result;
                result.RetainedFileLimit = fileLimit;
                result.ExcludedPaths = exclusions.Paths;

                if (result.NeedsRetry)
                {
                    if (result.UnverifiedPaths.Count == 0) MarkPrevious(result);
                    else if (result.Root.Measurement == MeasurementState.Measured) result.Root.Measurement = MeasurementState.Partial;
                    AddIssue(result, state.Path, token.IsCancellationRequested
                        ? "Yeniden doğrulama kullanıcı tarafından durduruldu; son ölçümün güncelliği doğrulanamadı."
                        : $"{result.RetryCount + 1} kontrol turundan sonra kararlı ölçüm alınamadı. Son ölçüm gösteriliyor; yeniden doğrulayın.");
                }
                result.Cancelled |= token.IsCancellationRequested;
                MarkUnsafeRecords(result);
            }
            var combined = Merge(states, fileLimit, watch.Elapsed, exclusions);
            exclusions.ApplyTo(combined);
            FileHints.Apply(combined);
            combined.Elapsed = watch.Elapsed;
            progress?.Report(new(combined.Root.FileCount, combined.Folders, combined.Root.LogicalBytes,
                combined.IssueCount, "Sonuç hazırlanıyor", watch.Elapsed, ScanPhase.Finalizing));
            return combined;
        }
        finally { foreach (var state in states) state.Pin?.Dispose(); }

        void Measure(RootState state)
        {
            try
            {
                CheckLocalDrive(state.Path);
                if (state.Pin == null)
                {
                    state.Pin = NativeDirectory.OpenRoot(state.Path, false);
                    state.Identity = NativeMetadata.Identity(state.Pin.Handle);
                }
                state.Pin.VerifyBinding(state.Path); state.Pin.EnsureRootLocation();

                long otherFiles = states.Where(s => !ReferenceEquals(s, state)).Sum(s => s.Result?.Root.FileCount ?? 0);
                long otherBytes = states.Where(s => !ReferenceEquals(s, state)).Sum(s => s.Result?.Root.LogicalBytes ?? 0);
                long otherFolders = states.Where(s => !ReferenceEquals(s, state)).Sum(s => s.Result?.Folders ?? 0);
                long otherIssues = states.Where(s => !ReferenceEquals(s, state)).Sum(s => s.Result?.IssueCount ?? 0);
                var forward = new ForwardProgress(p => progress?.Report(new(otherFiles + p.Files, otherFolders + p.Folders,
                    otherBytes + p.Bytes, otherIssues + p.Issues, p.Current, watch.Elapsed, p.Phase)));
                var next = new ScanRun(state.Path, forward, token, fileLimit, [], new ScanPolicy(state.Identity), exclusions).Execute(workers);
                next.RetainedFileLimit = fileLimit;
                state.Result = next;
                if (next.ScopeChanged || next.Failed) MarkPrevious(next);
                state.Blocked = next.ScopeChanged;
                state.History.Add($"{state.Path} · ilk tarama; {next.RetryCount} hedefli yenileme: " +
                    (next.ScopeChanged ? "Kök kimliği doğrulanamadı; yeni hedef izlenmedi." : next.NeedsRetry ? "Değişiklik algılandı. " + string.Join(" | ", next.Issues.Take(3).Select(i => i.Path + ": " + i.Reason)) : next.HasUnverified ? "Ölçülemeyen alanlar var." : "Değişiklik algılanmadı."));
            }
            catch (Exception ex) when (ex is ScopeChangedException || DiskScanner.IsFileError(ex)) { Block(state, ex.Message); }
        }
    }

    static void CheckLocalDrive(string path)
    {
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new IOException("Yerel sürücü kullanılamıyor veya desteklenmiyor; ağ / yeni hedef izlenmedi.");
    }

    static void Block(RootState state, string reason)
    {
        state.Blocked = true;
        state.Result ??= Missing(state.Path, reason);
        state.Result.ScopeChanged = true; state.Result.NeedsRetry = false;
        MarkPrevious(state.Result);
        AddIssue(state.Result, state.Path, "Kök yeniden doğrulanamadı; yeni hedef taranmadı. " + reason);
    }
    static ScanResult Missing(string path, string reason, bool cancelled = false)
    {
        var result = new ScanResult(new FolderEntry(path, null) { Measurement = MeasurementState.Unknown }) { Cancelled = cancelled, Failed = !cancelled, Folders = 1 };
        result.UnverifiedPaths.Add(path); AddIssue(result, path, reason); return result;
    }
    static void AddIssue(ScanResult result, string path, string reason)
    {
        result.IssueCount++; result.Root.IssueCount++;
        if (result.Issues.Count < ScanResult.IssueLimit) result.Issues.Add(new(path, reason));
    }
    static void MarkPrevious(ScanResult result)
    {
        result.UnverifiedPaths.Add(result.Root.Path);
        var pending = new Stack<FolderEntry>(); pending.Push(result.Root);
        while (pending.TryPop(out var folder))
        {
            if (folder.Measurement != MeasurementState.Unknown) folder.Measurement = MeasurementState.Previous;
            foreach (var child in folder.Children) pending.Push(child);
        }
        if (result.Root.FileCount == 0 && result.Root.LogicalBytes == 0) result.Root.Measurement = MeasurementState.Unknown;
        for (int i = 0; i < result.Files.Count; i++) result.Files[i] = result.Files[i] with { Unverified = true };
    }

    static void MarkUnsafeRecords(ScanResult result)
    {
        if (!result.HasUnverified) return;
        var safeDirectories = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<FolderEntry>(); pending.Push(result.Root);
        while (pending.TryPop(out var folder))
        {
            bool safe = result.CanUsePath(folder.Path); safeDirectories[folder.Path.TrimEnd('\\')] = safe;
            if (!safe && folder.Measurement != MeasurementState.Unknown) folder.Measurement = MeasurementState.Previous;
            foreach (var child in folder.Children) pending.Push(child);
        }
        for (int i = 0; i < result.Files.Count; i++)
        {
            var file = result.Files[i];
            string parent = Path.GetDirectoryName(file.Path)?.TrimEnd('\\') ?? "";
            if (!file.Unverified && (result.UnverifiedPaths.Contains(file.Path)
                || (safeDirectories.TryGetValue(parent, out bool safe) ? !safe : !result.CanUsePath(file.Path))))
                result.Files[i] = file with { Unverified = true };
        }
    }

    static ScanResult Merge(RootState[] states, int fileLimit, TimeSpan elapsed, ScanExclusions exclusions)
    {
        if (states.Length == 1)
        {
            var single = states[0].Result!;
            single.ObservedEvidence = ObservedFileEvidence.Capture([single], exclusions);
            single.PhysicalLinks.Clear(); single.PhysicalLinks.TrimExcess(); single.FileCandidates = null; single.Elapsed = elapsed;
            single.VerificationHistory.AddRange(states[0].History);
            return single;
        }
        var seenLinks = new Dictionary<(uint Volume, ulong Id), LinkAllocation>(
            IdentityCapacity.ForRoots(states.Select(state => state.Result!)));
        var sharedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stalePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refill = new HashSet<ScanResult>();
        foreach (var state in states)
        {
            var part = state.Result!;
            foreach (var link in part.PhysicalLinks)
            {
                if (seenLinks.TryAdd(link.Key, link.Value)) continue;
                var first = seenLinks[link.Key];
                // Claude 1.9.1: roots are measured one after another. A file with one link at both
                // observations was moved after the earlier root finished; that observation is stale.
                // Remove it from the earlier root's totals and list, and mark that folder unverified.
                if (!first.Linked && !link.Value.Linked && first.Name != null)
                {
                    string stale = Path.Combine(first.Owner.Path, first.Name);
                    stalePaths.Add(stale);
                    var earlier = states.Select(s => s.Result!).First(r => Contains(r.Root, first.Owner));
                    refill.Add(earlier);
                    for (FolderEntry? folder = first.Owner; folder != null; folder = folder.Parent)
                    {
                        folder.LogicalBytes -= first.Size; folder.AllocatedBytes -= first.Bytes; folder.FileCount--;
                        if (folder.Measurement == MeasurementState.Measured) folder.Measurement = MeasurementState.Partial;
                    }
                    earlier.UnverifiedPaths.Add(first.Owner.Path);
                    AddIssue(earlier, stale, "Ölçüm doğrulanamadı: bu dosya kökün taranmasından sonra başka bir seçili köke taşınmış görünüyor; bu konum toplamdan çıkarıldı.");
                    seenLinks[link.Key] = link.Value;
                    continue;
                }
                if (first.Name != null) sharedPaths.Add(Path.Combine(first.Owner.Path, first.Name));
                if (link.Value.Name != null) sharedPaths.Add(Path.Combine(link.Value.Owner.Path, link.Value.Name));
                part.HardLinkAliases++;
                for (FolderEntry? folder = link.Value.Owner; folder != null; folder = folder.Parent)
                    folder.AllocatedBytes -= link.Value.Bytes;
            }
            part.PhysicalLinks.Clear(); part.PhysicalLinks.TrimExcess();
            part.VerificationHistory.AddRange(state.History);
        }
        foreach (var part in refill) RefillCandidates(part, stalePaths, fileLimit);
        var observedEvidence = ObservedFileEvidence.Capture(states.Select(state => state.Result!), exclusions);
        foreach (var state in states) state.Result!.FileCandidates = null;
        var combined = new ScanResult(new FolderEntry($"Seçilen klasörler ({states.Length})", null)) { RetainedFileLimit = fileLimit, Elapsed = elapsed };
        combined.ObservedEvidence = observedEvidence;
        if (states.Any(s => s.Result!.Timings != null))
            combined.Timings = new(
                TimeSpan.FromTicks(states.Sum(s => s.Result!.Timings?.InitialScan.Ticks ?? 0)),
                TimeSpan.FromTicks(states.Sum(s => s.Result!.Timings?.Verification.Ticks ?? 0)),
                TimeSpan.FromTicks(states.Sum(s => s.Result!.Timings?.Refresh.Ticks ?? 0)));
        combined.RootPaths.Clear(); combined.RootPaths.AddRange(states.Select(s => s.Path));
        foreach (var state in states)
        {
            var part = state.Result!;
            part.Root.Parent = combined.Root; combined.Root.Children.Add(part.Root);
            combined.Root.LogicalBytes += part.Root.LogicalBytes; combined.Root.AllocatedBytes += part.Root.AllocatedBytes;
            combined.Root.FileCount += part.Root.FileCount; combined.Root.IssueCount += part.Root.IssueCount;
            combined.Folders += part.Folders; combined.IssueCount += part.IssueCount;
            combined.UnknownAllocation += part.UnknownAllocation; combined.HardLinkAliases += part.HardLinkAliases;
            combined.Workers = Math.Max(combined.Workers, part.Workers);
            combined.Failed |= part.Failed; combined.Cancelled |= part.Cancelled; combined.ScopeChanged |= part.ScopeChanged;
            combined.NeedsRetry |= part.NeedsRetry; combined.RetryCount += part.RetryCount;
            combined.RefreshedFolders += part.RefreshedFolders; combined.VerifiedFolders += part.VerifiedFolders;
            combined.VerificationRounds += part.VerificationRounds;
            combined.UnverifiedPaths.UnionWith(part.UnverifiedPaths); combined.VerificationHistory.AddRange(part.VerificationHistory);
            combined.Issues.AddRange(part.Issues.Take(ScanResult.IssueLimit - combined.Issues.Count));
            if (part.Root.Measurement != MeasurementState.Measured) combined.Root.Measurement = MeasurementState.Partial;
        }
        combined.Files = states.SelectMany(s => s.Result!.Files).Where(file => !stalePaths.Contains(file.Path))
            .Select(file => sharedPaths.Contains(file.Path) ? file with { HardLinked = true } : file).OrderByDescending(f => f.Size)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(fileLimit).ToList();
        return combined;
    }
    static void RefillCandidates(ScanResult part, HashSet<string> stalePaths, int limit)
    {
        if (part.FileCandidates == null) return;
        var heap = new PriorityQueue<FileEntry, long>();
        foreach (var (owner, file) in part.FileCandidates)
        {
            if (heap.Count == limit && heap.TryPeek(out _, out long floor) && file.Size <= floor) continue;
            string path = Path.Combine(owner.Path, file.Name);
            if (stalePaths.Contains(path)) continue;
            var entry = new FileEntry(path, file.Size, file.Allocated, file.Modified, file.HardLinked, file.Note,
                file.ExtraStreams, file.ExtraStreamBytes, file.Unverified || !part.CanUsePath(path));
            if (heap.Count == limit) heap.Dequeue();
            heap.Enqueue(entry, entry.Size);
        }
        part.Files = heap.UnorderedItems.Select(item => item.Element).OrderByDescending(file => file.Size)
            .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
    static bool Contains(FolderEntry root, FolderEntry folder)
    {
        for (FolderEntry? current = folder; current != null; current = current.Parent) if (ReferenceEquals(current, root)) return true;
        return false;
    }
    sealed class RootState(string path)
    {
        internal string Path { get; } = path;
        internal NativeDirectory? Pin;
        internal (uint Volume, ulong Id) Identity;
        internal ScanResult? Result;

        internal bool Blocked;
        internal List<string> History { get; } = [];
    }
    sealed class ForwardProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    { public void Report(ScanProgress value) => report(value); }
}
