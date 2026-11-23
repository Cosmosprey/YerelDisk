using System.Diagnostics;

namespace YerelDisk;

// Owns root pins through result preparation. Application must keep this lease
// alive through its own query/summary preparation, ValidateForCommit, and publish.
internal sealed class RefreshExecution : IDisposable
{
    readonly CancellationToken preparationToken;
    RefreshExecution(CancellationToken token) { preparationToken = token; }
    readonly List<(RefreshRoot Identity, NativeDirectory Pin)> roots = [];
    NativeDirectory? selectedPin;
    string? selectedPath;
    bool disposed;
    ScanResult? prepared;
    internal ScanResult? PreparedResult => prepared;
    internal RefreshAction Action { get; private set; } = RefreshAction.KeepPrevious;
    internal RefreshReason Reason { get; private set; } = RefreshReason.RootIdentityUnavailable;
    internal string Error { get; private set; } = "";
    internal bool IsIncremental => prepared?.HasMixedMeasurementTimes == true;

    internal static RefreshExecution Prepare(ScanResult previous, FolderEntry selected, int fileLimit, int workers,
        RefreshMemoryLimits? limits = null, long scratchBudgetBytes = RefreshRebuild.DefaultScratchBudget,
        IProgress<ScanProgress>? progress = null, CancellationToken token = default)
    {
        if (fileLimit < 1 || workers is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(fileLimit));
        if (scratchBudgetBytes is < 0 or > (2L << 30)) throw new ArgumentOutOfRangeException(nameof(scratchBudgetBytes));
        limits ??= RefreshMemoryLimits.Default;
        string[] scopeRoots = previous.RootPaths.ToArray(), scopeExclusions = previous.ExcludedPaths.ToArray();
        var lease = new RefreshExecution(token); var watch = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            // Missing old identities cannot authorize opening a replacement root.
            foreach (string path in scopeRoots)
            {
                var old = previous.Root.Path == path ? previous.Root : previous.Root.Children.FirstOrDefault(folder => folder.Path == path);
                if (old?.ObservedId is not > 0 || previous.RootVolumeIds?.TryGetValue(path, out var volume) != true || volume == Guid.Empty)
                    return lease.Stop(RefreshReason.RootIdentityUnavailable);
                token.ThrowIfCancellationRequested();
                if (new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
                    return lease.Stop(RefreshReason.RootIdentityUnavailable);
                var pin = NativeDirectory.OpenRoot(path, false);
                try
                {
                    var identity = NativeMetadata.Identity(pin.Handle);
                    var current = new RefreshRoot(path, identity.Volume, identity.Id, pin.TryGetVolumeId());
                    if (identity.Id != old.ObservedId || current.VolumeId != volume) { pin.Dispose(); return lease.Stop(RefreshReason.RootIdentityChanged); }
                    lease.roots.Add((current, pin));
                }
                catch { pin.Dispose(); throw; }
            }
            var live = lease.VerifyRoots(token);
            var plan = RefreshPlanner.Evaluate(previous, selected, live, limits, token: token);
            if (plan.Action == RefreshAction.KeepPrevious) return lease.Stop(plan.Reason);
            if (plan.Action == RefreshAction.MeasureScope) return Full(plan.Reason);
            if (previous.RefreshedAreas.Count(area => !RefreshPlanner.Below(area.Path, selected.Path)) >= RefreshRebuild.MaximumAreas)
                return Full(RefreshReason.MeasurementHistoryLimit);
            var admission = RefreshRebuild.CanMeasure(previous.RefreshIndex!, selected.Path, fileLimit, scratchBudgetBytes, token);
            if (!admission.Fits) return Full(RefreshReason.MetadataBudget);

            // Pin the exact old selected directory before enumerating any file.
            var scope = live.Single(root => RefreshPlanner.Below(selected.Path, root.Path));
            var expected = new RefreshRoot(selected.Path, scope.Volume, selected.ObservedId, scope.VolumeId);
            try
            {
                lease.selectedPin = NativeDirectory.OpenRoot(selected.Path, false);
                lease.selectedPath = selected.Path;
                var identity = NativeMetadata.Identity(lease.selectedPin.Handle);
                if (identity != (expected.Volume, expected.Id) || lease.selectedPin.TryGetVolumeId() != expected.VolumeId)
                    return Full(RefreshReason.SelectedIdentityChanged);
                lease.selectedPin.VerifyBinding(selected.Path); lease.selectedPin.EnsureRootLocation();
            }
            catch (Exception ex) when (ex is ScopeChangedException || DiskScanner.IsFileError(ex)) { return Full(RefreshReason.SelectedUncertain); }
            progress?.Report(new(0, 0, 0, 0, "Seçili alan yeniden ölçülüyor: " + selected.Path, watch.Elapsed, ScanPhase.Refresh));
            bool scratchExceeded = false; int maxPath = admission.MaxPath; var monitorLock = new object();
            using var nativeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var monitor = new Forward(p =>
            {
                lock (monitorLock)
                {
                    maxPath = Math.Max(maxPath, p.Current.Length);
                    if (RefreshRebuild.NativeScratchEstimate(p.Files, p.Folders, fileLimit, maxPath) > scratchBudgetBytes)
                    { scratchExceeded = true; nativeCancellation.Cancel(); }
                }
                progress?.Report(p);
            });
            var fresh = VerifiedScan.Scan([selected.Path], monitor, nativeCancellation.Token, fileLimit, workers,
                plan.SelectedExclusions, plan.SelectedIndexBudgetBytes, [expected]);
            token.ThrowIfCancellationRequested();
            if (scratchExceeded) return Full(RefreshReason.MetadataBudget);
            plan = RefreshPlanner.Evaluate(previous, selected, lease.VerifyRoots(token), limits, fresh.RefreshIndex, token);
            if (plan.Action == RefreshAction.KeepPrevious) return lease.Stop(plan.Reason);
            if (plan.Action != RefreshAction.MergeSelected) return Full(plan.Reason);
            try { lease.selectedPin.VerifyBinding(selected.Path); lease.selectedPin.EnsureRootLocation(); }
            catch (Exception ex) when (ex is ScopeChangedException || DiskScanner.IsFileError(ex)) { return Full(RefreshReason.SelectedIdentityChanged); }
            try
            {
                progress?.Report(new(fresh.Root.FileCount, fresh.Folders, fresh.Root.LogicalBytes, fresh.IssueCount,
                    "Yeni sonuç bellekten hazırlanıyor; dış alanın eski ölçümü korunuyor.", watch.Elapsed, ScanPhase.Finalizing));
                lease.prepared = RefreshRebuild.Build(previous, fresh, plan, live, fileLimit, scratchBudgetBytes, DateTimeOffset.UtcNow, token);
            }
            catch (RefreshPreparationException ex) { return Full(ex.Reason); }
            lease.Action = RefreshAction.MergeSelected; lease.Reason = RefreshReason.ReadyToMerge;
            lease.prepared.Elapsed = watch.Elapsed;
            lease.ValidateForCommit(token);
            return lease;

            RefreshExecution Full(RefreshReason reason)
            {
                lease.selectedPin?.Dispose(); lease.selectedPin = null; lease.selectedPath = null;
                var identities = lease.VerifyRoots(token);
                progress?.Report(new(0, 0, 0, 0, "Mevcut kapsamın tamamı yeniden ölçülüyor.", watch.Elapsed, ScanPhase.Refresh, reason));
                var completed = VerifiedScan.Scan(scopeRoots, progress, token, fileLimit, workers,
                    scopeExclusions, limits.IndexBytes, identities);
                token.ThrowIfCancellationRequested();
                if (completed.RefreshIndex?.State == RefreshIndexState.AllocationFailed)
                    return lease.Stop(RefreshReason.AllocationFailed);
                if (completed.Failed || completed.Cancelled || completed.ScopeChanged || completed.NeedsRetry)
                    return lease.Stop(RefreshReason.UnsafeEvidence);
                lease.prepared = completed; lease.Action = RefreshAction.MeasureScope; lease.Reason = reason;
                completed.Elapsed = watch.Elapsed;
                lease.ValidateForCommit(token);
                return lease;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return lease.Stop(RefreshReason.Cancelled); }
        catch (OutOfMemoryException) { return lease.Stop(RefreshReason.AllocationFailed); }
        catch (Exception ex) when (ex is ScopeChangedException || DiskScanner.IsFileError(ex))
        { lease.Error = ex.Message; return lease.Stop(RefreshReason.RootIdentityUnavailable); }
        catch { lease.Dispose(); throw; }
    }

    internal ScanResult ValidateForCommit(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        preparationToken.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        if (prepared == null) throw new InvalidOperationException("Yayımlanabilir yeni sonuç yok.");
        VerifyRoots(token);
        foreach (var root in roots)
        {
            var current = prepared.Root.Path == root.Identity.Path ? prepared.Root : prepared.Root.Children.FirstOrDefault(folder => folder.Path == root.Identity.Path);
            if (current?.ObservedId != root.Identity.Id || prepared.RootVolumeIds?.TryGetValue(root.Identity.Path, out var volume) != true || volume != root.Identity.VolumeId)
                throw new RootScopeChangedException("Hazırlanan sonucun kök kimliği eşleşmiyor.");
        }
        if (selectedPin != null) { selectedPin.VerifyBinding(selectedPath!); selectedPin.EnsureRootLocation(); }
        preparationToken.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        return prepared;
    }

    RefreshRoot[] VerifyRoots(CancellationToken token)
    {
        foreach (var item in roots)
        {
            token.ThrowIfCancellationRequested();
            item.Pin.VerifyBinding(item.Identity.Path); item.Pin.EnsureRootLocation();
            if (NativeMetadata.Identity(item.Pin.Handle) != (item.Identity.Volume, item.Identity.Id) || item.Pin.TryGetVolumeId() != item.Identity.VolumeId)
                throw new RootScopeChangedException("İşlem kökü değişti; önceki sonuç korunmalı.");
        }
        return roots.Select(item => item.Identity).ToArray();
    }

    RefreshExecution Stop(RefreshReason reason)
    {
        prepared = null; Action = RefreshAction.KeepPrevious; Reason = reason;
        DisposePins(); return this;
    }
    void DisposePins() { selectedPin?.Dispose(); selectedPin = null; selectedPath = null; foreach (var item in roots) item.Pin.Dispose(); roots.Clear(); }
    sealed class Forward(Action<ScanProgress> report) : IProgress<ScanProgress> { public void Report(ScanProgress value) => report(value); }
    public void Dispose() { if (disposed) return; disposed = true; DisposePins(); prepared = null; }
}
