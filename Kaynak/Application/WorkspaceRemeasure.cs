namespace YerelDisk.Presentation;

// One replacement transaction for both full-scope and selected refreshes.
internal sealed partial class WorkspaceViewModel
{
    bool remeasuring;
    public bool IsRemeasuring => remeasuring;
    public bool CanRemeasure => !disposed && !busy && !refreshing && !Inventory.IsBusy && scan is { RootPaths.Count: > 0 };
    public string RemeasureHint => remeasuring
        ? "Yeni ölçüm hazırlanıyor; listede ve kartlarda eski sonuç gösteriliyor. İptalde eski sonuç korunur."
        : scan == null ? "Önce bir tarama yapın."
        : Inventory.IsBusy ? "Uygulama kayıtlarının incelemesi bitince yeniden ölçebilirsiniz."
        : "Sonucun kendi köklerini ve hariç yollarını yeniden ölçer; sonraki tarama için seçtiğiniz yollar değişmez. Yeni sonuç hazır olana kadar eski ölçüm korunur.\n"
            + $"Ölçülecek: {scan.RootPaths.Count} kök · {scan.ExcludedPaths.Count} hariç yol\n" + string.Join("\n", scan.RootPaths.Take(3))
            + (scan.RootPaths.Count > 3 ? "\nDiğer kökler de bu sonucun kapsamındadır." : "");

