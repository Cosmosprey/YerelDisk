using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using YerelDisk;
using static Support;

static class Tests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string alias, string original, nint security);
    static string Signature(ScanResult scan) => JsonSerializer.Serialize(new
    {
        scan.Root.LogicalBytes, scan.Root.AllocatedBytes, scan.Root.FileCount, scan.Folders, scan.HardLinkAliases, scan.UnknownAllocation, scan.IssueCount,
        FolderRows = Walk(scan.Root).OrderBy(folder => folder.Path, StringComparer.Ordinal).Select(folder => new { folder.Path, folder.LogicalBytes, folder.AllocatedBytes, folder.FileCount, folder.IssueCount, folder.Measurement, folder.OwnStreams, folder.OwnStreamBytes, folder.ExcludedSubtrees }),
        Files = scan.Files.Select(file => new { file.Path, file.Size, file.Allocated, file.HardLinked, file.ExtraStreams, file.ExtraStreamBytes, file.Unverified, file.Hint })
    });
    static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try
        {
            var t = Tree(output, "links"); string mail = Path.Combine(t.A, "mail.pst");
            Check(CreateHardLinkW(Path.Combine(t.A, "mail-alias.pst"), mail, 0), "Create selected-area internal hard link");
            File.WriteAllBytes(mail + ":extra", new byte[3]); File.WriteAllBytes(t.Root + ":root-extra", new byte[7]); File.WriteAllBytes(t.A + ":dir-extra", new byte[5]);
            var old = Scan([t.Root], 10, [t.Skip]); string digest = Digest(old);
            Check(old.RefreshIndex is { Captured: true } && old.HardLinkAliases == 1, "Native linked-stream baseline has a complete replay index");
            File.WriteAllBytes(mail, new byte[200]); File.WriteAllBytes(Path.Combine(t.A, "winner.bin"), new byte[10]);
            // FileMode.Create may replace named streams on Windows. Recreate the
            // fixture's ADS after truncating the unnamed stream.
            File.WriteAllBytes(mail + ":extra", new byte[3]);
            using (var prepared = RefreshExecution.Prepare(old, Find(old, t.A), 10, 2, Limits))
            {
                var next = prepared.ValidateForCommit(); var all = Scan([t.Root], 10, [t.Skip]);
                Check(prepared.IsIncremental && Signature(next) == Signature(all), "Selected replay matches full native logical allocated count folder and top-K accounting");
                Check(next.Files.Count(file => file.HardLinked) == 2 && next.HardLinkAliases == 1, "Internal aliases retain one physical allocation and both link flags");
                Check(next.Files.Where(file => file.HardLinked).All(file => file.ExtraStreams == 1 && file.ExtraStreamBytes == 3), "File stream metadata survives selected remeasurement and replay");
                Check(next.Root.OwnStreamBytes == 7 && Find(next, t.A).OwnStreamBytes == 5, "Root and selected directory ADS are accounted independently");
                Check(Digest(old) == digest, "Native replay leaves the complete original generation intact");
            }
            var multiOld = Scan([t.A, t.B], 10, [t.Skip]);
            File.WriteAllBytes(Path.Combine(t.A, "winner.bin"), new byte[12]);
            using (var prepared = RefreshExecution.Prepare(multiOld, Find(multiOld, t.A), 10, 1, Limits))
            {
                var next = prepared.ValidateForCommit(); var all = Scan([t.A, t.B], 10, [t.Skip]);
                Check(prepared.IsIncremental && Signature(next) == Signature(all), "One full real root of a multi-root result refreshes with full-scan parity");
                Check(next.RootPaths.SequenceEqual(multiOld.RootPaths) && next.ExcludedPaths.SequenceEqual(multiOld.ExcludedPaths), "Multi-root replay preserves root order and inherited exclusion scope");
                Check(next.RefreshIndex is { Captured: true } && next.RefreshIndex.Roots.Count() == 2, "Rebuilt multi-root snapshot has complete per-root ownership ranges");
            }
            string boundary = Path.Combine(t.A, "outside-alias.bin"); var beforeBoundary = Scan([t.Root], 10, [t.Skip]);
            Check(CreateHardLinkW(boundary, Path.Combine(t.B, "runner-up.bin"), 0), "Create new cross-boundary hard link after the old measurement");
            using (var prepared = RefreshExecution.Prepare(beforeBoundary, Find(beforeBoundary, t.A), 10, 1, Limits))
                Check(prepared.Action == RefreshAction.MeasureScope && prepared.Reason == RefreshReason.SharedOutside && Signature(prepared.ValidateForCommit()) == Signature(Scan([t.Root], 10, [t.Skip])), "New boundary alias triggers original full scope with physical parity");
            var sharedOld = Scan([t.Root], 10, [t.Skip]);
            using (var prepared = RefreshExecution.Prepare(sharedOld, Find(sharedOld, t.A), 10, 1, Limits))
                Check(prepared.Action == RefreshAction.MeasureScope && !prepared.ValidateForCommit().HasMixedMeasurementTimes, "Existing boundary alias bypasses selected replay");
            File.Delete(boundary);
            var beforeMove = Scan([t.Root], 10, [t.Skip]);
            File.Move(Path.Combine(t.B, "runner-up.bin"), Path.Combine(t.A, "moved.bin"));
            using (var prepared = RefreshExecution.Prepare(beforeMove, Find(beforeMove, t.A), 10, 1, Limits))
                Check(prepared.Action == RefreshAction.MeasureScope && Signature(prepared.ValidateForCommit()) == Signature(Scan([t.Root], 10, [t.Skip])), "Moving an outside observed file into the selected area cannot leave a double-counted old alias");

            var j = Tree(output, "junction"); string outside = Path.Combine(output, "own-fixtures", "outside"); Directory.CreateDirectory(outside);
            string canary = Path.Combine(outside, "never-read.pst"); File.WriteAllBytes(canary, new byte[888]); string link = Path.Combine(j.Root, "junction");
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' | Out-Null" }) start.ArgumentList.Add(argument);
            using (var process = Process.Start(start)!) { var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); process.WaitForExit(); if (process.ExitCode != 0) throw new Exception(stdout.Result + stderr.Result); }
            var partial = Scan([j.Root], 10, [j.Skip]);
            using (var prepared = RefreshExecution.Prepare(partial, Find(partial, j.A), 10, 1, Limits))
            {
                var next = prepared.ValidateForCommit();
                Check(prepared.IsIncremental && Signature(next) == Signature(Scan([j.Root], 10, [j.Skip])), "Healthy selected replay retains an unrelated unknown junction with full-scan parity");
                Check(Find(next, link).Measurement == MeasurementState.Unknown && !next.RefreshIndex!.Files.Any(file => file.Name == "never-read.pst") && new FileInfo(canary).Length == 888, "Junction target is not followed and its unknown state is preserved");
            }
            var descriptor = partial.RefreshIndex!.Roots.Single();
            var rejected = VerifiedScan.Scan([j.Root], null, default, 10, 1, [j.Skip], 4 << 20, [descriptor with { Id = descriptor.Id + 1 }]);
            Check(rejected.ScopeChanged && rejected.Root.FileCount == 0 && rejected.Root.Children.Count == 0, "Expected-root rejection occurs before any descendant is enumerated");
            var bounded = Scan([j.Root], 10, [j.Skip]);
            bounded.RefreshedAreas = Array.AsReadOnly(Enumerable.Range(0, RefreshRebuild.MaximumAreas).Select(i => new RefreshAreaMeasurement(Path.Combine(j.Root, "old-area-" + i), DateTimeOffset.UtcNow)).ToArray());
            var phases=new List<string>();
            using (var prepared = RefreshExecution.Prepare(bounded, Find(bounded, j.A), 10, 1, Limits, progress:new Forward(p=>phases.Add(p.Current))))
            {
                Check(prepared.Action == RefreshAction.MeasureScope && prepared.ValidateForCommit().RefreshedAreas.Count == 0 && bounded.RefreshedAreas.Count == RefreshRebuild.MaximumAreas, "Provenance bound forces full measurement without modifying the old bounded history");
                Check(prepared.Reason==RefreshReason.MeasurementHistoryLimit && !phases.Any(p=>p.StartsWith("Seçili alan yeniden ölçülüyor:")),"Known history bound bypasses selected traversal with the truthful reason");
            }
            var indexed=Scan([j.Root],10,[j.Skip]);long baseOld=(16L<<20)+1024L*indexed.RefreshIndex!.DirectoryCount+192L*indexed.RefreshIndex.FileCount;
            Check(!RefreshRebuild.CanMeasure(indexed.RefreshIndex,j.A,10,baseOld-1,default).Fits,"An old pool already exceeding rebuild scratch is rejected at admission");
            Log.Add($"ALL CHECKS PASSED ({Log.Count})"); return 0;
        }
        catch (Exception ex) { Log.Add("FAIL " + ex); return 1; }
        finally { File.WriteAllLines(Path.Combine(output, "results.txt"), Log); foreach (string line in Log) Console.WriteLine(line); }
    }
}
