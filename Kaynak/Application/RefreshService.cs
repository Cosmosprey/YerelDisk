namespace YerelDisk;

internal interface IPreparedRefresh : IDisposable
{
    ScanOutcome Outcome { get; }
    RefreshAction Action { get; }
    RefreshReason Reason { get; }
    void ValidateForCommit(CancellationToken token);
}

internal interface IRefreshService
{
    Task<IPreparedRefresh> PrepareAsync(ScanResult previous, FolderEntry selected, ScanRequest request,
        IProgress<ScanProgress>? progress, CancellationToken token);
}

// Application owns this lease through filtering, summary preparation and commit.
internal sealed class NativeRefreshService : IRefreshService
{
    public Task<IPreparedRefresh> PrepareAsync(ScanResult previous, FolderEntry selected, ScanRequest request,
        IProgress<ScanProgress>? progress, CancellationToken token) => Task.Run<IPreparedRefresh>(() =>
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var limits = new RefreshMemoryLimits(request.RefreshIndexBudgetBytes, 512L << 20, 16L << 20);
        var lease = RefreshExecution.Prepare(previous, selected, request.FileLimit, request.Workers, limits,
            progress: progress, token: token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (lease.PreparedResult is not { } result)
                throw new IOException(lease.Reason == RefreshReason.AllocationFailed
                    ? "Bellek ayrılamadı. Önceki sonuç korundu; tam tarama başlatılmadı."
                    : "Klasör veya kök kimliği güvenle doğrulanamadı. Önceki sonuç korundu.");
            var candidates = ReviewRules.ForCurrentUser().Analyze(result);
            token.ThrowIfCancellationRequested();
            result.Elapsed = watch.Elapsed;
            return new Prepared(lease, new(result, candidates));
        }
        catch { lease.Dispose(); throw; }
    });

    sealed class Prepared(RefreshExecution lease, ScanOutcome outcome) : IPreparedRefresh
    {
        public ScanOutcome Outcome { get; } = outcome;
        public RefreshAction Action => lease.Action;
        public RefreshReason Reason => lease.Reason;
        public void ValidateForCommit(CancellationToken token) => lease.ValidateForCommit(token);
        public void Dispose() => lease.Dispose();
    }
}