    public Task RemeasureAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (busy) return CurrentScan;
        if (!CanRemeasure) return Task.CompletedTask;
        CurrentScan = RunRemeasureAsync();
        return CurrentScan;
    }

    async Task RunRemeasureAsync(FolderEntry? refreshTarget = null)
    {
        var previous = scan!;
        // Capture scope from the RESULT, never the editable next-scan lists.
        var request = new ScanRequest(previous.RootPaths, workers, fileLimit, previous.ExcludedPaths, RefreshIndexBudget);
        var previousMeasuredAt = MeasurementCompletedAt;
        var previousIndexState = previous.RefreshIndex?.State;
        string fullRefreshExplanation = "";
        IPreparedRefresh? refreshLease = null;
        var previousView = view;
        var targetView = view == WorkspaceView.Settings ? WorkspaceView.Folders : view;
        string? selectedPath = SelectedPath;
        Type? selectedType = selection?.GetType();
        var query = new ViewQuery(targetView, folder, search.Trim(), fullPath, reviewMinimum, reviewKind, sortKey, descending, targetView == WorkspaceView.Review && reviewCollection == 1);
        bool chart = targetView == WorkspaceView.Folders && distributionMode;
        CancelRefresh();
        var source = new CancellationTokenSource();
        scanCancellation = source;
        int version = ++scanVersion;
        busy = remeasuring = true; error = "";
        status = refreshTarget == null ? "Tüm mevcut kapsam yeniden ölçülüyor · Eski sonuç gösteriliyor."
            : "Klasör yenileniyor · Hedef: " + refreshTarget.Path + " · Eski sonuç gösteriliyor.";
        NotifyAll();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (disposed || !remeasuring || source.IsCancellationRequested || version != scanVersion) return;
            if (p.FullRefreshReason is { } reason) fullRefreshExplanation = FullRefreshExplanation(reason, previousIndexState);
            string operation = fullRefreshExplanation.Length == 0 ? "Yeniden ölçülüyor" : "Tüm mevcut kapsam ölçülüyor · Neden: " + fullRefreshExplanation;
            status = $"{operation} · {ScanSummary.PhaseName(p.Phase)} · {ScanSummary.Duration(p.Elapsed)} · {p.Files:N0} dosya · Eski sonuç gösteriliyor · {p.Current}";
            Notify(nameof(Status));
        });
        try
        {
            var completed = refreshTarget == null ? await scanService.ScanAsync(request, progress, source.Token)
                : (refreshLease = await refreshService.PrepareAsync(previous, refreshTarget, request, progress, source.Token)).Outcome;
            source.Token.ThrowIfCancellationRequested();
            if (disposed || version != scanVersion || !ReferenceEquals(scan, previous)) return;

            ValidateReplacement(previous, request, completed.Scan);
            var measuredAt = DateTimeOffset.UtcNow;
            var prepared = await Task.Run(() =>
            {
                var currentFolder = FindReplacementFolder(completed.Scan, query.Folder, source.Token);
                var currentQuery = query with { Folder = currentFolder };
                var filtered = ResultQuery.Execute(completed.Scan, completed.Candidates, currentQuery, source.Token);
                var distribution = chart ? SpaceDistribution.Create(completed.Scan, currentFolder, filtered.Rows, currentQuery.Search.Length > 0, source.Token) : SpaceDistribution.Empty;
                var summary = ScanSummary.Create(completed.Scan);
                object? selected = filtered.Rows.FirstOrDefault(row => row.GetType() == selectedType && string.Equals(RowValues.PathOf(row), selectedPath, StringComparison.Ordinal));
                return (Folder: currentFolder, Filtered: filtered, Distribution: distribution, Summary: summary, Selection: selected);
            }, source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (disposed || version != scanVersion || !ReferenceEquals(scan, previous)) return;

            // The native lease remains open through query/summary preparation.
            refreshLease?.ValidateForCommit(source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (disposed || version != scanVersion || !ReferenceEquals(scan, previous)) return;

            // Everything is ready before publishing any of the new generation.
            scan = completed.Scan; folder = prepared.Folder; candidates = completed.Candidates;
            rows = prepared.Filtered.Rows; MaximumVisibleSize = prepared.Filtered.Maximum;
            selection = prepared.Selection; preferredSelection = null; details = null;
            view = targetView; search = prepared.Filtered.Query.Search;
            AppliedQuery = prepared.Filtered.Query; AppliedSearch = prepared.Filtered.Query.Search; AppliedCounts = prepared.Filtered.Counts;
            unrecognizedAreas = prepared.Filtered.Areas; Distribution = prepared.Distribution; Summary = prepared.Summary;
            feedback.Clear(); viewStates.Clear(); reviewStates.Clear();
            MeasurementCompletedAt = scan.HasMixedMeasurementTimes ? previousMeasuredAt : measuredAt;
            LogicalSize = scan.Root.Measurement == MeasurementState.Unknown ? "Bilinmiyor" : PresentationText.Size(scan.Root.LogicalBytes);
            AllocatedSize = scan.Root.Measurement == MeasurementState.Unknown ? "Bilinmiyor" : PresentationText.Size(scan.Root.AllocatedBytes);
            FileCount = scan.Root.Measurement == MeasurementState.Unknown ? "Bilinmiyor" : scan.Root.FileCount.ToString("N0");
            RefreshDecisionNotice = refreshTarget == null ? "" : scan.HasMixedMeasurementTimes
                ? "Yalnız hedef klasör ölçüldü: " + refreshTarget.Path
                : "Tüm mevcut kapsam ölçüldü · Neden: " + FullRefreshExplanation(refreshLease!.Reason, previousIndexState);
            status = scan.HasMixedMeasurementTimes ? "Klasör yenilendi · Karma ölçüm; dış alanlar önceki ölçümünü korur · " + refreshTarget!.Path
                : $"Yeniden ölçüldü · {(refreshTarget == null ? "" : RefreshDecisionNotice + " · ")}{ResultStatus(scan)} · {scan.Elapsed.TotalSeconds:N2} sn · {scan.Folders:N0} klasör · {scan.IssueCount:N0} uyarı";
            Inventory.Bind(scan); // Drops old hints only after the replacement succeeded.
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        { if (!disposed) status = "Yeniden ölçüm durduruldu · Eski sonuç ve ölçüm zamanı korundu; güncel ölçüm değildir."; }
        catch (Exception ex)
        {
            if (!disposed)
            {
                ShowError("Yeniden ölçüm uygulanmadı. " + ex.Message);
                status = "Yeniden ölçüm tamamlanamadı · Eski sonuç ve ölçüm zamanı korundu; güncel ölçüm değildir.";
            }
        }
        finally
        {
            refreshLease?.Dispose();
            // No query rebuild on rollback: keep the exact old rows, selection,
            // reports and inventory state instead of mixing generations.
            if (ReferenceEquals(scan, previous)) view = previousView;
            busy = remeasuring = false;
            if (ReferenceEquals(scanCancellation, source)) scanCancellation = null;
            source.Dispose();
            if (!disposed) NotifyAll();
        }
    }

    static void ValidateReplacement(ScanResult previous, ScanRequest request, ScanResult next)
    {
        if (ReferenceEquals(previous, next)) throw new InvalidDataException("Tarama hizmeti ayrı bir sonuç üretmedi.");
        if (next.Cancelled) throw new IOException("Yeni ölçüm durdurulmuş veya tamamlanmamış.");
        if (next.RefreshIndex?.State == RefreshIndexState.AllocationFailed)
            throw new OutOfMemoryException("İndeks için bellek ayrılamadı. Önceki sonuç korundu.");
        if (next.Failed || next.ScopeChanged || next.NeedsRetry) throw new IOException("Kök veya kararlı ölçüm doğrulanamadı. Önceki sonuç değiştirilmedi.");
        if (!request.Roots.ToHashSet(StringComparer.Ordinal).SetEquals(next.RootPaths)
            || !request.Exclusions.ToHashSet(StringComparer.Ordinal).SetEquals(next.ExcludedPaths))
            throw new InvalidDataException("Yeni ölçümün kapsamı önceki kapsamla eşleşmiyor.");
        foreach (var root in request.Roots)
        {
            if (previous.RootVolumeIds?.TryGetValue(root, out var volume) == true
                && (next.RootVolumeIds?.TryGetValue(root, out var current) != true || current != volume))
                throw new IOException("Bir kökün disk kimliği değişti veya doğrulanamadı.");
            var before = RootFolder(previous, root); var after = RootFolder(next, root);
            if (before?.ObservedId is > 0 && before.ObservedId != after?.ObservedId)
                throw new IOException("Bir kök klasörün kimliği değişti veya doğrulanamadı.");
        }
        // Ordinary inaccessible descendants remain explicitly partial, just as
        // on a normal C: scan; they are not a fatal root/transaction failure.
    }
    static FolderEntry? RootFolder(ScanResult result, string path) => result.Root.Path == path ? result.Root : result.Root.Children.FirstOrDefault(f => f.Path == path);
    static FolderEntry FindReplacementFolder(ScanResult result, FolderEntry? previous, CancellationToken token)
    {
        var wanted = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var p = previous; p != null; p = p.Parent) wanted.TryAdd(p.Path, wanted.Count);
        FolderEntry best = result.Root; int bestRank = int.MaxValue;
        var todo = new Stack<FolderEntry>(); todo.Push(result.Root);
        while (todo.TryPop(out var next))
        {
            token.ThrowIfCancellationRequested();
            if (wanted.TryGetValue(next.Path, out int rank) && rank < bestRank)
            {
                if (rank == 0) return next;
                best = next; bestRank = rank;
            }
            foreach (var child in next.Children) todo.Push(child);
        }
        return best;
    }
}
