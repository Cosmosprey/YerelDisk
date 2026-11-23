namespace YerelDisk;

internal sealed class RefreshPreparationException(string message, RefreshReason reason = RefreshReason.MetadataBudget) : IOException(message)
{ internal RefreshReason Reason { get; } = reason; }

// Replays existing metadata into a NEW generation. No filesystem access and no
// previous FolderEntry is reused, reparented or written through.
internal static class RefreshRebuild
{
    internal const long DefaultScratchBudget = 256L << 20;
    internal const int MaximumAreas = 64;

    internal static long NativeScratchEstimate(long files, long directories, int limit, int maxDirectoryLength)
        => (16L << 20) + 1024 * directories + 1024 * files
            + Math.Min(files, limit) * (256L + 2L * (maxDirectoryLength + 256));

    internal static (bool Fits, int MaxPath) CanMeasure(RefreshIndex index, string selected, int limit, long budget, CancellationToken token)
    {
        long files = 0, directories = 0; int maxPath = selected.Length, oldMaxPath = 0;
        foreach (var directory in index.Directories)
        {
            token.ThrowIfCancellationRequested();
            oldMaxPath = Math.Max(oldMaxPath, directory.Data.Path.Length);
            if (!RefreshPlanner.Below(directory.Data.Path, selected)) continue;
            files += directory.FileCount; directories++; maxPath = Math.Max(maxPath, directory.Data.Path.Length);
        }
        // The old pool alone is an unavoidable part of the rebuild. A zero
        // filename length is a lower bound; it cannot wrongly reject a fitting pool.
        long oldMinimum = (16L << 20) + 1024L * index.DirectoryCount + 192L * index.FileCount
            + Math.Min(index.FileCount, limit) * (256L + 2L * (oldMaxPath + 1));
        return (oldMinimum <= budget && NativeScratchEstimate(files, directories, limit, maxPath) <= budget, maxPath);
    }

