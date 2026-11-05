using YerelDisk;
using YerelDisk.Presentation;

internal static class ExclusionTests
{
    static readonly List<string> log = [];
    static string fixtures = "";
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); log.Add("PASS " + name); }
    static string Dir(string name) { string path = Path.Combine(fixtures, name); Directory.CreateDirectory(path); return path; }
    static void Data(string path, int size) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, new byte[size]); }
    sealed class Callback(Action<ScanProgress> action) : IProgress<ScanProgress> { public void Report(ScanProgress value) => action(value); }
    static ScanResult Scan(string[] roots, string[] exclusions, int workers = 4, Action<ScanProgress>? callback = null, int limit = 100)
        => VerifiedScan.Scan(roots, callback == null ? null : new Callback(callback), CancellationToken.None, limit, workers, exclusions);
    static void Reject(string[] roots, string[] exclusions, string label)
    {
        bool rejected = false;
        try { ScanExclusions.Create(roots, exclusions); } catch (ArgumentException) { rejected = true; }
        Check(rejected, label);
    }
    static async Task<int> Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output); fixtures = Path.Combine(output, "own-fixtures"); Directory.CreateDirectory(fixtures);
        try
        {
            string root = Dir("scope"), skip = Path.Combine(root, "skip"), sibling = Path.Combine(root, "skip-other"), keep = Path.Combine(root, "keep");
            Data(Path.Combine(skip, "nested", "excluded.pst"), 65536); Data(Path.Combine(sibling, "included.bin"), 31); Data(Path.Combine(keep, "small.bin"), 7); Data(Path.Combine(root, "root.bin"), 5);
            Check(ScanExclusions.Create([root], []).Paths.Count == 0, "No exclusion is the existing scope");
            var normalized = ScanExclusions.Create([root], [skip, skip.ToUpperInvariant(), skip + "\\", Path.Combine(skip, "nested")]);
            Check(normalized.Paths.Count == 1 && normalized.Paths[0].Equals(skip, StringComparison.OrdinalIgnoreCase), "Case/trailing separator duplicates and nested rules collapse");
            Check(ScanExclusions.SameOrBelow(skip + "\\x", skip) && !ScanExclusions.SameOrBelow(sibling, skip), "Component boundaries preserve similarly named siblings");
            Check(ScanExclusions.SameOrBelow(@"C:\Windows", @"C:\") && !ScanExclusions.SameOrBelow(@"D:\Windows", @"C:\"), "Drive-root boundary stays on its volume");
            Check(normalized.SkipsEntry(root.ToUpperInvariant(), "SKIP") && !normalized.SkipsEntry(root, "skip-other"), "Allocation-free entry filter preserves case and sibling boundaries");
            for (int i = 0; i < 1000; i++) normalized.SkipsEntry(root, "skip");
            long before = GC.GetAllocatedBytesForCurrentThread(); int hits = 0;
            for (int i = 0; i < 200000; i++) if (normalized.SkipsEntry(root, "skip")) hits++;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            log.Add($"MEASURE 200k scope checks: {allocated} managed bytes; runtime/JIT setup included");
            Check(hits == 200000 && allocated < 65536, "200k exclusion checks do not allocate per-entry path strings");
            Reject([root], [root], "Selected root itself cannot be excluded");
            Reject([root], [Path.GetDirectoryName(root)!], "Ancestor of selected root cannot be excluded");
            Reject([root], [root + "-other\\skip"], "Outside path cannot silently change scope");
            foreach (string input in new[] { "skip", @"C:skip", @"\\server\share\skip", @"\\?\C:\skip", skip + ":ads", skip + "\\*", skip + "\\?", skip + "\\odd.", skip + "\\odd \\", "" })
                Reject([root], [input], "Invalid/ambiguous exclusion is rejected: " + input);
            Reject([root, keep], [keep], "Any selected root is protected from whole-root exclusion");
            string absent = Path.Combine(root, "not-created");
            Check(ScanExclusions.Create([root], [absent]).Paths.Single() == absent, "Absent path is retained as requested scope without probing");
            foreach (int workers in new[] { 1, 4, 16 })
            {
                var result = Scan([root], [skip], workers);
                Check(result.Root.LogicalBytes == 43 && result.Root.FileCount == 3, "Excluded bytes/counts absent, sibling included: workers " + workers);
                Check(result.Folders == 3 && result.Root.Children.All(f => f.Path != skip), "No zero-byte excluded placeholder: workers " + workers);
                Check(result.Files.Count == 3 && result.Files.All(f => !ScanExclusions.SameOrBelow(f.Path, skip)), "Top file list excludes subtree: workers " + workers);
                Check(result.IssueCount == 0 && result.UnknownAllocation == 0 && !result.HasUnverified && !result.ScopeChanged && result.RetryCount == 0, "Intentional scope is neither access failure nor uncertainty: workers " + workers);
                Check(result.Root.ExcludedSubtrees == 1 && result.Root.Measurement == MeasurementState.Measured && result.Root.Children.All(f => f.ExcludedSubtrees == 0), "Only measured ancestors carry separate exclusion marker: workers " + workers);
                Check(result.ExcludedPaths.SequenceEqual([skip]) && !result.CanUsePath(skip + "\\x") && result.CanUsePath(sibling), "Result preserves immutable scope and blocks excluded path actions: workers " + workers);
            }
            var full = Scan([root], []);
            Check(full.Root.FileCount == 4 && full.Root.LogicalBytes == 65579 && full.ExcludedPaths.Count == 0, "No-exclusion scan still measures full fixture");
            var missing = Scan([root], [absent]);
            Check(missing.ExcludedPaths.Single() == absent && missing.IssueCount == 0 && missing.Root.LogicalBytes == full.Root.LogicalBytes, "Absent rule is reported without pretending path existed");
            string nestedSkip = Path.Combine(skip, "nested");
            var nested = Scan([root], [nestedSkip]);
            Check(nested.Root.ExcludedSubtrees == 1 && nested.Root.Children.Single(f => f.Path == skip).ExcludedSubtrees == 1 && nested.Root.LogicalBytes == 43, "Nested exclusion marks observed parent and root");
            bool mutated = false;
            var churn = Scan([root], [skip], callback: p => {
                if (!mutated && p.Phase == ScanPhase.Verification) { mutated = true; Directory.Move(skip, skip + "-renamed"); Directory.CreateDirectory(skip); Data(Path.Combine(skip, "new.bin"), 900); }
            });
            // Renaming the target OUT of the excluded path changes included scope; it must be measured.
            Check(mutated && churn.RetryCount == 1 && !churn.HasUnverified, "Renaming out of exclusion triggers bounded repair of included parent");
            Check(churn.Root.LogicalBytes == 65579 && churn.Files.All(f => !ScanExclusions.SameOrBelow(f.Path, skip)), "Moved-out files become included; replacement target stays excluded after repair");
            bool changed = false;
            var hiddenChanges = Scan([root], [skip], callback: p => {
                if (!changed && p.Phase == ScanPhase.Verification) { changed = true; File.Delete(Path.Combine(skip, "new.bin")); Data(Path.Combine(skip, "different.bin"), 500); }
            });
            Check(changed && hiddenChanges.RetryCount == 0 && hiddenChanges.Root.LogicalBytes == churn.Root.LogicalBytes, "Content churn inside excluded target does not trigger repair");
            bool replaced = false;
            var replacement = Scan([root], [skip], callback: p => {
                if (!replaced && p.Phase == ScanPhase.Verification) { replaced = true; Directory.Delete(skip, true); File.WriteAllBytes(skip, new byte[128]); }
            });
            Check(replaced && replacement.RetryCount == 0 && replacement.Root.LogicalBytes == churn.Root.LogicalBytes, "Excluded path replaced by file stays outside fingerprint and totals");
            File.Delete(skip);
            bool added = false;
            var late = Scan([root], [absent], callback: p => {
                if (!added && p.Phase == ScanPhase.Verification) { added = true; Data(Path.Combine(absent, "late.bin"), 5000); }
            });
            Check(added && late.RetryCount == 0 && late.Files.All(f => !ScanExclusions.SameOrBelow(f.Path, absent)), "Excluded name appearing after initial scan does not cause false refresh");
            string second = Dir("second"); Data(Path.Combine(second, "kept.bin"), 11); Data(Path.Combine(second, "omit", "large.bin"), 8000);
            var multi = Scan([root, second], [absent, Path.Combine(second, "omit")], limit: 1);
            Check(multi.Root.FileCount == 5 && multi.Root.LogicalBytes == 65590 && multi.Files.Single().Name == "excluded.pst", "Multi-root global totals and top-K respect both exclusions");
            Check(multi.ExcludedPaths.Count == 2 && multi.Root.ExcludedSubtrees == 2 && multi.Root.Children.All(f => f.ExcludedSubtrees == 1), "Multi-root scope survives merge and per-root markers");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var stopped = VerifiedScan.Scan([root, second], null, cancelled.Token, 10, 4, [absent]);
            Check(stopped.Cancelled && stopped.ExcludedPaths.Single() == absent && stopped.HasUnverified, "Already-cancelled scan retains exclusions and unknown roots");
            var requestedExclusions = new List<string> { absent }; var request = new ScanRequest([root], 4, 100, requestedExclusions); requestedExclusions.Clear();
            Check(request.Exclusions.SequenceEqual([absent]), "Request snapshots exclusions before background dispatch");
            var fake = new FakeService(multi); using var model = new WorkspaceViewModel(fake); model.Roots.Add(root); model.Roots.Add(second); model.Exclusions.Add(absent);
            await model.ScanAsync();
            Check(fake.Last!.Exclusions.SequenceEqual([absent]), "View model sends configured exclusion snapshot");
            Check(model.ShowScopeNotice && model.ScopeNotice.Contains("2 yol bilerek") && model.ScopeText.Contains(absent), "Finished result notice uses scanned scope, not edited settings");
            Check(model.Summary!.Coverage.Contains("toplamlar bunları içermez") && model.Summary.Coverage.Contains("2 yol"), "Summary explains intentional omitted scope");
            Check(WorkspaceViewModel.ResultStatus(multi).Contains("hariç"), "Finished status distinguishes excluded scope");
            var report = ReportSnapshot.Capture(model); model.Exclusions.Clear(); var writer = new StringWriter(); report.Write(writer);
            Check(writer.ToString().Contains("Hariç tutulan yol") && writer.ToString().Contains(absent) && writer.ToString().Contains("Kapsam dışında; ölçülmedi"), "CSV captures measured exclusion scope despite later edits");
            Check(RowValues.MeasurementOf(multi.Root).Contains("hariç tutma"), "Folder measurement column marks intentionally narrowed totals");
            var details = SelectionDetails.Create(multi, multi.Root, false, "", model.ScopeText);
            Check(details.Sections.Any(s => s.Heading.Contains("hariç") && s.Text.Contains(absent)), "F6 explicitly lists exclusions separately from failures");
            string home = Dir("review-home"), cache = Path.Combine(home, ".gradle", "caches"); Data(Path.Combine(cache, "keep.bin"), 17); Data(Path.Combine(cache, "omit", "personal.pst"), 6000);
            var reviewScan = Scan([home], [Path.Combine(cache, "omit")]);
            var candidate = new ReviewRules(home, Path.Combine(home, "local"), Path.Combine(home, "pd"), @"C:\Windows").Analyze(reviewScan).Single();
            Check(candidate.ExcludedSubtrees == 1 && candidate.Kind == ReviewKind.Cache && candidate.LogicalBytes == 17, "Candidate sizes and type evidence use only included files");
            Check(ReviewText.Measurement(candidate).Contains("bilerek hariç") && ReviewText.Measurement(candidate).Contains("dosya ipuçlarına dahil değildir"), "Candidate measurement explains absent subtree evidence");
            var bad = new FakeService(multi); using var invalidModel = new WorkspaceViewModel(bad); invalidModel.Roots.Add(root); invalidModel.Exclusions.Add(second); await invalidModel.ScanAsync();
            Check(bad.Last == null && invalidModel.HasError && !invalidModel.IsBusy, "Invalid pending scope never starts scan or discards existing result");
            log.Add($"ALL CHECKS PASSED ({log.Count(s => s.StartsWith("PASS "))})"); File.WriteAllLines(Path.Combine(output, "results.txt"), log); Console.WriteLine(log[^1]); return 0;
        }
        catch(Exception ex) { log.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(output, "results.txt"), log); Console.WriteLine(ex); return 1; }
    }
    sealed class FakeService(ScanResult result) : IScanService
    {
        internal ScanRequest? Last;
        public string[] NormalizeRoots(IEnumerable<string> paths) => paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token) { Last = request; return Task.FromResult(new ScanOutcome(result, [])); }
    }
}
