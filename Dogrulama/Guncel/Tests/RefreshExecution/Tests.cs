using YerelDisk;
using static Support;

static class Tests
{
    static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try
        {
            var t = Tree(output, "ordinary"); var old = Scan([t.Root], excluded: [t.Skip]); var selected = Find(old, t.A); string digest = Digest(old);
            Check(old.Files.Single().Size == 900 && old.RefreshIndex is { Captured: true }, "Initial winner and complete optional index are available");
            File.Delete(Path.Combine(t.A, "winner.bin"));
            File.WriteAllBytes(Path.Combine(t.B, "not-remeasured.bin"), new byte[8000]);
            var paths = new List<string>();
            using var execution = RefreshExecution.Prepare(old, selected, 1, 2, Limits, progress: new Forward(p => paths.Add(p.Current)));
            Check(execution.Action == RefreshAction.MergeSelected && execution.IsIncremental, "Healthy selected scan produces a prepared incremental generation");
            var next = execution.ValidateForCommit();
            Check(next.Root.LogicalBytes == old.Root.LogicalBytes - 900 && next.Root.FileCount == old.Root.FileCount - 1, "Deleted selected file is removed from totals and count");
            Check(next.Files.Single().Path == Path.Combine(t.B, "runner-up.bin"), "Old K+1 outside winner is refilled from all retained observations");
            Check(Find(next, t.B).LogicalBytes == 500 && !paths.Any(p => p == t.B), "Outside folder is neither enumerated nor silently refreshed");
            Check(Digest(old) == digest && ReferenceEquals(selected.Parent, old.Root), "Previous totals rows parents and cache remain unchanged");
            Check(Walk(next.Root).All(f => !Walk(old.Root).Contains(f)), "Every published folder belongs to the new generation");
            Check(next.RefreshIndex is { Captured: true, FileCount: 2 }, "Rebuilt index is complete after deleting the former top-K winner");
            Check(next.RefreshedAreas.Count == 1 && next.RefreshedAreas[0].Path == t.A, "Mixed measurement provenance marks only the newly measured area");
            Check(Throws<InvalidDataException>(() => FolderMeasurements.Capture(next, DateTimeOffset.UtcNow)), "Mixed-time result cannot be saved as a globally fresh reference");
            Check(next.ObservedEvidence!.Owners.Keys.All(f => Walk(next.Root).Contains(f)), "Risk evidence refers only to cloned owners");
            Check(next.ObservedEvidence.Population.Owners[Find(next, t.A)].Bytes == 100, "File population is rebuilt from the new selected observations");
            File.WriteAllBytes(Path.Combine(t.A, "mail.pst"), new byte[200]);
            using var second = RefreshExecution.Prepare(next, Find(next, t.A), 1, 1, Limits);
            var twice = second.ValidateForCommit();
            Check(second.IsIncremental && twice.Root.LogicalBytes == 700, "A second selected refresh can replay the newly rebuilt index");
            Check(twice.RefreshedAreas.Count == 1 && twice.RefreshedAreas[0].MeasuredAt >= next.RefreshedAreas[0].MeasuredAt, "Same-area provenance is replaced without unbounded growth");
            Check(Digest(old) == digest && next.Root.LogicalBytes == 600, "Repeated refresh never mutates either earlier generation");

            using (var c = new CancellationTokenSource())
            {
                c.Cancel(); using var cancelled = RefreshExecution.Prepare(old, selected, 1, 1, Limits, token: c.Token);
                Check(cancelled.PreparedResult == null && cancelled.Reason == RefreshReason.Cancelled, "Cancellation before work produces no replacement");
            }
            using (var c = new CancellationTokenSource())
            {
                using var cancelled = RefreshExecution.Prepare(old, selected, 1, 1, Limits, progress: new Forward(p => { if (p.Current.StartsWith("Seçili alan")) c.Cancel(); }), token: c.Token);
                Check(cancelled.PreparedResult == null && cancelled.Reason == RefreshReason.Cancelled && Digest(old) == digest, "Cancellation at selected-scan boundary retains the exact previous generation");
            }
            using (var c = new CancellationTokenSource())
            {
                using var cancelled = RefreshExecution.Prepare(old, selected, 1, 1, Limits, progress: new Forward(p => { if (p.Current.StartsWith("Yeni sonuç bellekten")) c.Cancel(); }), token: c.Token);
                Check(cancelled.PreparedResult == null && cancelled.Reason == RefreshReason.Cancelled, "Cancellation during rebuild preparation prevents publication");
            }
            using (var c = new CancellationTokenSource())
            {
                c.Cancel(); Check(Throws<OperationCanceledException>(() => execution.ValidateForCommit(c.Token)), "Late publication cancellation is checked after preparation");
            }
            using (var c = new CancellationTokenSource())
            {
                using var prepared = RefreshExecution.Prepare(old, selected, 1, 1, Limits, token: c.Token);
                c.Cancel(); Check(Throws<OperationCanceledException>(() => prepared.ValidateForCommit()), "Lease remembers its preparation token and rejects late cancellation with the default validation call");
            }
            using (var c = new CancellationTokenSource())
            {
                using var prepared = RefreshExecution.Prepare(old, selected, 1, 1, new(0,0,0), token: c.Token);
                c.Cancel(); Check(Throws<OperationCanceledException>(() => prepared.ValidateForCommit()), "Full fallback lease also retains its original cancellation contract");
            }
            using (var foreign = RefreshExecution.Prepare(old, new FolderEntry(t.A, old.Root) { ObservedId = selected.ObservedId }, 1, 1, Limits))
                Check(foreign.PreparedResult == null && foreign.Reason == RefreshReason.OutsideScope, "A foreign selection object does not authorize a scan");
            var fullProgress = new List<ScanProgress>();
            using (var full = RefreshExecution.Prepare(old, selected, 1, 1, Limits, scratchBudgetBytes: 0, progress: new Forward(fullProgress.Add)))
            {
                var all = full.ValidateForCommit();
                Check(full.Action == RefreshAction.MeasureScope && !all.HasMixedMeasurementTimes, "Scratch admission failure falls back to a full original-scope measurement");
                Check(all.Root.LogicalBytes == 8700 && all.ExcludedPaths.SequenceEqual([t.Skip]), "Full fallback sees outside changes while preserving exclusions");
                Check(FolderMeasurements.Capture(all, DateTimeOffset.UtcNow).Folders.Length > 0, "A completed full fallback can be saved as one-time reference");
                Check(fullProgress.First(p => p.FullRefreshReason != null).FullRefreshReason == full.Reason && full.Reason == RefreshReason.MetadataBudget, "Full-scope progress reports the real budget fallback reason before enumeration");
            }
            var uncached = Scan([t.Root], excluded: [t.Skip], budget: 0);
            using (var full = RefreshExecution.Prepare(uncached, Find(uncached, t.A), 1, 1, Limits))
                Check(full.Action == RefreshAction.MeasureScope && full.ValidateForCommit().RefreshIndex is { Captured: true }, "Missing cache automatically measures original scope and can capture a new cache");
            using (var full = RefreshExecution.Prepare(old, selected, 1, 1, new(0, 0, 0)))
                Check(full.Action == RefreshAction.MeasureScope && full.ValidateForCommit().RefreshIndex == null, "Disabled index policy stays disabled on full fallback");
            Check(Throws<InvalidOperationException>(() => RefreshExecution.Prepare(old, selected, 1, 1, Limits,
                progress: new Forward(_ => throw new InvalidOperationException("injected preparation failure")))), "Unexpected preparation errors propagate after releasing pins");
            Check(Digest(old) == digest, "Injected preparation failure leaves previous results intact");
            var disposable = RefreshExecution.Prepare(old, selected, 1, 1, Limits); disposable.Dispose(); disposable.Dispose();
            Check(Throws<ObjectDisposedException>(() => disposable.ValidateForCommit()), "Disposed leases cannot publish and double disposal is safe");

            var late = Tree(output, "late-selected"); var baseline = Scan([late.Root], excluded: [late.Skip]); string saved = Digest(baseline);
            using (var prepared = RefreshExecution.Prepare(baseline, Find(baseline, late.A), 1, 1, Limits))
            {
                Directory.Move(late.A, late.A + "-moved"); Directory.CreateDirectory(late.A);
                Check(Throws<ScopeChangedException>(() => prepared.ValidateForCommit()) && Digest(baseline) == saved, "Selected-directory swap after preparation blocks publication without mutating old data");
            }
            var rootSwap = Tree(output, "root-swap"); var rootOld = Scan([rootSwap.Root]);
            bool rootMoved = false;
            using (var prepared = RefreshExecution.Prepare(rootOld, Find(rootOld, rootSwap.A), 1, 1, Limits))
            {
                try { Directory.Move(rootSwap.Root, rootSwap.Root + "-moved"); rootMoved = true; Directory.CreateDirectory(rootSwap.Root); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 5 or 32) { }
                Check(rootMoved ? Throws<ScopeChangedException>(() => prepared.ValidateForCommit()) : prepared.ValidateForCommit() != null,
                    "Root swap is either prevented by held directory pins or rejected before publication");
            }
            if (!rootMoved) { Directory.Move(rootSwap.Root, rootSwap.Root + "-moved"); Directory.CreateDirectory(rootSwap.Root); }
            using (var stopped = RefreshExecution.Prepare(rootOld, Find(rootOld, rootSwap.A), 1, 1, Limits))
                Check(stopped.Action == RefreshAction.KeepPrevious && stopped.PreparedResult == null, "Root already replaced before work is never enumerated as a new result");

            var during = Tree(output, "during-fallback"); var oldDuring = Scan([during.Root], budget: 0); bool swapped = false, blocked = false;
            using (var stopped = RefreshExecution.Prepare(oldDuring, Find(oldDuring, during.A), 1, 1, Limits, progress: new Forward(p =>
            {
                if (!swapped && !blocked && p.Current.StartsWith("Mevcut kapsamın"))
                {
                    try { Directory.Move(during.Root, during.Root + "-moved"); swapped = true; Directory.CreateDirectory(during.Root); }
                    catch (IOException ex) when ((ex.HResult & 0xffff) is 5 or 32) { blocked = true; }
                }
            }))) Check(swapped ? stopped.PreparedResult == null : blocked && stopped.ValidateForCommit().Root.ObservedId == oldDuring.Root.ObservedId,
                "Fallback never measures a replacement root even if an attempted swap is blocked by the filesystem");

            var page = Tree(output, "pages");
            for (int i = 0; i < 200; i++) File.WriteAllBytes(Path.Combine(page.B, $"p{i}.bin"), new byte[i]);
            var paged = Scan([page.Root]); var owners = paged.RefreshIndex!.Owners().ToArray();
            Check(owners.SelectMany(o => o.Files).SequenceEqual(paged.RefreshIndex.Files), "Page slices cover every file once across page boundaries and empty owners");
            Check(owners.Reverse().Sum(o => o.Files.Count()) == paged.RefreshIndex.FileCount, "Page slices are independently repeatable in another traversal order");
            var growth = Tree(output, "growth"); var beforeGrowth = Scan([growth.Root], excluded: [growth.Skip]); bool expanded = false;
            using (var bounded = RefreshExecution.Prepare(beforeGrowth, Find(beforeGrowth, growth.A), 1, 1, Limits,
                scratchBudgetBytes: (16 << 20) + 65536, progress: new Forward(p =>
                {
                    if (!expanded && p.Current.StartsWith("Seçili alan"))
                    { expanded = true; for (int i = 0; i < 200; i++) File.WriteAllBytes(Path.Combine(growth.A, "growth-" + i), []); }
                })))
                Check(expanded && bounded.Action == RefreshAction.MeasureScope && bounded.Reason == RefreshReason.MetadataBudget
                    && bounded.ValidateForCommit().Root.FileCount == 203, "Unexpected selected growth cancels the fast scan at a progress boundary and falls back to full scope");
            Check(Throws<ArgumentOutOfRangeException>(() => RefreshExecution.Prepare(old, selected, 0, 1)), "Invalid retained-file limit is rejected");
            Check(Throws<ArgumentOutOfRangeException>(() => RefreshExecution.Prepare(old, selected, 1, 17)), "Invalid worker limit is rejected");
            Check(Throws<ArgumentOutOfRangeException>(() => RefreshExecution.Prepare(old, selected, 1, 1, scratchBudgetBytes: -1)), "Invalid scratch budget is rejected");
            Log.Add($"ALL CHECKS PASSED ({Log.Count})"); return 0;
        }
        catch (Exception ex) { Log.Add("FAIL " + ex); return 1; }
        finally { File.WriteAllLines(Path.Combine(output, "results.txt"), Log); foreach (string line in Log) Console.WriteLine(line); }
    }
}
