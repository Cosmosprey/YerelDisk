using System.Diagnostics;
using YerelDisk;
using YerelDisk.Presentation;

internal static class OtherAreaTests
{
    static readonly List<string> log = [];
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); log.Add("PASS " + label); }
    static FolderEntry Add(FolderEntry parent, string name, long size = 100)
    { var node = new FolderEntry(Path.Combine(parent.Path, name), parent) { LogicalBytes = size, AllocatedBytes = size, FileCount = 1 }; parent.Children.Add(node); return node; }
    static ReviewCandidate Known(FolderEntry f, ReviewKind kind = ReviewKind.Cache) => new(f.Path, f.LogicalBytes, f.AllocatedBytes, f.FileCount, "fixture", "fixture", "GRADLE-CACHES", "fixture", "fixture", false, kind);
    static ViewQuery Query(ScanResult s) => new(WorkspaceView.Review, s.Root, "", false, 0, 0, "Size", true, true);
    static UnrecognizedAreas Areas(ScanResult s, params ReviewCandidate[] known) => UnrecognizedAreas.Create(s, known, default);
    static async Task<int> Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try
        {
            var root = new FolderEntry(@"C:\Area-Fixture", null) { LogicalBytes = 10000 };
            var apps = Add(root, "Applications", 5000); var ide = Add(apps, "IDE", 4000);
            var user = Add(root, "User", 4000); var tools = Add(user, "Tools", 1000);
            var cache = Add(tools, "cache", 800); var logs = Add(tools, "logs", 200);
            var work = Add(user, "Work", 3000); var video = Add(work, "Video", 3000);
            var scan = new ScanResult(root); var known = Known(cache);
            var areas = Areas(scan, known);
            Check(areas.Folders.Count == 3 && areas.Folders.Contains(apps) && areas.Folders.Contains(logs) && areas.Folders.Contains(work), "Known descendant splits only its ancestors; independent unknown siblings remain discoverable");
            Check(!areas.Folders.Contains(ide) && !areas.Folders.Contains(video) && !areas.Folders.Contains(user), "Unknown ancestor/descendant sizes do not appear twice");
            Check(!areas.Folders.Any(f => ScanExclusions.SameOrBelow(f.Path, cache.Path) || ScanExclusions.SameOrBelow(cache.Path, f.Path)), "Known subtree is absent both directly and inside aggregate rows");
            Check(areas.Folders.Sum(f => f.LogicalBytes) == 8200, "Discovery rows reuse existing sizes without claiming the omitted direct data");
            Check(UnrecognizedAreas.Coverage.Contains("doğrudan dosyaları") && UnrecognizedAreas.Coverage.Contains("Büyük dosyalar"), "Direct data omitted by partitioning is explicitly described");
            Check(Areas(scan).Folders.Count == 2 && Areas(scan).Folders.Contains(user), "No known rules still expands selected root once, not the whole drive as one row");
            Check(Areas(scan, Known(root)).Folders.Count == 0, "Known selected root suppresses its entire subtree");
            Check(Areas(scan, Known(apps, ReviewKind.Protected)).Folders.All(f => !ScanExclusions.SameOrBelow(f.Path, apps.Path)), "Protected recognized subtree is not relabeled as an unknown area");
            var leaf = new FolderEntry(@"D:\Leaf", null) { LogicalBytes = 23, FileCount = 1 };
            Check(Areas(new(leaf)).Folders.Single() == leaf, "Selected leaf root with direct files remains visible");
            Check(Areas(new(new FolderEntry(@"D:\Empty", null))).Folders.Count == 0, "Genuinely empty measured leaf does not create a discovery row");
            leaf.Measurement = MeasurementState.Partial;
            Check(Areas(new(leaf)).Folders.Single().Measurement == MeasurementState.Partial, "Safe partial leaf retains its measurement state");
            leaf.Measurement = MeasurementState.Unknown; Check(Areas(new(leaf)).Folders.Count == 0, "Unknown measurement is not zero or an actionable discovery row");
            leaf.Measurement = MeasurementState.Previous; Check(Areas(new(leaf)).Folders.Count == 0, "Previous unverified measurement is not a current discovery row");
            scan.ExcludedPaths = [apps.Path];
            Check(!Areas(scan, known).Folders.Contains(apps), "Explicit excluded subtree remains absent");
            scan.ExcludedPaths = []; scan.UnverifiedPaths.Add(apps.Path);
            Check(!Areas(scan, known).Folders.Contains(apps), "Unverified subtree remains absent");
            scan.ScopeChanged = true; Check(Areas(scan, known).Folders.Contains(work), "Locally reconciled scope change retains safe siblings");
            scan.UnverifiedPaths.Clear(); Check(Areas(scan, known).Folders.Count == 0, "Unresolved global scope change suppresses discovery"); scan.ScopeChanged = false;
            var broken = Add(root, "broken", 50); broken.Children.Add(new(@"C:\Outside-Selected\large", broken) { LogicalBytes = 999 });
            Check(!Areas(scan, known).Folders.Contains(broken), "Malformed foreign child cannot become an aggregate unknown row");
            broken.Children.Clear(); broken.Children.Add(new(broken.Path + "\\bad\0name", broken));
            Check(!Areas(scan, known).Folders.Contains(broken), "Invalid metadata path does not crash or produce a reassuring parent row");
            root.Children.Remove(broken);
            var multiRoot = new FolderEntry("Seçili konumlar", null); var a = new FolderEntry(@"C:\A", multiRoot); var b = new FolderEntry(@"D:\B", multiRoot); multiRoot.Children.AddRange([a, b]); var aData = Add(a, "Data", 200); var bData = Add(b, "Data", 300); var multi = new ScanResult(multiRoot); multi.RootPaths.Clear(); multi.RootPaths.AddRange([a.Path, b.Path]);
            Check(Areas(multi).Folders.Count == 2 && Areas(multi).Folders.Contains(aData) && Areas(multi).Folders.Contains(bData), "Multiple selected roots are partitioned without exposing synthetic display root");
            Check(Areas(multi, Known(aData)).Folders.SequenceEqual([bData]), "Known data on one root does not hide unrelated second disk");
            Check(Areas(new(new FolderEntry(@"\\server\share", null))).Folders.Count == 0, "Non-local scope cannot create discovery rows");
            using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); bool stopped = false; try { UnrecognizedAreas.Create(scan, [known], canceled.Token); } catch (OperationCanceledException) { stopped = true; } Check(stopped, "Canceled preparation stops rather than publishing a partial catalog"); }
            var query = Query(scan);
            var result = ResultQuery.Execute(scan, [known], query, default);
            Check(result.Rows.SequenceEqual(new object[] { apps, work, logs }), "Default discovery ordering uses descending size");
            Check(ResultQuery.Execute(scan, [known], query with { ReviewKind = 3 }, default).Rows.Count == 3, "Hidden old kind filter cannot remove unknown-area rows");
            Check(ResultQuery.Execute(scan, [known], query with { ReviewMinimum = 1000 }, default).Rows.Count == 2, "Size threshold applies to discovery folders");
            Check(ResultQuery.Execute(scan, [known], query with { Search = "User\\Tools" }, default).Rows.Single() == logs, "Discovery search uses full path, regardless of folder-name search setting");
            Check(ResultQuery.Execute(scan, [known], query with { Search = "Video" }, default).Rows.Count == 0 && UnrecognizedAreas.Coverage.Contains("hazırlanan bu listeye uygulanır"), "Search filters the frontier rather than inventing a complete descendant search");
            Check(ReferenceEquals(result.Areas, ResultQuery.Execute(scan, [known], query, default, result.Areas).Areas), "Filtering can reuse the immutable catalog without rebuilding the tree");
            var fake = new FakeService(new(scan, [known])); using var model = new WorkspaceViewModel(fake); model.Roots.Add(root.Path); await model.ScanAsync(); model.ReviewMinimum = 0; await model.SetViewAsync(WorkspaceView.Review);
            Check(model.IsKnownReview && ReferenceEquals(model.Rows.Single(), known), "Existing recognized candidates remain default and unchanged");
            model.Search = "cache"; await model.RefreshAsync(); await model.SetReviewCollectionAsync(1);
            Check(model.IsUnrecognized && model.Search == "" && model.Rows.Count == 3 && model.Subtitle.Contains("silme önerisi değildir"), "Mode switch clears unrelated filters and clearly labels uncertainty");
            model.SelectedRow = apps;
            Check(model.Details.Sections[0].Caution && model.Details.FullText.Contains("Sistem, uygulama veya kişisel veri") && model.Details.FullText.Contains("doğrudan dosyaları"), "F6 uses conservative meaning and full partition coverage");
            model.Feedback = "Tutacağım"; Check(model.Feedback == "Değerlendirilmedi", "Unknown folder does not become a recognized rule through personal feedback");
            var report = ReportSnapshot.Capture(model); var writer = new StringWriter(); report.Write(writer); string csv = writer.ToString();
            Check(report.Unrecognized && report.Items.Length == 3 && csv.Contains("Diğer büyük alanlar") && csv.Contains("Listenin kapsamı") && !csv.Contains("Nasıl incelemeli?"), "Unknown-area CSV identifies its own folder schema and limitations");
            await model.SetReviewCollectionAsync(0); Check(model.Search == "cache" && ReferenceEquals(model.Rows.Single(), known), "Recognized mode restores its own search and rows");
            await model.SetReviewCollectionAsync(1); model.SelectedRow = apps; await model.EnterAsync(apps);
            Check(model.View == WorkspaceView.Folders && model.Rows.Single() == ide && model.CanGoUp, "Discovery double-click target enters ordinary folder navigation");
            await model.SetViewAsync(WorkspaceView.Review); Check(model.IsUnrecognized && model.Rows.Contains(apps), "Returning to review restores discovery mode");
            var old = model.SelectedRow; model.Search = "Work"; var slow = model.RefreshAsync(true); var swap = model.SetReviewCollectionAsync(0); await Task.WhenAll(slow, swap);
            Check(model.IsKnownReview && model.AppliedQuery is { Unrecognized: false } && model.Rows.All(r => r is ReviewCandidate), "Superseded discovery query cannot overwrite recognized mode");
            var freshRoot = new FolderEntry(root.Path, null); var freshChild = Add(freshRoot, "New-scan-only", 500);
            fake.Outcome = new(new(freshRoot), []); await model.ScanAsync(); await model.SetReviewCollectionAsync(1);
            Check(model.Rows.Count == 1 && ReferenceEquals(model.Rows[0], freshChild) && !model.Rows.Contains(apps), "New scan discards cached frontier and every old folder reference");
            var big = new FolderEntry(@"C:\Flat-Test", null); for (int i = 0; i < 100000; i++) Add(big, "folder-" + i, i + 1);
            long before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew(); var bigAreas = Areas(new(big)); watch.Stop(); long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            File.WriteAllText(Path.Combine(output, "cost.txt"), $"100000 folders; preparation {watch.Elapsed.TotalMilliseconds:F1} ms; thread allocation {allocated:N0} B; no I/O; not a full app memory/real disk benchmark.");
            Check(bigAreas.Folders.Count == 100000 && bigAreas.VisitedFolders == 100001 && allocated < 256L * 1024 * 1024, "100k flat folder preparation retains one row per frontier folder with bounded temporary allocations");
            log.Add($"ALL CHECKS PASSED ({log.Count})"); File.WriteAllLines(Path.Combine(output, "results.txt"), log); Console.WriteLine(log[^1]); return 0;
        }
        catch (Exception ex) { log.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(output, "results.txt"), log); Console.WriteLine(ex); return 1; }
    }
    sealed class FakeService(ScanOutcome outcome) : IScanService
    { internal ScanOutcome Outcome { get; set; } = outcome; public string[] NormalizeRoots(IEnumerable<string> paths) => paths.ToArray(); public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token) => Task.FromResult(Outcome); }
}
