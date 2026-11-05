using System.Globalization;
using YerelDisk;
using YerelDisk.Presentation;

internal static class SummaryTests
{
    static readonly List<string> log = [];
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); log.Add("PASS " + name); }
    static ScanResult Sample() => new(new FolderEntry(@"C:\example", null) { LogicalBytes = 8192, AllocatedBytes = 4096, FileCount = 2 })
    { Folders = 3, Elapsed = TimeSpan.FromSeconds(10), Timings = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2)) };
    static void TimingInvariant(ScanResult result, string name)
    {
        var timing = result.Timings ?? throw new Exception("Missing timing: " + name);
        Check(timing.InitialScan > TimeSpan.Zero && timing.Verification >= TimeSpan.Zero && timing.Refresh >= TimeSpan.Zero
            && timing.InitialScan + timing.Verification + timing.Refresh <= result.Elapsed, "Non-overlapping wall-time accounting: " + name);
    }
    static async Task<int> Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        string fixtures = Path.Combine(output, "own-fixtures"); Directory.CreateDirectory(fixtures);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            var sample = Sample(); var summary = ScanSummary.Create(sample);
            Check(summary.Timing.Contains("İlk tarama 3,00 sn") && summary.Timing.Contains("Doğrulama 4,00 sn")
                && summary.Timing.Contains("bekleme 2,00 sn") && summary.Timing.Contains("Diğer hazırlık 1,00 sn"), "Each phase and residual are displayed separately");
            sample.VerifiedFolders = 7; sample.RefreshedFolders = 2;
            Check(ScanSummary.Create(sample).Coverage.Contains("3 gözlenen klasör · 7 klasör kontrolü · 2 klasör yenilemesi (turlar toplamı)"), "Visits and refresh operations do not claim distinct-folder counts");
            sample.Root.Measurement = MeasurementState.Unknown;
            Check(ScanSummary.Create(sample).Totals.Contains("bilinmiyor") && !ScanSummary.Create(sample).Totals.Contains("8,00"), "Unknown root suppresses numeric totals");
            sample.Root.Measurement = MeasurementState.Previous;
            Check(ScanSummary.Create(sample).Totals.Contains("son ölçüm") && ScanSummary.Create(sample).Heading.Contains("kısmi"), "Previous observation is qualified in totals and heading");
            sample.Root.Measurement = MeasurementState.Partial;
            Check(ScanSummary.Create(sample).Totals.Contains("kısmi") && ScanSummary.Create(sample).Heading.Contains("kısmi"), "Partial observation is qualified");
            foreach (string state in new[] { "cancel", "scope", "failed", "retry", "unverified", "warning", "allocation" })
            {
                var result = Sample();
                switch (state)
                {
                    case "cancel": result.Cancelled = true; break;
                    case "scope": result.ScopeChanged = true; break;
                    case "failed": result.Failed = true; break;
                    case "retry": result.NeedsRetry = true; break;
                    case "unverified": result.UnverifiedPaths.Add(result.Root.Path); break;
                    case "warning": result.IssueCount = 1; break;
                    case "allocation": result.UnknownAllocation = 1; break;
                }
                string heading = ScanSummary.Create(result).Heading;
                Check(state is "warning" or "allocation" ? heading.Contains("uyarı") : heading.Contains("kısmi"), "Non-clean status retained: " + state);
            }
            sample = Sample(); sample.Timings = null;
            Check(ScanSummary.Create(sample).Timing.Contains("mevcut değil"), "Unavailable phase data is not synthesized as zero");
            Check(ScanSummary.Duration(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5)).Contains("3.605,00"), "Durations do not wrap at an hour");

            foreach (int workers in new[] { 1, 4 })
            {
                string root = Path.Combine(fixtures, "quiet-" + workers); Directory.CreateDirectory(Path.Combine(root, "child"));
                File.WriteAllBytes(Path.Combine(root, "child", "data.bin"), new byte[64]);
                var events = new List<ScanProgress>();
                var result = VerifiedScan.Scan([root], new Forward(p => { lock (events) events.Add(p); }), CancellationToken.None, 10, workers);
                TimingInvariant(result, "quiet-" + workers);
                Check(result.Root.LogicalBytes == 64 && result.Root.FileCount == 1 && result.Timings!.Refresh == TimeSpan.Zero
                    && result.Timings.Verification > TimeSpan.Zero, "Quiet scan totals and zero refresh: " + workers);
                Check(events.Any(p => p.Phase == ScanPhase.InitialScan) && events.Any(p => p.Phase == ScanPhase.Verification)
                    && events[^1].Phase == ScanPhase.Finalizing, "Structured progress stages: " + workers);
            }
            string dirty = Path.Combine(fixtures, "dirty"); Directory.CreateDirectory(dirty);
            File.WriteAllBytes(Path.Combine(dirty, "first.bin"), new byte[8]); bool changed = false;
            var repaired = VerifiedScan.Scan([dirty], new Forward(p =>
            {
                if (!changed && p.Phase == ScanPhase.Verification && p.Current == "Doğrulama: " + dirty)
                { changed = true; File.WriteAllBytes(Path.Combine(dirty, "late.bin"), new byte[16]); }
            }), CancellationToken.None, 10, 4);
            TimingInvariant(repaired, "repair");
            Check(changed && repaired.Root.FileCount == 2 && repaired.Root.LogicalBytes == 24 && repaired.RefreshedFolders == 1, "One-folder repair keeps exact totals");
            Check(repaired.Timings!.Refresh.TotalMilliseconds >= 200 && repaired.VerificationRounds == 2, "Bounded retry wait belongs to refresh timing");
            foreach (var at in new[] { ScanPhase.InitialScan, ScanPhase.Verification })
            {
                using var cancellation = new CancellationTokenSource();
                var result = VerifiedScan.Scan([dirty], new Forward(p => { if (p.Phase == at) cancellation.Cancel(); }), cancellation.Token, 10, 4);
                TimingInvariant(result, "cancel-" + at);
                Check(result.Cancelled && ScanSummary.Create(result).Heading.Contains("kısmi") && result.Timings!.Refresh == TimeSpan.Zero, "Cancellation retains partial state and phase timing: " + at);
                if (at == ScanPhase.InitialScan) Check(result.Timings!.Verification == TimeSpan.Zero, "Verification not started on early cancellation");
            }
            var missing = VerifiedScan.Scan([Path.Combine(fixtures, "absent")], null, CancellationToken.None, 10, 1);
            Check(missing.Failed && missing.Timings == null && ScanSummary.Create(missing).Totals.Contains("bilinmiyor"), "Unavailable root is unknown without invented phase timing");
            using (var cancellation = new CancellationTokenSource())
            {
                bool altered = false;
                var result = VerifiedScan.Scan([dirty], new Forward(p =>
                {
                    if (!altered && p.Phase == ScanPhase.Verification && p.Current == "Doğrulama: " + dirty)
                    { altered = true; File.WriteAllBytes(Path.Combine(dirty, "before-cancel.bin"), new byte[4]); }
                    if (p.Phase == ScanPhase.Refresh) cancellation.Cancel();
                }), cancellation.Token, 10, 4);
                TimingInvariant(result, "cancel-refresh");
                Check(altered && result.Cancelled && result.RefreshedFolders == 0 && ScanSummary.Create(result).Heading.Contains("kısmi"), "Cancel before refresh wait neither claims repair nor clean completion");
            }
            var multi = VerifiedScan.Scan([Path.Combine(fixtures, "quiet-1"), Path.Combine(fixtures, "quiet-4")], null, CancellationToken.None, 10, 4);
            TimingInvariant(multi, "multi-root");
            Check(multi.Root.FileCount == 2 && multi.Root.LogicalBytes == 128 && multi.Timings!.Refresh == TimeSpan.Zero, "Multi-root timing aggregation preserves measurements");
            var outcome = await new NativeScanService().ScanAsync(new([dirty], 1, 10), null, CancellationToken.None);
            TimingInvariant(outcome.Scan, "service");

            var fake = new FakeService { Next = Task.FromResult(new ScanOutcome(Sample(), [])) };
            using var model = new WorkspaceViewModel(fake); model.Roots.Add(@"C:\example");
            Check(!model.ShowSummary && model.Summary == null, "Initial workspace has no stale summary");
            await model.ScanAsync(); Check(model.ShowSummary && model.Summary != null, "Completed workspace exposes summary");
            var report = ReportSnapshot.Capture(model); using var csv = new StringWriter(); report.Write(csv);
            Check(csv.ToString().Contains("İlk tarama (saniye)\";\"3.00") && csv.ToString().Contains("Doğrulama (saniye)\";\"4.00"), "CSV phase values use invariant decimal notation");
            await model.SetViewAsync(WorkspaceView.Settings); Check(!model.ShowSummary, "Settings do not show result summary");
            await model.SetViewAsync(WorkspaceView.Files); Check(model.ShowSummary, "Summary persists across result views");
            var pending = new TaskCompletionSource<ScanOutcome>(TaskCreationOptions.RunContinuationsAsynchronously); fake.Next = pending.Task;
            var running = model.ScanAsync(); Check(model.IsBusy && !model.ShowSummary && model.Summary == null, "New scan clears old summary before awaiting result");
            var stopped = Sample(); stopped.Cancelled = true; pending.SetResult(new(stopped, [])); await running;
            Check(model.ShowSummary && model.Summary!.Heading.Contains("Durduruldu"), "Cancelled workspace summary is not a clean completion");
            log.Add($"SUMMARY CHECKS PASSED ({log.Count})"); Console.WriteLine(log[^1]); return 0;
        }
        catch (Exception ex) { log.Add("FAIL " + ex); Console.WriteLine(ex); return 1; }
        finally { File.WriteAllLines(Path.Combine(output, "results.txt"), log); }
    }
    sealed class Forward(Action<ScanProgress> report) : IProgress<ScanProgress> { public void Report(ScanProgress progress) => report(progress); }
    sealed class FakeService : IScanService
    {
        internal required Task<ScanOutcome> Next;
        public string[] NormalizeRoots(IEnumerable<string> paths) => paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token) => Next;
    }
}
