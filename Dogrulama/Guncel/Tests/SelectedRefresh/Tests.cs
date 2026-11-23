using YerelDisk;
using YerelDisk.Presentation;

static class Tests
{
    static readonly List<string> log = [];
    static void Check(bool value, string label) { if (!value) throw new Exception(label); log.Add("PASS " + label); }
    static string Csv(WorkspaceViewModel model) { using var writer = new StringWriter(); ReportSnapshot.Capture(model).Write(writer); return writer.ToString(); }
    static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        try { await Workflow(); await Rollback(); await Lifetime(); await Native(args[0]); Provenance(); log.Add($"ALL CHECKS PASSED ({log.Count})"); return 0; }
        catch (Exception ex) { log.Add("FAIL " + ex); return 1; }
        finally { File.WriteAllLines(Path.Combine(args[0], "results.txt"), log); foreach (string line in log) Console.WriteLine(line); }
    }
    static async Task Workflow()
    {
        var scan = new ScanFake(); var refresh = new RefreshFake(); var inventory = new InventoryFake(); using var model = new WorkspaceViewModel(scan, inventory, refresh);
        model.Roots.Add(@"C:\scope"); await model.ScanAsync();
        Check(model.ScanActionLabel == "Yeni tam tarama" && model.ScanActionHint.Contains("yalnız doğrulama yapmaz"), "Primary action describes a new complete scan instead of verification");
        Check(model.RefreshCacheMiB == 0 && scan.Last!.RefreshIndexBudgetBytes == 0 && !model.CanRefreshSelected, "Default scan retains no optional cache and selected refresh starts disabled");
        await model.RefreshSelectedAsync(); Check(refresh.Calls == 0, "Disabled selected action does not dispatch native work");
        model.RefreshCacheMiB = 64; model.SelectedRow = model.Rows.OfType<FolderEntry>().Single(f => f.Name == "a");
        Check(model.CanRefreshSelected && model.RefreshSelectionHint.Contains(@"C:\scope\a") && model.RefreshSelectionHint.Contains("İlk kullanım"), "Opt-in explains the selected target and uncached full-scope first use");
        await model.Inventory.InspectAsync(); model.Inventory.ExportHints = true;
        var old = model.Scan; var rows = model.Rows; var oldTime = model.MeasurementCompletedAt; var oldReport = ReportSnapshot.Capture(model); string oldCsv = Csv(model); var oldInventory = model.Inventory.Index;
        model.Roots.Clear(); model.Roots.Add(@"D:\future"); model.Exclusions.Add(@"D:\future\skip");
        refresh.Pending = Data.Gate(); var running = model.RefreshSelectedAsync();
        Check(refresh.Request!.Roots.SequenceEqual([@"C:\scope"]) && refresh.Request.Exclusions.SequenceEqual([@"C:\scope\skip"]) && refresh.Request.RefreshIndexBudgetBytes == (64L << 20), "Selected request freezes result scope and current metadata preference");
        Check(ReferenceEquals(model.Scan, old) && ReferenceEquals(model.Rows, rows) && model.MeasurementCompletedAt == oldTime && model.IsRemeasuring, "Old scan rows and timestamp remain intact while the selected service prepares");
        Check(!model.CanRefreshSelected && !model.CanCompareMeasurements && !model.CanExport && model.CanStop, "Conflicting actions are disabled and cancellation remains available");
        model.RefreshCacheMiB = 0; Check(model.RefreshCacheMiB == 64, "Index preference cannot change during replacement");
        Check(ReferenceEquals(running, model.RemeasureAsync()) && ReferenceEquals(running, model.ScanAsync()), "Repeated full or selected actions cannot start competing work");
        var next = Data.Outcome(12, true); bool coherent = true;
        model.PropertyChanged += (_, _) => { if (ReferenceEquals(model.Scan, next.Scan)) coherent &= model.MeasurementCompletedAt == oldTime && model.Rows.OfType<FolderEntry>().All(f => ReferenceEquals(f.Parent, next.Scan.Root)); };
        var lease = new LeaseFake(next, _ => Check(ReferenceEquals(model.Scan, old) && ReferenceEquals(model.Rows, rows), "Lease commit validation happens before any new generation is published"));
        refresh.Pending.SetResult(lease); await running;
        Check(coherent && ReferenceEquals(model.Scan, next.Scan) && model.Rows.OfType<FolderEntry>().Any(f => f.LogicalBytes == 12), "Notifications see a coherent new generation after atomic publication");
        Check(lease.Validations == 1 && lease.Disposals == 1, "Selected lease is validated once after preparation and disposed exactly once");
        Check(model.MeasurementCompletedAt == oldTime && model.HasMixedMeasurements && model.ShowScopeNotice, "Mixed result preserves original full-measurement time and always shows a notice");
        Check(!model.CanCompareMeasurements && model.ComparisonHint.Contains("tüm kapsamı"), "Mixed result disables reference and comparison with an actionable explanation");
        Check(model.CompactScopeNotice.Contains("Karma ölçüm") && model.ScopeText.Contains("Alan yenilemeleri"), "Compact notice and full scope identify differing measurement times");
        model.SelectedRow = model.Rows.OfType<FolderEntry>().Single(f => f.Name == "a");
        Check(model.FullDetails.Contains("Alan ölçümü") && model.Details.Sections[0].Heading == "Ölçüm zamanı", "F6 and side details identify the selected refreshed area's time");
        model.SelectedRow = model.Rows.OfType<FolderEntry>().Single(f => f.Name == "b");
        Check(model.FullDetails.Contains("yeniden taranmadı"), "Outside selection explicitly identifies older observations");
        string mixedCsv = Csv(model); Check(mixedCsv.Contains("Yenilenen alan") && mixedCsv.Contains("Önceki tam kapsam ölçümü"), "CSV captures mixed provenance and actual updated area timestamps");
        Check(!model.Inventory.HasSnapshot && !model.Inventory.ExportHints && inventory.Calls == 1, "Commit discards stale inventory without automatically reading new records");
        Check(model.Roots.Single() == @"D:\future", "Editable future scan scope is preserved");
        using (var writer = new StringWriter()) { oldReport.Write(writer); Check(writer.ToString() == oldCsv, "A pre-refresh CSV snapshot remains immutable"); }
        await model.RemeasureAsync();
        Check(!model.HasMixedMeasurements && model.MeasurementCompletedAt > oldTime && model.CanCompareMeasurements, "Full remeasurement clears mixed provenance and restores references");
        Check(scan.Last!.RefreshIndexBudgetBytes == (64L << 20), "Full measurement honors the explicitly enabled index preference");
        model.SelectedRow = null; refresh.Pending = null; refresh.Next = () => new LeaseFake(Data.Outcome(12), reason: RefreshReason.WholeScope); await model.RefreshSelectedAsync();
        Check(ReferenceEquals(refresh.Target, model.Scan!.Root) == false && refresh.Target!.Path == @"C:\scope" && !model.HasMixedMeasurements && model.Status.Contains("Tüm mevcut kapsam") && model.Status.Contains("Kök / tüm kapsam hedeflendi"), "Without a selected row the open root is targeted and full measurement has the actual reason");
        Check(model.RefreshActionLabel == "Tüm kapsamı yenile" && model.RefreshTargetNotice.Contains(@"C:\scope") && model.RefreshTargetNotice.Contains("tüm kapsam"), "Root refresh visibly declares both its target and complete scope");
        Check(model.RefreshSelectionHint.Contains(model.RefreshDecisionNotice) && model.RefreshDecisionNotice.Contains("Kök / tüm kapsam"), "Last completed refresh decision remains available after progress ends");
        await model.SetViewAsync(WorkspaceView.Files); int calls = refresh.Calls; await model.RefreshSelectedAsync(); Check(!model.CanRefreshSelected && refresh.Calls == calls, "Files view cannot accidentally refresh a file as a directory");
        using var other = new WorkspaceViewModel(new ScanFake(), new InventoryFake(), new RefreshFake()); Check(other.RefreshCacheMiB == 0, "Index preference is session-only and defaults off in another workspace");
    }
    static async Task Rollback()
    {
        foreach (bool cancel in new[] { false, true })
        {
            var refresh = new RefreshFake(); using var model = new WorkspaceViewModel(new ScanFake(), new InventoryFake(), refresh); model.Roots.Add(@"C:\scope"); model.RefreshCacheMiB = 128; await model.ScanAsync(); model.SelectedRow = model.Rows[0];
            var old = model.Scan; var rows = model.Rows; var time = model.MeasurementCompletedAt; string csv = Csv(model);
            refresh.Pending = Data.Gate(); var running = model.RefreshSelectedAsync(); if (cancel) model.CancelScan();
            var lease = new LeaseFake(Data.Outcome(15, true), _ => throw new IOException("Injected final root check failure")); refresh.Pending.SetResult(lease); await running;
            Check(ReferenceEquals(model.Scan, old) && ReferenceEquals(model.Rows, rows) && model.MeasurementCompletedAt == time && Csv(model) == csv, "Cancelled or failed commit preserves exact old generation: cancel=" + cancel);
            Check(lease.Disposals == 1 && lease.Validations == (cancel ? 0 : 1) && !model.IsBusy, "Rollback releases the lease without unnecessary commit checks: cancel=" + cancel);
        }
        var service = new ScanFake(); using var vm = new WorkspaceViewModel(service, new InventoryFake(), new RefreshFake()); vm.Roots.Add(@"C:\scope"); await vm.ScanAsync(); var prior = vm.Scan;
        service.Next = () => { var result = Data.Outcome(); result.Scan.RefreshIndex = RefreshIndex.Capture(FailingSources(), [new(@"C:\scope", 1, 1, Data.Volume)], [], 1 << 20); return result; }; await vm.RemeasureAsync();
        Check(ReferenceEquals(vm.Scan, prior) && vm.HasError, "Full UI remeasurement cannot publish a captured allocation-failure descriptor");
        var refreshFailure = new RefreshFake(); using var workspace = new WorkspaceViewModel(new ScanFake(), new InventoryFake(), refreshFailure); workspace.Roots.Add(@"C:\scope"); workspace.RefreshCacheMiB = 64; await workspace.ScanAsync(); workspace.SelectedRow = workspace.Rows[0];
        workspace.SelectedRow = null; // Make selection mapping visit the malformed row instead of stopping at the old selection.
        var previous = workspace.Scan; var malformed = Data.Outcome(20, true); malformed.Scan.Root.Children.Add(null!); var failedPreparation = new LeaseFake(malformed); refreshFailure.Next = () => failedPreparation; await workspace.RefreshSelectedAsync();
        Check(ReferenceEquals(workspace.Scan, previous) && workspace.HasError && failedPreparation.Validations == 0 && failedPreparation.Disposals == 1, "Presentation preparation failure releases the lease before any commit validation or publication");
    }
    static IEnumerable<ScanResult> FailingSources() { yield return Fail(); }
    static ScanResult Fail() => throw new OutOfMemoryException("Controlled test exception");
    static async Task Lifetime()
    {
        var refresh = new RefreshFake(); var model = new WorkspaceViewModel(new ScanFake(), new InventoryFake(), refresh); model.Roots.Add(@"C:\scope"); model.RefreshCacheMiB = 64; await model.ScanAsync(); var old = model.Scan;
        refresh.Pending = Data.Gate(); var running = model.RefreshSelectedAsync(); model.Dispose(); var lease = new LeaseFake(Data.Outcome(20, true)); refresh.Pending.SetResult(lease); await running;
        Check(ReferenceEquals(model.Scan, old) && lease.Disposals == 1 && lease.Validations == 0, "Closing workspace rejects a late selected result and releases native ownership");
    }
    static async Task Native(string output)
    {
        string root = Path.Combine(output, "own-fixtures", "native"), a = Path.Combine(root, "a"), b = Path.Combine(root, "b"), skip = Path.Combine(root, "skip"); Directory.CreateDirectory(a); Directory.CreateDirectory(b); Directory.CreateDirectory(skip);
        File.WriteAllBytes(Path.Combine(a, "one.pst"), new byte[10]); File.WriteAllBytes(Path.Combine(b, "two.bin"), new byte[20]); File.WriteAllBytes(Path.Combine(skip, "canary.bin"), new byte[999]);
        using var model = new WorkspaceViewModel(new NativeScanService(), new InventoryFake()); model.RefreshCacheMiB = 64; model.Roots.Add(root); model.Exclusions.Add(skip); await model.ScanAsync(); var old = model.Scan!; var time = model.MeasurementCompletedAt;
        Check(old.RefreshIndex is { Captured: true } && old.Root.LogicalBytes == 30, "Opt-in native UI scan captures an index while respecting exclusions");
        model.SelectedRow = model.Rows.OfType<FolderEntry>().Single(f => f.Path == a); File.WriteAllBytes(Path.Combine(a, "one.pst"), new byte[12]); File.WriteAllBytes(Path.Combine(b, "two.bin"), new byte[30]); model.Roots.Clear(); model.Roots.Add(Path.Combine(output, "never-selected"));
        await model.RefreshSelectedAsync();
        Check(!model.HasError && model.Scan!.Root.LogicalBytes == 32 && model.HasMixedMeasurements && old.Root.LogicalBytes == 30, "Real selected service updates only the target and atomically preserves older outside observations");
        Check(model.MeasurementCompletedAt == time && !model.CanCompareMeasurements && model.Scan!.RefreshIndex is { Captured: true }, "Real native mixed result keeps baseline time and its reusable index");
        var mixedRows = model.Rows; model.RefreshCacheMiB = 0;
        Check(model.HasMixedMeasurements && model.Scan!.RefreshIndex == null && !model.CanCompareMeasurements && ReferenceEquals(model.Rows, mixedRows), "Dropping an active cache cannot relabel hybrid measurements as one fresh scan");
        model.RefreshCacheMiB = 64; await model.RemeasureAsync();
        await model.EnterAsync(model.Rows.OfType<FolderEntry>().Single(f => f.Path == a)); File.WriteAllBytes(Path.Combine(a, "one.pst"), new byte[14]); await model.RefreshSelectedAsync();
        Check(model.RefreshTargetNotice.Contains(a) && model.RefreshTargetNotice.Contains("açık klasör") && model.RefreshActionLabel == "Klasörü yenile", "Opened leaf without a selection visibly declares itself as the target");
        Check(model.HasMixedMeasurements && model.FolderPath == a && model.Rows.Count == 0 && model.Scan!.Root.LogicalBytes == 44, "Opened leaf folder without a selected row refreshes itself and keeps navigation mapped to the new generation");
        await model.RemeasureAsync(); Check(model.Scan!.Root.LogicalBytes == 44 && !model.HasMixedMeasurements && model.CanCompareMeasurements, "Real full remeasurement updates all original roots and clears hybrid status");
        var full = model.Scan; var rows = model.Rows; var baseline = model.MeasurementCompletedAt; model.RefreshCacheMiB = 0;
        Check(ReferenceEquals(model.Scan, full) && full.RefreshIndex == null && ReferenceEquals(model.Rows, rows) && model.MeasurementCompletedAt == baseline && !model.CanRefreshSelected, "Disabling opt-in releases advisory cache without changing visible measurements");
    }
    static void Provenance()
    {
        var sequential=Data.Outcome(12,true).Scan;var start=new DateTimeOffset(2026,11,23,12,0,0,TimeSpan.Zero);
        sequential.RefreshedAreas=[new(@"C:\scope\a",start.AddMilliseconds(111)),new(@"C:\scope\b",start.AddMilliseconds(222))];
        var a=new FileEntry(@"C:\scope\a\one.bin",0,0,default,false,"");var b=new FileEntry(@"C:\scope\b\two.bin",0,0,default,false,"");
        Check(MeasurementProvenance.ForRow(sequential,start,a,true)=="Önceki yenileme","Earlier partial refresh has a distinct short provenance label");
        Check(MeasurementProvenance.ForRow(sequential,start,b,true)=="Yenilenen alan","Latest partial refresh retains its current-area label");
        Check(MeasurementProvenance.ForRow(sequential,start,a)!=MeasurementProvenance.ForRow(sequential,start,b),"Details distinguish partial refreshes within the same second");
        var scan = Data.Outcome(12, true).Scan; var baseline = DateTimeOffset.UtcNow.AddHours(-2); string nested = @"C:\scope\a\nested"; scan.RefreshedAreas = Array.AsReadOnly(scan.RefreshedAreas.Append(new RefreshAreaMeasurement(nested, DateTimeOffset.UtcNow.AddMinutes(1))).ToArray());
        Check(MeasurementProvenance.ForRow(scan, baseline, scan.Root.Children[0], true) == "Karma zaman", "A parent with newer descendant measurements is labeled as mixed rather than wholly refreshed");
        Check(MeasurementProvenance.ForRow(scan, baseline, new FileEntry(nested + @"\file.bin", 0, 0, default, false, ""), true) == "Yenilenen alan", "A file inside the most specific refreshed scope receives its actual measurement provenance");
    }
}
