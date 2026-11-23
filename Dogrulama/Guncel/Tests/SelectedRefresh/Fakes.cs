using YerelDisk;
using YerelDisk.Presentation;

static class Data
{
    internal static readonly Guid Volume = Guid.Parse("613836fd-3a80-4eda-a52e-73a321b21079");
    internal static ScanOutcome Outcome(long selected = 10, bool mixed = false)
    {
        var root = new FolderEntry(@"C:\scope", null) { ObservedId = 1, LogicalBytes = selected + 20, AllocatedBytes = 8192, FileCount = 2 };
        root.Children.Add(new(root.Path + @"\a", root) { ObservedId = 2, LogicalBytes = selected, AllocatedBytes = 4096, FileCount = 1 });
        root.Children.Add(new(root.Path + @"\b", root) { ObservedId = 3, LogicalBytes = 20, AllocatedBytes = 4096, FileCount = 1 });
        var scan = new ScanResult(root) { Folders = 3, ExcludedPaths = [root.Path + @"\skip"], RootVolumeIds = new Dictionary<string, Guid> { [root.Path] = Volume } };
        if (mixed) scan.RefreshedAreas = Array.AsReadOnly(new[] { new RefreshAreaMeasurement(root.Path + @"\a", DateTimeOffset.UtcNow) });
        return new(scan, []);
    }
    internal static TaskCompletionSource<IPreparedRefresh> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
sealed class ScanFake : IScanService
{
    internal int Calls; internal ScanRequest? Last; internal Func<ScanOutcome> Next = () => Data.Outcome();
    public string[] NormalizeRoots(IEnumerable<string> paths) => paths.ToArray();
    public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token)
    { Calls++; Last = request; return Task.FromResult(Next()); }
}
sealed class RefreshFake : IRefreshService
{
    internal int Calls; internal FolderEntry? Target; internal ScanRequest? Request; internal IProgress<ScanProgress>? Progress;
    internal TaskCompletionSource<IPreparedRefresh>? Pending;
    internal Func<IPreparedRefresh> Next = () => new LeaseFake(Data.Outcome(12, true));
    public Task<IPreparedRefresh> PrepareAsync(ScanResult previous, FolderEntry selected, ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token)
    { Calls++; Target = selected; Request = request; Progress = progress; return Pending?.Task ?? Task.FromResult(Next()); }
}
sealed class LeaseFake(ScanOutcome outcome, Action<CancellationToken>? validate = null, RefreshReason reason = RefreshReason.ReadyToMerge) : IPreparedRefresh
{
    internal int Validations, Disposals;
    public ScanOutcome Outcome { get; } = outcome;
    public RefreshAction Action => Outcome.Scan.HasMixedMeasurementTimes ? RefreshAction.MergeSelected : RefreshAction.MeasureScope;
    public RefreshReason Reason => reason;
    public void ValidateForCommit(CancellationToken token) { Validations++; token.ThrowIfCancellationRequested(); validate?.Invoke(token); }
    public void Dispose() { Disposals++; }
}
sealed class InventoryFake : IInventoryProvider
{
    internal int Calls;
    public Task<InventorySnapshot> ReadAsync(CancellationToken token)
    { Calls++; return Task.FromResult(new InventorySnapshot(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], [new("fixture", InventoryReadState.Complete, 0, 0, 0)])); }
}