    internal static ScanResult Build(ScanResult previous, ScanResult fresh, RefreshPlan plan,
        IReadOnlyList<RefreshRoot> roots, int fileLimit, long scratchBudget, DateTimeOffset measuredAt,
        CancellationToken token)
    {
        if (plan.Action != RefreshAction.MergeSelected || previous.RefreshIndex is not { Captured: true } oldIndex
            || fresh.RefreshIndex is not { Captured: true } newIndex) throw new InvalidOperationException("Birleştirme kararı yok.");
        string selected = plan.SelectedPath;
        var areas = previous.RefreshedAreas.Where(area => !RefreshPlanner.Below(area.Path, selected)).ToList();
        if (areas.Count >= MaximumAreas) throw new RefreshPreparationException("Ölçüm zamanı kayıt sınırı; tam ölçüm gerekli.", RefreshReason.MeasurementHistoryLimit);
        EnsureScratch(oldIndex, newIndex, fileLimit, scratchBudget, token);
        var originals = Walk(previous.Root).ToDictionary(folder => folder.Path, StringComparer.Ordinal);
        var freshFolders = Walk(fresh.Root).ToDictionary(folder => folder.Path, StringComparer.Ordinal);
        var data = new List<(RefreshDirectoryData Data, IEnumerable<FileObservation> Files, FolderEntry Original)>();
        bool inserted = false;
        foreach (var owner in oldIndex.Owners())
        {
            token.ThrowIfCancellationRequested();
            if (!RefreshPlanner.Below(owner.Directory.Data.Path, selected))
                data.Add((owner.Directory.Data, owner.Files, originals[owner.Directory.Data.Path]));
            else if (owner.Directory.Data.Path == selected)
            {
                if (inserted) throw new RefreshPreparationException("Yinelenen seçili alan.");
                inserted = true;
                foreach (var replacement in newIndex.Owners())
                {
                    var metadata = replacement.Directory.Data;
                    if (metadata.Path == selected) metadata = metadata with { ParentPath = owner.Directory.Data.ParentPath };
                    data.Add((metadata, replacement.Files, freshFolders[metadata.Path]));
                }
            }
        }
        if (!inserted) throw new RefreshPreparationException("Seçili alan indekste bulunamadı.");
        var folders = new Dictionary<string, FolderEntry>(StringComparer.Ordinal);
        foreach (var item in data)
        {
            token.ThrowIfCancellationRequested();
            var original = item.Original;
            var clone = new FolderEntry(item.Data.Path, null)
            {
                ObservedId = original.ObservedId, Measurement = original.Measurement,
                LastObservedBytes = original.LastObservedBytes,
                IssueCount = Math.Max(0, original.IssueCount - original.Children.Sum(child => child.IssueCount))
            };
            if (!folders.TryAdd(clone.Path, clone)) throw new RefreshPreparationException("Yinelenen klasör yolu.");
        }
        bool multiple = previous.RootPaths.Count > 1;
        var root = multiple ? new FolderEntry(previous.Root.Path, null) { Measurement = previous.Root.Measurement } : folders[previous.Root.Path];
        foreach (var item in data)
        {
            var folder = folders[item.Data.Path];
            FolderEntry? parent = item.Data.ParentPath is { } parentPath ? folders[parentPath] : multiple ? root : null;
            folder.Parent = parent; parent?.Children.Add(folder);
        }
        var result = new ScanResult(root)
        {
            Folders = folders.Count, RetainedFileLimit = fileLimit, Workers = fresh.Workers,
            UnknownAllocation = previous.UnknownAllocation, Timings = fresh.Timings
        };
        result.RootPaths.Clear(); result.RootPaths.AddRange(plan.ScopeRoots);
        result.RootVolumeIds = roots.ToDictionary(item => item.Path, item => item.VolumeId!.Value, StringComparer.Ordinal);
        result.ExcludedPaths = plan.Exclusions;
        result.UnverifiedPaths.UnionWith(previous.UnverifiedPaths.Where(path => !RefreshPlanner.Below(path, selected)));
        result.Issues.AddRange(previous.Issues.Where(issue => !RefreshPlanner.Below(issue.Path, selected)).Take(ScanResult.IssueLimit));
        result.VerificationHistory.AddRange(previous.VerificationHistory.TakeLast(127));
        result.VerificationHistory.Add("Seçili alan yeniden ölçüldü; diğer alanların önceki gözlemleri korundu: " + selected);
        var owners = data.Select(item => new ObservationOwner(folders[item.Data.Path], item.Data.OwnStreams,
            item.Files, checked((int)(item.Original.FileCount - item.Original.Children.Sum(child => child.FileCount))))).ToArray();
        ObservationAggregate.Apply(result, owners, fileLimit, Math.Min(IdentityCapacity.MaxInitialHint, oldIndex.FileCount),
            (_, _) => throw new RefreshPreparationException("Taşınmış dosya gözlemi; tam ölçüm gerekli."), token);
        var order = Walk(root).ToArray();
        for (int i = order.Length - 1; i > 0; i--)
        {
            token.ThrowIfCancellationRequested();
            var folder = order[i]; var parent = folder.Parent!;
            parent.LogicalBytes += folder.LogicalBytes; parent.AllocatedBytes += folder.AllocatedBytes;
            parent.FileCount += folder.FileCount; parent.IssueCount += folder.IssueCount;
            if (folder.Measurement != MeasurementState.Measured && parent.Measurement == MeasurementState.Measured) parent.Measurement = MeasurementState.Partial;
        }
        result.IssueCount = root.IssueCount;
        var exclusions = ScanExclusions.Create(plan.ScopeRoots, plan.Exclusions);
        exclusions.ApplyTo(result);
        result.ObservedEvidence = ObservedFileEvidence.Capture([result], exclusions);

        // Capture expects one source per actual root. Their trees are the new
        // generation's objects, never old trees, and the first owner is the root.
        var parts = roots.Select(scope =>
        {
            var part = new ScanResult(folders[scope.Path]) { ExcludedPaths = plan.Exclusions };
            var own = data.Where(item => RefreshPlanner.Below(item.Data.Path, scope.Path)).ToArray();
            part.Folders = own.Length;
            part.FileCandidates = result.FileCandidates!.Where(item => RefreshPlanner.Below(item.Owner.Path, scope.Path));
            part.RefreshDirectoryCandidates = own.Select(item =>
            {
                var folder = folders[item.Data.Path];
                return (folder, item.Data with { ParentPath = folder.Path == scope.Path ? null : folder.Parent?.Path,
                    Measurement = folder.Measurement, LogicalBytes = folder.LogicalBytes,
                    AllocatedBytes = folder.AllocatedBytes, FileCount = folder.FileCount, IssueCount = folder.IssueCount });
            });
            return part;
        }).ToArray();
        result.RefreshIndex = RefreshIndex.Capture(parts, roots, plan.Exclusions, plan.ReplacementIndexBudgetBytes, token);
        token.ThrowIfCancellationRequested();
        if (result.RefreshIndex?.State == RefreshIndexState.AllocationFailed)
            throw new OutOfMemoryException("Yeni yenileme indeksi için bellek ayrılamadı; eski sonuç korunmalı.");
        if (result.RefreshIndex is not { Captured: true }) throw new RefreshPreparationException("Yeni indeks bütçeye sığmadı veya tamamlanamadı.");
        result.PhysicalLinks.Clear(); result.PhysicalLinks.TrimExcess(); result.FileCandidates = null;
        FileHints.Apply(result);
        areas.Add(new(selected, measuredAt)); result.RefreshedAreas = areas.AsReadOnly();
        return result;
    }

    static void EnsureScratch(RefreshIndex oldIndex, RefreshIndex fresh, int limit, long budget, CancellationToken token)
    {
        long count = (long)oldIndex.FileCount + fresh.FileCount, directories = (long)oldIndex.DirectoryCount + fresh.DirectoryCount;
        int maxDirectory = 0, maxName = 0;
        foreach (var index in new[] { oldIndex, fresh })
        {
            foreach (var directory in index.Directories) { token.ThrowIfCancellationRequested(); maxDirectory = Math.Max(maxDirectory, directory.Data.Path.Length); }
            foreach (var file in index.Files) { token.ThrowIfCancellationRequested(); maxName = Math.Max(maxName, file.Name.Length); }
        }
        // Conservative admission estimate, not a process-RAM guarantee. Includes
        // maps/clone owners, identity accounting, evidence, top-K paths/heap.
        long estimate = (16L << 20) + 1024 * directories + 192 * count
            + Math.Min(count, limit) * (256L + 2L * (maxDirectory + maxName + 1));
        if (estimate > budget) throw new RefreshPreparationException("Yeniden kurma çalışma alanı sınırı; tam ölçüm gerekli.");
    }

    static IEnumerable<FolderEntry> Walk(FolderEntry root)
    {
        var todo = new Stack<FolderEntry>(); todo.Push(root);
        while (todo.TryPop(out var folder)) { yield return folder; for (int i = folder.Children.Count - 1; i >= 0; i--) todo.Push(folder.Children[i]); }
    }
}
