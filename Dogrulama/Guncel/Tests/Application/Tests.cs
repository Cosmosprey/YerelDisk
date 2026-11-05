using System.Globalization;
using System.Reflection;
using YerelDisk;
using YerelDisk.Presentation;

internal static class ApplicationTests
{
    static readonly List<string> log = [];
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); log.Add("PASS " + name); }
    static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { Check(true, name); return; }
        throw new Exception(name);
    }
    static async Task<int> Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            Boundaries();
            await Workflow();
            await UserFlow();
            await Lifetime();
            await Native(output);
            log.Add($"ALL CHECKS PASSED ({log.Count})");
            File.WriteAllLines(Path.Combine(output, "results.txt"), log);
            Console.WriteLine(log[^1]); return 0;
        }
        catch (Exception ex) { log.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(output, "results.txt"), log); Console.WriteLine(ex); return 1; }
    }
    static void Boundaries()
    {
        foreach (Assembly assembly in new[] { typeof(DiskScanner).Assembly, typeof(WorkspaceViewModel).Assembly })
        {
            string[] names = assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
            Check(!names.Any(n => n is "PresentationFramework" or "PresentationCore" or "System.Windows.Forms" or "WindowsBase"), assembly.GetName().Name + " has no desktop UI dependency");
            Check(!names.Any(n => n.StartsWith("System.Net", StringComparison.Ordinal)), assembly.GetName().Name + " has no direct network dependency");
        }
        Check(typeof(DiskScanner).Assembly != typeof(WorkspaceViewModel).Assembly, "Engine and application state compile independently");
        Check(typeof(DiskScanner).Assembly.GetType("YerelDisk.PresentationText") == null && typeof(DiskScanner).Assembly.GetType("YerelDisk.ReviewText") == null, "Core does not contain formatting reporting or Explorer preparation");
        string[] roots = [@"C:\first"];
        var request = new ScanRequest(roots, 4, 100000); roots[0] = @"C:\changed";
        Check(request.Roots[0] == @"C:\first" && request.Workers == 4 && request.FileLimit == 100000, "Scan request snapshots mutable caller input");
        Throws<ArgumentOutOfRangeException>(() => new ScanRequest([@"C:\x"], 17, 1), "Worker count rejects invalid requests");
        Throws<ArgumentOutOfRangeException>(() => new ScanRequest([@"C:\x"], 1, 0), "File limit rejects invalid requests");
        Throws<ArgumentException>(() => new ScanRequest([], 1, 1), "Empty root request rejected");
    }
    static ScanOutcome Example(bool scopeChanged = false)
    {
        var root = new FolderEntry(@"C:\demo", null) { LogicalBytes = 111, AllocatedBytes = 4096, FileCount = 3, OwnStreams = 2, OwnStreamBytes = 73 };
        var child = new FolderEntry(@"C:\demo\reports", root) { LogicalBytes = 111, FileCount = 3 };
        root.Children.Add(child);
        var result = new ScanResult(root) { Workers = 4, ScopeChanged = scopeChanged };
        result.Files.AddRange([
            new(@"C:\demo\small.bin", 2, null, DateTime.MinValue, false, "Örnek"),
            new(@"C:\demo\big.bin", 100, 4096, DateTime.MinValue, false, "Örnek"),
            new(@"C:\demo\=formula.bin", 9, 4096, DateTime.MinValue, false, "Örnek")]);
        var candidates = new List<ReviewCandidate>
        {
            new(@"C:\demo\Package Cache", 100000000, 100000000, 8, "Kurulum verileri", "Bilinen konum", "PACKAGE-CACHE", "Bilinen konum.", "Elle silmeyin.", false, ReviewKind.Protected),
            new(@"C:\demo\.gradle\caches", 20000000, 20000000, 4, "Önbellek", "Bilinen konum", "GRADLE-CACHES", "Bilinen konum.", "Kullanımı doğrulayın.", false, ReviewKind.Cache)
        };
        return new(result, candidates);
    }
    static async Task Workflow()
    {
        var outcome = Example(); var fake = new FakeService { Handler = (_, _) => Task.FromResult(outcome) };
        using var model = new WorkspaceViewModel(fake); model.Roots.Add(@"C:\demo");
        await model.ScanAsync();
        Check(model.Scan == outcome.Scan && model.Rows.Count == 1 && model.AppliedQuery?.View == WorkspaceView.Folders, "Completed scan produces an applied folder view without a window");
        using (var writer = new StringWriter())
        {
            model.FullFolderPath = true; // Intent changes before the list has been refreshed.
            ReportSnapshot.Capture(model).Write(writer);
            Check(writer.ToString().Contains("Klasör adı") && !writer.ToString().Contains("Tam yol"), "CSV records applied folder policy rather than a pending setting");
            Check(writer.ToString().Contains("kendi ek akışları") && writer.ToString().Contains("73"), "Folder CSV includes its own alternate streams");
        }
        await model.SetViewAsync(WorkspaceView.Files);
        Check(((FileEntry)model.Rows[0]).Size == 100 && ((FileEntry)model.Rows[^1]).Size == 2, "File sizes sorted numerically");
        model.SelectedRow = model.Rows[1]; object selected = model.SelectedRow;
        await model.SortAsync("Name");
        Check(ReferenceEquals(model.SelectedRow, selected), "Sorting preserves the selected data object");
        model.Search = "small"; Task stale = model.RefreshAsync(true);
        model.Search = "big"; Task latest = model.RefreshAsync(); await Task.WhenAll(stale, latest);
        Check(model.Rows.Count == 1 && ((FileEntry)model.Rows[0]).Name == "big.bin" && model.AppliedSearch == "big", "Latest query wins over a delayed older search");
        Check(model.SelectedRow == null && !model.CanUseSelection, "Filtering a selection away disables path actions");
        model.Search = ""; await model.RefreshAsync();
        var snapshot = ReportSnapshot.Capture(model);
        await model.SetViewAsync(WorkspaceView.Review); model.ReviewMinimum = 0; await model.RefreshAsync();
        using (var writer = new StringWriter()) { snapshot.Write(writer); Check(writer.ToString().Contains("big.bin") && writer.ToString().Contains("\"'=formula.bin\"") && !writer.ToString().Contains("GRADLE-CACHES"), "Report snapshot survives later navigation and protects formulas"); }
        model.ReviewKindFilter = 3; // Again, this intent is not yet applied.
        using (var writer = new StringWriter()) { ReportSnapshot.Capture(model).Write(writer); Check(writer.ToString().Contains("Tüm türler") && writer.ToString().Contains("GRADLE-CACHES"), "Review CSV metadata and rows use the same applied filter"); }
        await model.RefreshAsync();
        Check(model.Rows.Count == 1 && ((ReviewCandidate)model.Rows[0]).Kind == ReviewKind.Protected, "Explicit protected kind survives the new query layer");
        model.SelectedRow = model.Rows[0];
        Check(model.FullDetails.Contains("Neyi bilmiyoruz?") && model.FullDetails.Contains("Elle silmeyin"), "Shared review explanation preserves risk and uncertainty");
        Check(model.SortDescription.Contains("Boyut") && !model.SortDescription.Contains("Size"), "Report sort labels are user-facing Turkish");
        fake.Handler = (_, _) => Task.FromResult(Example(true));
        await model.ScanAsync();
        Check(model.Rows.Count == 0 && !model.CanUseSelection && model.Status.Contains("Kapsam değişti"), "Scope change hides review rows and blocks path actions");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Throws<OperationCanceledException>(() => ResultQuery.Execute(outcome.Scan, outcome.Candidates, new(WorkspaceView.Files, null, "", false, 0, 0, "Size", true), cancelled.Token), "Already-cancelled query does not enumerate results");
        Check(outcome.Scan.Files[0].Name == "small.bin", "Sorting never mutates engine result order");
    }
    static async Task UserFlow()
    {
        var outcome = Example();
        var fake = new FakeService { Handler = (_, _) => Task.FromResult(outcome) };
        using var model = new WorkspaceViewModel(fake); model.Roots.Add(@"C:\demo"); await model.ScanAsync();
        model.Search = "reports"; await model.RefreshAsync(); model.SelectedRow = model.Rows[0];
        var savedFolder = model.SelectedRow; await model.SortAsync("Name");
        await model.SetViewAsync(WorkspaceView.Files);
        Check(model.Search == "" && model.RowCount == 3, "File view starts with its own search rather than inheriting the folder filter");
        model.Search = "big"; await model.RefreshAsync(); model.SelectedRow = model.Rows[0]; var savedFile = model.SelectedRow;
        await model.SortAsync("Name");
        await model.SetViewAsync(WorkspaceView.Review);
        Check(model.Search == "" && model.RowCount == 2, "Review view does not inherit the file search");
        model.SelectedRow = model.Rows.OfType<ReviewCandidate>().Single(c => c.Kind == ReviewKind.Protected);
        var candidate = (ReviewCandidate)model.SelectedRow;
        var details = model.Details;
        Check(details.Sections[0].Caution && details.Sections[0].Text == ReviewText.Explain(candidate).Consequence, "First candidate section preserves the complete shared consequence text");
        Check(details.Sections.Any(s => s.Text == ReviewText.Explain(candidate).Purpose) && details.Sections.Any(s => s.Text == ReviewText.Explain(candidate).Unknown), "Structured details preserve purpose and uncertainty");
        Check(ReferenceEquals(details, model.Details), "Repeated detail reads reuse one selection model");
        model.Feedback = "Tutacağım";
        Check(!ReferenceEquals(details, model.Details) && model.FullDetails.Contains("Tutacağım"), "Session feedback refreshes the full copyable explanation");
        await model.SetViewAsync(WorkspaceView.Files);
        Check(model.Search == "big" && model.SortKey == "Name" && ReferenceEquals(model.SelectedRow, savedFile), "Returning to file view restores its filter sort and selected record");
        await model.SetViewAsync(WorkspaceView.Folders);
        Check(model.Search == "reports" && ReferenceEquals(model.SelectedRow, savedFolder), "Returning to folders restores its independent filter and selection");
        await model.EnterAsync((FolderEntry)savedFolder!);
        Check(model.Search == "" && model.CanGoUp && model.FolderPath.EndsWith("reports"), "Entering a folder clears the parent-name filter");
        await model.GoUpAsync(); Check(model.Search == "" && model.RowCount == 1, "Going up restores unfiltered children");
        string previousPath = model.FolderPath;
        await model.EnterAsync(new FolderEntry(@"C:\outside", null));
        Check(model.FolderPath == previousPath, "An unrelated folder cannot be entered through stale input");
        await model.SetViewAsync(WorkspaceView.Files);
        await model.EnterAsync(outcome.Scan.Root.Children[0]);
        Check(model.View == WorkspaceView.Files, "File view does not perform folder navigation");
        model.Search = ""; Task pending = model.RefreshAsync(true);
        Check(!model.CanUseSelection && !model.CanExport, "Selection and export actions wait for the current view query");
        await pending;
        Task navReview = model.SetViewAsync(WorkspaceView.Review), navFolders = model.SetViewAsync(WorkspaceView.Folders), navFiles = model.SetViewAsync(WorkspaceView.Files);
        await Task.WhenAll(navReview, navFolders, navFiles);
        Check(model.View == WorkspaceView.Files && model.Rows.All(r => r is FileEntry), "Rapid page switches cannot publish rows from the previous page");
        fake.Handler = (_, _) => Task.FromResult(Example()); await model.ScanAsync();
        await model.SetViewAsync(WorkspaceView.Folders);
        Check(model.Search == "" && model.SelectedRow == null, "New scan discards old per-page search and object selections");
        var incomplete = Example(); incomplete.Scan.Cancelled = true; incomplete.Scan.UnknownAllocation = 1;
        fake.Handler = (_, _) => Task.FromResult(incomplete); await model.ScanAsync();
        Check(model.ShowScopeNotice && model.ScopeNotice.Contains("kısmi") && model.ScopeNotice.Contains("eksik"), "Partial and unknown measurements have an explicit scope notice");
        await model.SetViewAsync(WorkspaceView.Settings);
        Check(!model.ShowMetrics && !model.ShowScopeNotice, "Settings leave room for configuration rather than scan cards");
        var linked = new FileEntry(@"C:\demo\disk.vhdx", 50, null, DateTime.MinValue, true, "Sanal disk: kişisel veri içerebilir.", 2, 20);
        var fileDetails = SelectionDetails.Create(outcome.Scan, linked, false, "", model.ScopeText);
        Check(fileDetails.Sections[0].Caution && fileDetails.PlainText.Contains("kişisel veri") && fileDetails.PlainText.Contains("Hard link"), "File hints and shared-storage risk both survive structured rendering");
        Check(fileDetails.PlainText.Contains("Tarih bilinmiyor") && fileDetails.PlainText.Contains("Ölçülemedi") && fileDetails.PlainText.Contains("20 B"), "Unknown date allocation and alternate streams remain explicit");
        var scope = Example(true);
        var changed = SelectionDetails.Create(scope.Scan, candidate, true, "", "Kapsam değişti");
        Check(changed.Path == "" && !changed.FullText.Contains(candidate.Path) && changed.Sections[0].Caution, "Changed scope detail model suppresses the stale selected path");
    }
    static async Task Lifetime()
    {
        var pending = new TaskCompletionSource<ScanOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeService { Handler = (_, _) => pending.Task };
        var model = new WorkspaceViewModel(fake); model.Roots.Add(@"C:\demo"); model.Workers = 6; model.FileLimit = 50000;
        Task running = model.ScanAsync();
        Check(model.IsBusy && model.CanStop && !model.CanExport, "Pending scan exposes busy state without UI controls");
        model.Workers = 16; model.FileLimit = 500000;
        Check(fake.Request is { Workers: 6, FileLimit: 50000 }, "In-flight scan settings cannot change with later preferences");
        Throws<InvalidOperationException>(() => ReportSnapshot.Capture(model), "Busy scan cannot export a stale list");
        model.CancelScan(); Check(fake.Token.IsCancellationRequested, "Cancel reaches the injected scan service");
        var result = Example(); result.Scan.Cancelled = true; pending.SetResult(result); await running;
        Check(model.Scan is { Cancelled: true } && model.Status.Contains("kısmi") && !model.IsBusy, "Cancelled outcome remains explicitly partial");
        var late = new TaskCompletionSource<ScanOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Handler = (_, _) => late.Task;
        Task closing = model.ScanAsync(); model.Dispose(); late.SetResult(Example()); await closing;
        Check(model.Scan == null && !model.CanScan && !model.CanSearch, "Disposed session ignores a late scan completion");
        Throws<ObjectDisposedException>(() => model.ScanAsync(), "Disposed session cannot start another scan");
        var failing = new FakeService { Handler = (_, _) => Task.FromException<ScanOutcome>(new IOException("örnek hata")) };
        using var failed = new WorkspaceViewModel(failing); failed.Roots.Add(@"C:\demo"); await failed.ScanAsync();
        Check(failed.HasError && failed.Error.Contains("örnek hata") && failed.Scan == null && !failed.CanExport && !failed.IsBusy, "Service errors release busy state without false successful results");
    }
    static async Task Native(string output)
    {
        string root = Path.Combine(output, "native-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "six.bin"), new byte[6]);
        var native = new NativeScanService();
        var outcome = await native.ScanAsync(new(native.NormalizeRoots([root]), 1, 10), null, CancellationToken.None);
        Check(outcome.Scan.Root.FileCount == 1 && outcome.Scan.Root.LogicalBytes == 6 && !outcome.Scan.Failed, "Shared service calls the unchanged native engine on a real fixture");
        using var token = new CancellationTokenSource(); token.Cancel();
        var cancelled = await native.ScanAsync(new([root], 1, 10), null, token.Token);
        Check(cancelled.Scan.Cancelled && !cancelled.Scan.ScopeChanged, "Native service preserves partial result for pre-cancelled requests");
    }
    sealed class FakeService : IScanService
    {
        internal Func<ScanRequest, CancellationToken, Task<ScanOutcome>> Handler { get; set; } = (_, _) => Task.FromResult(Example());
        internal ScanRequest? Request { get; private set; }
        internal CancellationToken Token { get; private set; }
        public string[] NormalizeRoots(IEnumerable<string> paths) => paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token) { Request = request; Token = token; return Handler(request, token); }
    }
}
