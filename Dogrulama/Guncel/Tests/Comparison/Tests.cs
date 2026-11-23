using System.Diagnostics;
using System.Text.Json;
using YerelDisk;

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
var log = new List<string>();
var timestamp = DateTimeOffset.UtcNow;
const string root = @"C:\Örnek";
const string other = @"D:\Diğer";
const string partial = root + @"\Kısmi";
const string safe = partial + @"\Ölçüldü";
const string lost = partial + @"\Kayboldu";
const string unsafeBranch = root + @"\Eski";
const string unsafeChild = unsafeBranch + @"\Alt";
const string excluded = @"c:\örnek\Hariç";

try
{
    var first = Example(100, 20);
    var second = Example(220, null);
    var reference = FolderMeasurements.Capture(first, timestamp);
    var current = FolderMeasurements.Capture(second, timestamp.AddHours(1));
    var comparison = new FolderComparison(reference, current);
    Check(comparison.Largest is [{ Path: safe, Difference: 120 }], "Verified child ranks under a partial ancestor");
    Check(comparison.At(partial).Single(row => row.Path == lost).After == null, "Absent child under a partial parent never becomes zero");
    Check(comparison.At(root).Single(row => row.Path == partial) is { Difference: null, CanEnter: true }, "Partial branch can be browsed without a total");
    Check(comparison.At(unsafeBranch).Single().Difference == null, "Previous ancestor blocks seemingly measured descendant");
    Check(comparison.At(null).Single().Difference == null, "Partial selected root stays uncomparable");
    Check(comparison.UncomparableCount == 5, "Coverage count includes the unavailable path and its parent areas");
    Reject(() => FolderMeasurements.Capture(new ScanResult(new(root, null)) { Cancelled = true }, timestamp), "Cancelled scan cannot be used");
    Reject(() => FolderMeasurements.Capture(new ScanResult(new(root, null)) { ScopeChanged = true }, timestamp), "Unlocalized scope change cannot be used");
    var unavailable=VerifiedScan.Scan([Path.Combine(output,"own-fixtures","missing-root")],null,CancellationToken.None,10,1);
    Check(unavailable.Failed && unavailable.UnverifiedPaths.Count>0,"Real missing-root output includes an unsafe root marker");
    Reject(()=>FolderMeasurements.Capture(unavailable,timestamp),"Wholly unavailable native result cannot become a reference");
    var combined=new ScanResult(new FolderEntry("selected",null){Measurement=MeasurementState.Partial}){Failed=true,ScopeChanged=true};
    combined.RootPaths.Clear();combined.RootPaths.AddRange([root,other]);combined.UnverifiedPaths.Add(root);
    combined.Root.Children.AddRange([new(root,combined.Root){Measurement=MeasurementState.Unknown},new(other,combined.Root){LogicalBytes=9}]);
    Check(FolderMeasurements.Capture(combined,timestamp).Folders.Single(f=>f.Path==other).Reliable,
        "Healthy selected root remains referenceable when another root is wholly unavailable");
    var overlapping=current with {Exclusions=[],Roots=[root,root.ToLowerInvariant()+@"\Alt"],Folders=[new(root,10,true),new(root.ToLowerInvariant()+@"\Alt",100,true)]};
    Reject(()=>new FolderComparison(overlapping,overlapping),"Case-insensitive nested roots are rejected");
    var duplicateRoot=current with {Exclusions=[],Roots=[root,root.ToLowerInvariant()],Folders=[new(root,10,true),new(root.ToLowerInvariant(),10,true)]};
    Reject(()=>new FolderComparison(duplicateRoot,duplicateRoot),"Case-insensitive duplicate roots are rejected");
    var impossible=current with {Exclusions=[],Folders=[new(root,10,true),new(root+@"\child",11,true)]};
    Reject(()=>new FolderComparison(impossible,impossible),"Reliable child totals cannot exceed a reliable parent");
    Reject(() => new FolderComparison(reference, current with { Exclusions = [] }), "Changed exclusions rejected");
    Reject(() => new FolderComparison(reference, current with { Roots = [root, other], Folders = [.. current.Folders, new(other, 0, false)] }), "Changed roots rejected");
    Reject(() => new FolderComparison(reference, current with { Format = FolderMeasurements.CurrentFormat + 1 }), "Unknown format rejected");
    Reject(() => new FolderComparison(reference, current with { Folders = [.. current.Folders, current.Folders[0]] }), "Duplicate path rejected");
    Reject(() => new FolderComparison(reference, current with { Folders = [.. current.Folders, new(root + @"\Missing\Orphan", 1, true)] }), "Orphan row rejected");
    Reject(() => new FolderComparison(reference, current with { Folders = [.. current.Folders, new(root + @"\Hariç", 1, true)] }), "Excluded row rejected");
    var multi = current with { Roots = [root, other], Folders = [.. current.Folders, new(other, 0, true)] };
    Check(new FolderComparison(multi, multi with { Roots = [other, root], Exclusions = [excluded.ToUpperInvariant()] }).At(null).Count == 2,
        "Root selection order and exclusion casing do not change scope");
    var marked = Example(220, null);
    marked.UnverifiedPaths.Add(partial);
    Reject(()=>FolderMeasurements.Capture(marked, timestamp), "Unverified ancestors leaving no reliable folder cannot produce a reference");

    Guid volume = Guid.NewGuid();
    var bound = reference with { RootVolumes = [new(root, volume)] };
    Reject(() => new FolderComparison(bound, current with { RootVolumes = [new(root, Guid.NewGuid())] }),
        "The same drive letter with a different volume is rejected");
    Reject(() => new FolderComparison(bound, current with { RootVolumes = [new(other, volume)] }), "Volume for an unselected root rejected");
    Check(!new FolderComparison(bound, current with { RootVolumes = bound.RootVolumes }).HasUnverifiedVolumeIdentity,
        "Matching volumes are verified");
    Check(new FolderComparison(bound, current).HasUnverifiedVolumeIdentity, "Missing volume proof is visible");

    var upper = reference with { Exclusions = [], Folders = [new(root, 100, true), new(root + @"\Foo", 100, true),
        new(root + @"\Foo\Nested", 100, true)] };
    var lower = upper with { Folders = [new(root, 100, true), new(root + @"\foo", 100, true),
        new(root + @"\foo\Nested", 100, true)] };
    var casing = new FolderComparison(upper, lower);
    Check(casing.Largest.Count == 0 && casing.At(root).All(row => row.Difference == null) && casing.UncomparableCount == 4,
        "Case-only variants do not create changes or inferred absence in their subtrees");
    Check(casing.At(root + @"\Foo").Single().After == null, "Drill-down preserves case ambiguity");
    var both = upper with { Folders = [new(root,300,true),.. upper.Folders.Skip(1), new(root + @"\foo", 200, true)] };
    var bothChanged = both with { Folders = [new(root,330,true),.. upper.Folders.Skip(1), new(root + @"\foo", 230, true)] };
    Check(new FolderComparison(both, bothChanged).At(root).Single(row=>row.Path==root+@"\foo").Difference==30, "Exact matches keep distinct case-sensitive sibling rows");

    string fixtures = Path.Combine(output, "own-fixtures");
    string measured = Path.Combine(fixtures, "measured");
    string grow = Directory.CreateDirectory(Path.Combine(measured, "grow")).FullName;
    string shrink = Directory.CreateDirectory(Path.Combine(measured, "shrink")).FullName;
    string removed = Directory.CreateDirectory(Path.Combine(measured, "removed")).FullName;
    string file = Path.Combine(shrink, "file.bin");
    File.WriteAllBytes(file, new byte[4096]);
    string deleted = Path.Combine(removed, "removed.bin");
    File.WriteAllBytes(deleted, new byte[2048]);
    string baseline = Path.Combine(fixtures, "reference.yereldisk.json");
    NativeCapture().Write(baseline);
    File.Move(file, Path.Combine(grow, "moved.bin"));
    File.WriteAllBytes(Path.Combine(grow, "added.bin"), new byte[8192]);
    File.Delete(deleted);
    Directory.Delete(removed);
    string added = Directory.CreateDirectory(Path.Combine(measured, "added", "leaf")).FullName;
    File.WriteAllBytes(Path.Combine(added, "payload.bin"), new byte[1024]);
    var live = NativeCapture();
    var native = new FolderComparison(FolderMeasurements.Read(baseline), live);
    Check(!native.HasUnverifiedVolumeIdentity && live.RootVolumes is [{ Id: var nativeId }] && nativeId != Guid.Empty,
        "Native scan obtains a volume identity from its pinned handle");
    var changes = native.At(measured).ToDictionary(row => row.Path);
    Check(changes[grow].Difference == 12288 && changes[shrink].Difference == -4096, "Real scans report addition and movement");
    Check(changes[removed] is { Difference: -2048, After: 0 }, "Verified missing folder reports decrease");
    Check(native.At(Path.GetDirectoryName(added)).Single() is { Difference: 1024, Before: 0 }, "New subtree can be browsed with a verified absent side");
    Check(!File.ReadAllText(baseline).Contains("removed.bin", StringComparison.Ordinal), "Saved measurement omits file paths");
    using (var pin = new FileStream(baseline, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { live.Write(baseline); throw new InvalidOperationException("Locked reference was replaced"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Check(new FolderComparison(FolderMeasurements.Read(baseline), live).Largest.Count > 0
                && !Directory.EnumerateFiles(fixtures, "*.tmp").Any(), "Failed replacement preserves the reference and cleans temporary output");
        }
    }
    live.Write(baseline);
    Check(new FolderComparison(FolderMeasurements.Read(baseline), live).Largest.Count == 0 && !Directory.EnumerateFiles(fixtures, "*.tmp").Any(),
        "Explicit replacement completes and removes temporary file");
    string bad = Path.Combine(fixtures, "bad.yereldisk.json");
    var legacy = live with { Format = 1, RootVolumes = null };
    legacy.Write(bad);
    var legacyComparison = new FolderComparison(FolderMeasurements.Read(bad), live);
    Check(legacyComparison.HasUnverifiedVolumeIdentity && legacyComparison.Largest.Count == 0
        && !File.ReadAllText(bad).Contains("RootVolumes", StringComparison.Ordinal), "Existing format-one reference opens with a volume warning");
    File.WriteAllText(bad, "{broken");
    try { FolderMeasurements.Read(bad); throw new InvalidOperationException("Malformed JSON accepted"); }
    catch (JsonException) { Check(true, "Malformed JSON rejected"); }
    File.WriteAllText(bad, "{\"Roots\":[" + string.Join(",", Enumerable.Repeat("\"C:\\\\\"", 10_001)) + "]}");
    try { FolderMeasurements.Read(bad); throw new InvalidOperationException("Oversized scope list accepted"); }
    catch (JsonException) { Check(true, "Reader enforces the scope record budget"); }
    File.WriteAllText(bad, File.ReadAllText(baseline).Replace("\"Reliable\":true", "\"Unknown\":true", StringComparison.Ordinal));
    try { FolderMeasurements.Read(bad); throw new InvalidOperationException("Unexpected measurement field accepted"); }
    catch (JsonException) { Check(true, "Reader rejects unexpected row fields"); }
    File.WriteAllText(bad, File.ReadAllText(baseline).Replace(",\"Reliable\":true", "", StringComparison.Ordinal));
    try { FolderMeasurements.Read(bad); throw new InvalidOperationException("Missing required field accepted"); }
    catch (JsonException) { Check(true, "Reader rejects missing measurement fields"); }
    Reject(() => FolderMeasurements.Read(@"\\invalid\share\test.yereldisk.json"), "UNC rejected before file access");
    Reject(() => FolderMeasurements.Read(Path.Combine(fixtures, "bad.yereldisk.json:stream")), "Alternate data stream path rejected");

    string historyPath = Path.Combine(fixtures, "referanslar.json");
    var library = ReferenceLibrary.Load(historyPath);
    library.SetDefaultFolder(fixtures);
    library.Record(baseline, timestamp, 123);
    Check(!library.Remember && library.Persist() == null && !File.Exists(historyPath), "Reference history is session-only before opt-in");
    Check(library.PickerDirectory() == fixtures, "Selected local folder becomes the picker default");
    for (int i = 0; i < 12; i++) library.Record(Path.Combine(fixtures, $"referans-{i}.yereldisk"), timestamp, i);
    Check(library.Recent.Count == 10 && library.Recent[0].FileBytes == 11 && library.Recent[^1].FileBytes == 2,
        "History is bounded and ordered by last use");
    string repeated = library.Recent[^1].Path;
    library.Record(repeated, timestamp.AddHours(1), 321);
    Check(library.Recent.Count == 10 && library.Recent[0].Path == repeated && library.Recent[0].FileBytes == 321,
        "Reusing a reference promotes it and updates its metadata");
    library.SetRemember(true);
    Check(library.Persist() == null && File.Exists(historyPath), "Explicit opt-in writes a local preferences file");
    var reopened = ReferenceLibrary.Load(historyPath);
    Check(reopened.Remember && reopened.DefaultFolder == fixtures && reopened.Recent.SequenceEqual(library.Recent),
        "Reopening restores metadata and the configured folder");
    reopened.SetRemember(false);
    Check(reopened.Persist() == null && !File.Exists(historyPath) && reopened.Recent.Count == 10,
        "Opt-out clears preferences on disk and keeps the current session");
    Check(library.Persist() != null && !library.Remember && !File.Exists(historyPath), "A stale session cannot restore revoked preferences");
    reopened.Record(baseline, timestamp, 123);
    reopened.Remove(baseline);
    Check(File.Exists(baseline) && reopened.Recent.All(item => item.Path != baseline), "Removing a history entry does not remove its reference file");
    string oldFolder = Directory.CreateDirectory(Path.Combine(fixtures, "temporary-picker")).FullName;
    reopened.SetDefaultFolder(oldFolder);
    Directory.Delete(oldFolder);
    Check(reopened.PickerDirectory() == "", "Unavailable default falls back to the regular file picker");
    // Claude 1.21.1: one file with different letter case is one history entry.
    string historyUpper = Path.Combine(fixtures, "HARF.yereldisk"), historyLower = Path.Combine(fixtures, "harf.yereldisk");
    reopened.Record(historyUpper, timestamp, 1);
    reopened.Record(historyLower, timestamp, 2);
    Check(reopened.Recent.Count(item => string.Equals(item.Path, historyLower, StringComparison.OrdinalIgnoreCase)) == 1
        && reopened.Recent[0].Path == historyLower, "Letter-case variants of one path share a history entry");
    reopened.Remove(historyUpper);
    Check(reopened.Recent.All(item => !string.Equals(item.Path, historyLower, StringComparison.OrdinalIgnoreCase)),
        "Removing with different letter case removes the entry");
    // Claude 1.21.1: two open instances keep each other's references instead of last-writer-wins.
    string sharedPath = Path.Combine(fixtures, "ortak-referanslar.json");
    var opener = ReferenceLibrary.Load(sharedPath);
    opener.SetRemember(true);
    opener.Record(Path.Combine(fixtures, "ilk.yereldisk"), timestamp, 1);
    Check(opener.Persist() == null, "Shared history is created by opt-in");
    var sessionA = ReferenceLibrary.Load(sharedPath);
    var sessionB = ReferenceLibrary.Load(sharedPath);
    sessionA.Record(Path.Combine(fixtures, "birinci.yereldisk"), timestamp, 1);
    sessionB.Record(Path.Combine(fixtures, "ikinci.yereldisk"), timestamp, 1);
    sessionB.Remove(Path.Combine(fixtures, "ilk.yereldisk"));
    Check(sessionA.Persist() == null && sessionB.Persist() == null, "Both instances persist");
    var merged = ReferenceLibrary.Load(sharedPath);
    Check(merged.Recent.Any(item => item.Path.EndsWith("birinci.yereldisk")) && merged.Recent.Any(item => item.Path.EndsWith("ikinci.yereldisk"))
        && merged.Recent.All(item => !item.Path.EndsWith("ilk.yereldisk")) && merged.Recent[0].Path.EndsWith("ikinci.yereldisk"),
        "Concurrent instances merge new entries and respect removals");
    File.WriteAllText(historyPath, "{broken");
    var malformedLibrary = ReferenceLibrary.Load(historyPath);
    Check(malformedLibrary.LoadWarning != null && !malformedLibrary.Remember && malformedLibrary.Recent.Count == 0,
        "Malformed preferences start an empty session");

    const int size = 100_000;
    var big = reference with { Exclusions = [], Folders = [new(root, size * 4L, true),
        .. Enumerable.Range(0, size).Select(i => new FolderMeasurement($@"{root}\folder-{i:D6}", 4, true))] };
    var larger = big with { Folders = [new(root, size * 8L, true),
        .. Enumerable.Range(0, size).Select(i => new FolderMeasurement($@"{root}\folder-{i:D6}", 8, true))] };
    string previous = File.ReadAllText(baseline);
    long allocation = GC.GetAllocatedBytesForCurrentThread();
    Reject(() => big.Write(baseline, maximumBytes: 4096), "Writer rejects a large measurement while streaming");
    Check(GC.GetAllocatedBytesForCurrentThread() - allocation < 2 * 1024 * 1024
        && previous == File.ReadAllText(baseline) && !Directory.EnumerateFiles(fixtures, "*.tmp").Any(),
        "Early write failure uses a small buffer, preserves target and removes temporary file");
    var watch = Stopwatch.StartNew();
    big.Write(baseline);
    var large = new FolderComparison(FolderMeasurements.Read(baseline), larger);
    watch.Stop();
    log.Add($"100001 klasör: kayıt, okuma ve karşılaştırma {watch.Elapsed.TotalMilliseconds:N0} ms; referans {new FileInfo(baseline).Length:N0} bayt.");
    Check(large.Largest.Count == 100 && large.Largest[0].Difference == size * 4L, "Large comparison ranks with bounded result list");

    var deepRows = new List<FolderMeasurement> { new(root, 2000, true) };
    string deepest = root;
    for (int i = 0; i < 1000; i++) { deepest += @"\a"; deepRows.Add(new(deepest, 2000, true)); }
    for (int i = 0; i < 2000; i++) deepRows.Add(new(deepest + "\\" + i, 1, true));
    var deep = big with { Folders = deepRows.ToArray() };
    var empty = big with { Folders = [new(root, 0, true)] };
    watch.Restart();
    allocation = GC.GetAllocatedBytesForCurrentThread();
    var deepComparison = new FolderComparison(deep, empty);
    var deepChildren = deepComparison.At(deepest);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocation;
    watch.Stop();
    Check(deepChildren.Count == 2000 && deepChildren.All(row => row.Difference == -1)
        && allocated < 32 * 1024 * 1024, "Deep missing branch comparison and drill-down reuse absence evidence");
    log.Add($"1000 derinlik, 2000 yaprak: {watch.Elapsed.TotalMilliseconds:N0} ms, {allocated / 1048576.0:N2} MiB toplam tahsis.");
    log.Add($"ALL CHECKS PASSED ({log.Count(line => line.StartsWith("PASS "))})");
    return 0;

    FolderMeasurements NativeCapture()
        => FolderMeasurements.Capture(VerifiedScan.Scan([measured], null, default, fileLimit: 1, workers: 4), timestamp);
}
catch (Exception ex)
{
    log.Add("FAIL " + ex);
    return 1;
}
finally
{
    File.WriteAllLines(Path.Combine(output, "results.txt"), log);
    foreach (string line in log) Console.WriteLine(line);
}

ScanResult Example(long safeBytes, long? lostBytes)
{
    var main = new FolderEntry(root, null) { Measurement = MeasurementState.Partial };
    var nested = new FolderEntry(partial, main) { Measurement = MeasurementState.Partial };
    var previous = new FolderEntry(unsafeBranch, main) { Measurement = MeasurementState.Previous };
    main.Children.AddRange([nested, previous]);
    nested.Children.Add(new(safe, nested) { LogicalBytes = safeBytes });
    if (lostBytes is long value) nested.Children.Add(new(lost, nested) { LogicalBytes = value });
    previous.Children.Add(new(unsafeChild, previous) { LogicalBytes = 42 });
    return new(main) { ExcludedPaths = [excluded] };
}
void Check(bool success, string name)
{
    if (!success) throw new InvalidOperationException(name);
    log.Add("PASS " + name);
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new InvalidOperationException(name);
}
