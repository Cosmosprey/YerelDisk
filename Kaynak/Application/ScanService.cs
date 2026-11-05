namespace YerelDisk;

// UI-independent orchestration. Inputs are snapshotted before dispatching work.
internal sealed class ScanRequest
{
    readonly string[] roots;
    readonly string[] exclusions;
    internal IReadOnlyList<string> Roots => Array.AsReadOnly(roots);
    internal IReadOnlyList<string> Exclusions => Array.AsReadOnly(exclusions);
    internal int Workers { get; }
    internal int FileLimit { get; }
    internal ScanRequest(IEnumerable<string> paths, int workers, int fileLimit, IEnumerable<string>? excludedPaths = null)
    {
        roots = paths.ToArray();
        exclusions = excludedPaths?.ToArray() ?? [];
        if (roots.Length == 0) throw new ArgumentException("Önce klasör seçin.");
        if (workers is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(workers));
        if (fileLimit < 1) throw new ArgumentOutOfRangeException(nameof(fileLimit));
        Workers = workers; FileLimit = fileLimit;
    }
}

internal sealed record ScanOutcome(ScanResult Scan, List<ReviewCandidate> Candidates);

internal interface IScanService
{
    string[] NormalizeRoots(IEnumerable<string> paths);
    string[] PrepareRootsForScan(IEnumerable<string> paths) => NormalizeRoots(paths);
    Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token);
}

internal sealed class NativeScanService : IScanService
{
    public string[] NormalizeRoots(IEnumerable<string> paths) => MultiScan.NormalizeRoots(paths);
    public string[] PrepareRootsForScan(IEnumerable<string> paths) => MultiScan.NormalizeRootPaths(paths);
    public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token)
        // Do not pass token to Task.Run: the engine must observe an already cancelled
        // request itself so that it can return an explicitly partial ScanResult.
        => Task.Run(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = VerifiedScan.Scan(request.Roots, progress, token, request.FileLimit, request.Workers, request.Exclusions);
            var candidates = ReviewRules.ForCurrentUser().Analyze(result);
            result.Elapsed = watch.Elapsed;
            return new ScanOutcome(result, candidates);
        });
}
