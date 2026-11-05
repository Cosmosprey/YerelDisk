using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using YerelDisk;
using YerelDisk.Presentation;

static class RecoveryTests
{
    static string output = "", fixtures = "";
    static int failures;
    static readonly List<string> log = [];
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); log.Add("PASS " + label); }
    static string Dir(string name) { string path = Path.Combine(fixtures, name); Directory.CreateDirectory(path); return path; }
    static void Data(string directory, string name, int size) => File.WriteAllBytes(Path.Combine(directory, name), new byte[size]);
    static ScanResult Scan(string[] roots, Action<ScanProgress>? action = null, CancellationToken token = default, int limit = 100, int workers = 4)
        => VerifiedScan.Scan(roots, action == null ? null : new Progress(action), token, limit, workers);
    static int Main(string[] args)
    {
        output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        fixtures = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixtures);
        foreach (Action test in new Action[] { Fingerprint, Stable, EntryIdentity, RootClassification, LargeAddition, Growth, RankingAfterShrink, Persistent, PersistentChild, CancelValidation, CancelRetry, RootReplacement, JunctionReplacement, HardLinks, DeniedRoot, FallbackObservation, LostChildAccess, MissingRoot, () => Presentation().GetAwaiter().GetResult(), Performance })
        {
            if (args.Length > 1 && !args[1].Split(',').Contains(test.Method.Name)) continue;
            try { test(); }
            catch (Exception ex) { failures++; log.Add("FAIL " + test.Method.Name + ": " + ex); }
            File.WriteAllLines(Path.Combine(output, "results.txt"), log);
        }
        log.Add($"RESULT PASS={log.Count(l => l.StartsWith("PASS "))}; Failures={failures}");
        File.WriteAllLines(Path.Combine(output, "results.txt"), log); Console.WriteLine(log[^1]); return failures == 0 ? 0 : 1;
    }
    static void Fingerprint()
    {
        var a = new NativeEntry("a.bin", 1, DateTime.MinValue, FileAttributes.Normal, 1);
        var b = new NativeEntry("b.bin", 2, DateTime.MinValue, FileAttributes.Normal, 2);
        DirectoryFingerprint first = default, reverse = default, changed = default;
        first.Add(a); first.Add(b); reverse.Add(b); reverse.Add(a); changed.Add(a); changed.Add(b with { Size = 9 });
        Check(first == reverse, "Metadata fingerprint ignores enumeration ordering");
        Check(first != changed, "Metadata fingerprint detects size change");
        DirectoryFingerprint directoryA = default, directoryB = default;
        directoryA.Add(a with { Attributes = FileAttributes.Directory }); directoryB.Add(a with { Attributes = FileAttributes.Directory, Modified = DateTime.Now });
        Check(directoryA == directoryB, "Directory timestamps are not a false change signal; directory content has its own fingerprint");
    }
    static void Stable()
    {
        string root = Dir("stable"), child = Path.Combine(root, "child"); Directory.CreateDirectory(child);
        Data(root, "a.bin", 31); Data(child, "b.bin", 12);
        var one = Scan([root], workers: 1); var four = Scan([root]);
        log.Add($"DIAG stable one: files={one.Root.FileCount}; bytes={one.Root.LogicalBytes}; retry={one.RetryCount}; needs={one.NeedsRetry}; state={one.Root.Measurement}; unsafe={one.UnverifiedPaths.Count}; issues={string.Join(" | ",one.Issues.Select(i=>i.Path+": "+i.Reason))}; history={string.Join(" | ",one.VerificationHistory)}");
        Check(one.Root.FileCount == 2 && one.Root.LogicalBytes == 43 && !one.HasUnverified && one.RetryCount == 0, "Stable tree completes without retries or uncertainty");
        Check(four.Root.LogicalBytes == one.Root.LogicalBytes && four.Root.AllocatedBytes == one.Root.AllocatedBytes, "One/four worker verified totals agree");
        Check(four.CanUsePath(Path.Combine(child, "b.bin")), "Validated paths remain actionable");
    }
    static void LargeAddition()
    {
        string root = Dir("addition"); Data(root, "small.bin", 17); bool done = false;
        var result = Scan([root], p => { if (!done && p.Current.Contains("Doğrulama: ")) { done = true; Data(root, "huge.bin", 8192); } }, limit: 1);
        Check(result.RetryCount == 1 && !result.NeedsRetry && !result.HasUnverified, "Change after first measurement is remeasured once and resolved");
        Check(result.Root.FileCount == 2 && result.Root.LogicalBytes == 8209, "Replacement attempt does not double count old totals");
        Check(result.Files.Count == 1 && result.Files[0].Name == "huge.bin" && result.Files[0].Size == 8192, "Newly added large file is not lost behind retention limit");
    }
    static void EntryIdentity()
    {
        string root = Dir("entry-identity"); Data(root, "file.bin", 7);
        using var directory = NativeDirectory.OpenRoot(root);
        using var handle = directory.OpenFile("file.bin");
        var identity = NativeMetadata.Identity(handle);
        var entry = new NativeEntry("file.bin", 7, DateTime.MinValue, FileAttributes.Normal, identity.Id == 1 ? 2UL : 1UL);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var frameType = typeof(ScanRun).GetNestedType("Frame", BindingFlags.NonPublic)!;
        foreach (bool recover in new[] { false, true })
        {
            var run = recover ? new ScanRun(root, null, CancellationToken.None, 10, [], new ScanPolicy(NativeMetadata.Identity(directory.Handle)))
                : new ScanRun(root, null, CancellationToken.None, 10, []);
            using var cancellation = new CancellationTokenSource(); typeof(ScanRun).GetField("stop", flags)!.SetValue(run, cancellation);
            var measuredResult = (ScanResult)typeof(ScanRun).GetField("result", flags)!.GetValue(run)!;
            var frame = Activator.CreateInstance(frameType, flags, null, [new ScanNode(measuredResult.Root, null, directory, "")], null)!;
            bool threw = false;
            try { typeof(ScanRun).GetMethod("Entry", flags)!.Invoke(run, [null, frame, entry, long.MaxValue]); }
            catch (TargetInvocationException ex) when (ex.InnerException is ScopeChangedException) { threw = true; }
            finally { ((IDisposable)frameType.GetProperty("Entries", flags)!.GetValue(frame)!).Dispose(); }
            if (!recover) Check(threw, "Strict low-level identity mismatch remains fatal");
            else
            {
                var result = (ScanResult)typeof(ScanRun).GetField("result", flags)!.GetValue(run)!;
                var retained = ((ScanNode)frameType.GetProperty("Node", flags)!.GetValue(frame)!).Files;
                Check(!threw && result.NeedsRetry && !result.ScopeChanged, "Recovering file identity mismatch queues remeasurement instead of stopping root");
                Check(retained.Single().Unverified && result.UnverifiedPaths.Contains(Path.Combine(root,"file.bin")), "Changed file's listing observation is explicitly unverified");
            }
        }
    }
    static void Growth()
    {
        string root = Dir("growth"); Data(root, "growing.bin", 20); bool done = false;
        var result = Scan([root], p => { if (!done && p.Current.Contains("Doğrulama: ")) { done = true; Data(root, "growing.bin", 16000); } });
        Check(result.RetryCount == 1 && result.Root.LogicalBytes == 16000 && result.Files[0].Size == 16000, "Existing file growth after measurement is detected and replaced");
    }
    static void RootClassification()
    {
        string root = Dir("root-classification"); Data(root,"old.bin",7);
        using var pin=NativeDirectory.OpenRoot(root);
        var run=new ScanRun(root,null,CancellationToken.None,10,[],new ScanPolicy(NativeMetadata.Identity(pin.Handle)));
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(ScanRun).GetField("anchor",flags)!.SetValue(run,pin);
        Directory.Move(root,root+"-old");Directory.CreateDirectory(root);Data(root,"new.bin",100);
        Exception? failure=null;
        try{typeof(ScanRun).GetMethod("VerifyAnchor",flags)!.Invoke(run,null);}
        catch(TargetInvocationException ex){failure=ex.InnerException;}
        Check(failure is RootScopeChangedException,"Real root replacement is classified separately from retryable descendant scope changes");
        using var current = NativeDirectory.OpenRoot(root);
        var wrong = NativeMetadata.Identity(current.Handle); wrong.Id ^= 1UL;
        var rejected = new ScanRun(root,null,CancellationToken.None,10,[],new ScanPolicy(wrong));
        var rejectedResult=rejected.Execute(1);
        var opened=(NativeDirectory)typeof(ScanRun).GetField("anchor",flags)!.GetValue(rejected)!;
        Check(rejectedResult.ScopeChanged && opened.Handle.IsClosed,"Root rejected before first enumeration releases its native handle immediately");
    }
    static void RankingAfterShrink()
    {
        string first = Dir("ranking-a"), second = Dir("ranking-b"); Data(first, "shrinks.bin", 9000); Data(second, "winner.bin", 8000); bool done = false;
        var result = Scan([first, second], p => { if (!done && p.Current.Contains("Doğrulama: " + first)) { done = true; Data(first, "shrinks.bin", 10); } }, limit: 1);
        Check(result.RetryCount == 1 && result.Root.LogicalBytes == 8010 && result.Root.FileCount == 2, "Only changed root is replaced in multi-root totals");
        Check(result.Files.Single().Name == "winner.bin", "Top-K recovers the healthy root's file when former winner shrinks");
        Check(result.VerificationHistory.Count(h => h.StartsWith(second + " ·")) == 1, "Healthy selected root is not needlessly rescanned");
    }
    static void Persistent()
    {
        string root = Dir("persistent"); Data(root, "original.bin", 30); int changes = 0;
        var watch = Stopwatch.StartNew();
        var result = Scan([root], p => { if (p.Current.Contains("Doğrulama: " + root)) Data(root, "change-" + (++changes) + ".bin", changes); });
        Check(changes == 3 && result.RetryCount == 2 && result.NeedsRetry && watch.Elapsed < TimeSpan.FromSeconds(15), "Persistent churn stops after exactly three bounded attempts");
        Check(result.Root.Measurement == MeasurementState.Previous && result.HasUnverified && result.Files.All(f => f.Unverified), "Unresolved observation is explicitly stale in folders and files");
        Check(!result.CanUsePath(result.Files[0].Path), "Unresolved observation cannot open stale paths");
    }
    static void CancelValidation()
    {
        string root = Dir("cancel-validation"); Data(root, "file.bin", 17); using var cts = new CancellationTokenSource();
        var result = Scan([root], p => { if (p.Current.Contains("Doğrulama: ")) cts.Cancel(); }, cts.Token);
        Check(result.Cancelled && !result.ScopeChanged && result.RetryCount == 0, "Cancel during validation stays a cancellation, never a scope error");
    }
    static void PersistentChild()
    {
        string root = Dir("persistent-child"), child = Path.Combine(root, "busy"); Directory.CreateDirectory(child);
        Data(root, "healthy-large.bin", 9000); Data(child, "initial.bin", 30); int changes = 0;
        var result = Scan([root], p => { if (p.Current.Contains("Doğrulama: " + root)) Data(child, "change-" + (++changes) + ".bin", changes); });
        Check(result.NeedsRetry && result.RetryCount == 2 && result.Root.Measurement == MeasurementState.Partial, "Unstable child keeps the aggregate explicitly partial after bounded retries");
        Check(result.CanUsePath(Path.Combine(root, "healthy-large.bin")) && !result.Files.Single(f => f.Name == "healthy-large.bin").Unverified, "Unstable child does not disable a validated sibling file");
        Check(!result.CanUsePath(Path.Combine(child, "initial.bin")) && result.Files.Where(f => f.Path.StartsWith(child + "\\")).All(f => f.Unverified), "Only the unstable branch's observed files remain stale and blocked");
    }
    static void CancelRetry()
    {
        string root = Dir("cancel-retry"); Data(root, "file.bin", 17); using var cts = new CancellationTokenSource(); bool changed = false;
        var result = Scan([root], p =>
        {
            if (!changed && p.Current.Contains("Doğrulama: ")) { changed = true; Data(root, "new.bin", 99); }
            if (p.Current.StartsWith("Değişen alanlar")) cts.Cancel();
        }, cts.Token);
        Check(result.Cancelled && result.RetryCount == 0 && result.HasUnverified, "Cancellation interrupts retry wait without silently accepting incomplete validation");
    }
    static void RootReplacement()
    {
        string root = Dir("root-race"), healthy = Dir("root-race-healthy"); Data(root, "old.bin", 17); Data(healthy, "healthy.bin", 29); bool swapped = false;
        var result = Scan([root, healthy], p =>
        {
            if (!swapped && p.Current.Contains("Doğrulama: " + root))
            { swapped = true; Directory.Move(root, root + "-old"); Directory.CreateDirectory(root); Data(root, "replacement-canary.bin", 50000); }
        });
        Check(result.ScopeChanged && !result.Files.Any(f => f.Name == "replacement-canary.bin"), "Replaced selected root is never adopted or traversed");
        Check(result.Files.Any(f => f.Name == "healthy.bin") && result.CanUsePath(Path.Combine(healthy, "healthy.bin")), "Independent healthy root still completes and remains actionable");
        Check(!result.CanUsePath(Path.Combine(root, "old.bin")), "Changed root paths stay blocked even with healthy roots present");
    }
    static void JunctionReplacement()
    {
        string root = Dir("junction"), child = Path.Combine(root, "child"), outside = Dir("junction-outside"); Directory.CreateDirectory(child); Data(child, "old.bin", 17); Data(outside, "outside-canary.bin", 60000); bool swapped = false;
        var result = Scan([root], p =>
        {
            if (!swapped && p.Current.Contains("Doğrulama: " + root))
            {
                swapped = true; Directory.Move(child, Path.Combine(root, "moved"));
                var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + child.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' | Out-Null");
                using var process = Process.Start(start)!; process.WaitForExit(); if (process.ExitCode != 0) throw new Exception(process.StandardError.ReadToEnd());
            }
        });
        Check(result.RetryCount == 1 && !result.Files.Any(f => f.Name == "outside-canary.bin"), "Retry never follows a replacement junction outside the selected scope");
        Check(result.Root.Children.Single(f => f.Path == child).Measurement == MeasurementState.Unknown, "Skipped junction appears as an unknown-size folder, not an empty folder");
        Check(result.Root.Children.Single(f => f.Path == child).LastObservedBytes == 17, "Unknown replacement retains the prior observed size without treating it as current");
        Check(result.CanUsePath(Path.Combine(root, "moved", "old.bin")) && !result.CanUsePath(child), "Only unverified junction path is blocked after successful restabilization");
    }
    static void HardLinks()
    {
        string first = Dir("links-a"), second = Dir("links-b"); Data(first, "original.bin", 8192);
        Check(CreateHardLinkW(Path.Combine(second, "alias.bin"), Path.Combine(first, "original.bin"), 0), "Hard-link fixture created");
        bool changed = false;
        var result = Scan([first, second], p => { if (!changed && p.Current.Contains("Doğrulama: " + first)) { changed = true; Data(first, "extra.bin", 5000); } });
        var baseline = MultiScan.ScanConfigured([first, second], null, CancellationToken.None, 100, 4);
        Check(result.Root.LogicalBytes == baseline.Root.LogicalBytes && result.Root.AllocatedBytes == baseline.Root.AllocatedBytes && result.HardLinkAliases == baseline.HardLinkAliases, "Root replacement reconciles cross-root hard links without double allocation");
        Check(result.Root.Children.Sum(f => f.AllocatedBytes) == result.Root.AllocatedBytes, "Reconciled root physical totals equal sum of child roots");
    }
    static Action DenyListing(string path)
    {
        var directory = new DirectoryInfo(path); string original = FileSystemAclExtensions.GetAccessControl(directory).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var changed = FileSystemAclExtensions.GetAccessControl(directory);
        changed.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        FileSystemAclExtensions.SetAccessControl(directory, changed);
        return () =>
        {
            var restored = new DirectorySecurity(); restored.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
            FileSystemAclExtensions.SetAccessControl(directory, restored);
        };
    }
    static void DeniedRoot()
    {
        string root = Dir("denied"), healthy = Dir("denied-healthy"); Data(root, "private.bin", 1000); Data(healthy, "healthy.bin", 29);
        Action restore = DenyListing(root);
        try
        {
            var result = Scan([root, healthy]);
            Check(result.Root.Children.Single(f => f.Path == root).Measurement == MeasurementState.Unknown, "Unreadable root is represented as unknown size");
            Check(result.RetryCount == 0 && result.Files.Any(f => f.Name == "healthy.bin"), "Permanent access denial is not retried and does not stop other roots");
        }
        finally { restore(); }
        Check(Directory.GetFiles(root).Length == 1, "Denied root ACL restored without changing its data");
    }
    static void FallbackObservation()
    {
        string root = Dir("fallback"); Data(root, "large-observed.bin", 9000); bool changed = false; Action? restore = null;
        try
        {
            var result = Scan([root], p =>
            {
                if (!changed && p.Current.Contains("Doğrulama: ")) { changed = true; Data(root, "new.bin", 1); }
                if (restore == null && p.Current.StartsWith("Değişen alanlar")) restore = DenyListing(root);
            });
            Check(result.Files.Any(f => f.Name == "large-observed.bin") && result.Root.LogicalBytes >= 9000, "Failed replacement preserves an observed large file rather than publishing zero");
            Check(result.Root.Measurement == MeasurementState.Previous && !result.CanUsePath(Path.Combine(root, "large-observed.bin")), "Preserved observation is stale and nonactionable");
        }
        finally { restore?.Invoke(); }
    }
    static async Task Presentation()
    {
        var root = new FolderEntry("Seçilen klasörler (2)", null) { Measurement = MeasurementState.Partial };
        var bad = new FolderEntry(@"C:\bad", root) { Measurement = MeasurementState.Unknown };
        var good = new FolderEntry(@"C:\good", root) { LogicalBytes = 9000, AllocatedBytes = 12288, FileCount = 1 };
        root.Children.AddRange([bad, good]);
        var result = new ScanResult(root) { ScopeChanged = true }; result.RootPaths.Clear(); result.RootPaths.AddRange([bad.Path, good.Path]); result.UnverifiedPaths.Add(bad.Path);
        result.Files.Add(new(@"C:\good\large.bin", 9000, 12288, DateTime.MinValue, false, ""));
        using var model = new WorkspaceViewModel(new FakeService(new(result, []))); model.Roots.Add(@"C:\fake"); await model.ScanAsync();
        Check(RowValues.Text(bad, "Size") == "Boyut bilinmiyor" && RowValues.Text(bad, "Count") == "Bilinmiyor" && RowValues.AllocatedOf(bad) == null, "Unknown folder has no fake zero size/count/allocation");
        Check(ReferenceEquals(model.Rows[0], bad), "Unknown folder is prominent in default descending size view");
        using (var writer = new StringWriter())
        {
            ReportSnapshot.Capture(model).Write(writer);
            Check(writer.ToString().Contains("\"bad\";\"\";\"\";\"\";") && writer.ToString().Contains("Boyut bilinmiyor"), "CSV keeps unknown folder byte/count fields empty and states uncertainty");
        }
        await model.SetViewAsync(WorkspaceView.Files); model.SelectedRow = model.Rows[0];
        Check(model.CanUseSelection && model.Details.Path == @"C:\good\large.bin", "Healthy-root selection stays usable beside a changed root");
        Check(model.ShowScopeNotice && model.ScanActionLabel == "Yeniden doğrula", "Unverified scope exposes warning and manual revalidation action");
        var safety = new ScanResult(new FolderEntry(@"C:\data", null)); safety.UnverifiedPaths.Add(@"C:\data");
        Check(!safety.CanUsePath(@"C:\data\x") && safety.CanUsePath(@"C:\data10\x"), "Unsafe path matching respects component boundaries");
        safety.UnverifiedPaths.Clear(); safety.UnverifiedPaths.Add(@"C:\");
        Check(!safety.CanUsePath(@"C:\any\file") && safety.CanUsePath(@"D:\any\file"), "Drive-root uncertainty blocks only that drive");
        var stale = new ScanResult(new FolderEntry(@"C:\stale", null) { Measurement = MeasurementState.Previous, LogicalBytes = 9000 }) { NeedsRetry = true, RetryCount = 2 };
        stale.UnverifiedPaths.Add(stale.Root.Path); stale.Files.Add(new(@"C:\stale\large.bin", 9000, 12288, DateTime.MinValue, false, "", Unverified: true));
        using var staleModel = new WorkspaceViewModel(new FakeService(new(stale, []))); staleModel.Roots.Add(stale.Root.Path); await staleModel.ScanAsync(); await staleModel.SetViewAsync(WorkspaceView.Files); staleModel.SelectedRow = staleModel.Rows[0];
        Check(!staleModel.CanUseSelection && staleModel.Details.Sections[0].Caution && staleModel.Status.Contains("Kararlı ölçüm alınamadı"), "Stale file has visible caution and disabled actions");
        Check(RowValues.Text(stale.Root, "Size").Contains("son ölçüm") && RowValues.Text(stale.Files[0], "Size").Contains("doğrulanamadı"), "Folder/file rows distinguish previous observations");
        using var staleWriter = new StringWriter(); ReportSnapshot.Capture(staleModel).Write(staleWriter);
        Check(staleWriter.ToString().Contains("Doğrulanamadı") && staleWriter.ToString().Contains("Yeniden ölçüm sayısı"), "CSV retains stale state and revalidation metadata");
    }
    static void LostChildAccess()
    {
        string root = Dir("lost-access"), child = Path.Combine(root, "child"); Directory.CreateDirectory(child); Data(child, "observed.bin", 9000);
        Action? restore = null;
        try
        {
            var result = Scan([root], p => { if (restore == null && p.Current.Contains("Doğrulama: " + root)) restore = DenyListing(child); });
            log.Add($"DIAG lost-child: retry={result.RetryCount}; state={result.Root.Measurement}; files={result.Files.Count}; unsafe={result.UnverifiedPaths.Count}; child={result.Root.Children.Single().Measurement}; previous={result.Root.Children.Single().LastObservedBytes}; issues={string.Join(" | ",result.Issues.Select(i=>i.Reason))}");
            Check(result.RetryCount == 0 && result.HasUnverified, "Permanent child access loss is explicit without pointless retries");
            Check(result.Files.Single().Unverified && result.Root.Children.Single().Measurement == MeasurementState.Previous, "Observed descendants of an unverifiable folder are labelled stale, not measured-current");
            Check(!result.CanUsePath(result.Files.Single().Path), "Validation-time child access loss blocks descendant path actions");
        }
        finally { restore?.Invoke(); }
    }
    static void MissingRoot()
    {
        string root = Path.Combine(fixtures, "missing"), good = Dir("missing-healthy"); Data(good, "healthy.bin", 70);
        var result = Scan([root, good]);
        Check(result.Files.Any(f => f.Name == "healthy.bin") && result.Root.Children.Single(f => f.Path == root).Measurement == MeasurementState.Unknown, "Root disappearing before start is shown as unknown while other roots complete");
        Check(!result.CanUsePath(root) && result.CanUsePath(Path.Combine(good, "healthy.bin")), "Missing root does not disable the healthy root's paths");
    }
    static void Performance()
    {
        string root = Dir("bench");
        for (int d = 0; d < 240; d++) { string child = Path.Combine(root, "d" + d); Directory.CreateDirectory(child); for (int f = 0; f < 50; f++) Data(child, f + ".bin", f + 1); }
        var watch = Stopwatch.StartNew(); var strict = MultiScan.ScanConfigured([root], null, CancellationToken.None, 100, 4); long strictMs = watch.ElapsedMilliseconds;
        watch.Restart(); var verified = Scan([root], limit: 100); long verifiedMs = watch.ElapsedMilliseconds;
        Check(verified.Root.FileCount == 12000 && verified.Root.LogicalBytes == strict.Root.LogicalBytes && verified.Root.AllocatedBytes == strict.Root.AllocatedBytes && verified.RetryCount == 0, "12k-file validation preserves totals without spurious retry");
        log.Add($"MEASURE warm sequential 12k files: strict_ms={strictMs}; verified_ms={verifiedMs}; retained=100; not a cold-disk benchmark");
    }
    sealed class FakeService(ScanOutcome outcome) : IScanService
    {
        public string[] NormalizeRoots(IEnumerable<string> roots) => roots.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token) => Task.FromResult(outcome);
    }
    sealed class Progress(Action<ScanProgress> action) : IProgress<ScanProgress> { public void Report(ScanProgress value) => action(value); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string name, string existing, nint security);
}
