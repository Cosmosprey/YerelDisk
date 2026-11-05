using System.Reflection;
using YerelDisk;

internal static class CapacityRegression
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly List<string> log = [];
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); log.Add("PASS " + name); }
    static (ScanResult Result, List<ScanNode> Nodes) Snapshot(int unique, int copies, string kind = "normal")
    {
        // Generated metadata only. The fake path is never opened.
        var run = new ScanRun(@"C:\capacity-regression", null, CancellationToken.None, 5, [], new ScanPolicy((1, 1)));
        var result = (ScanResult)typeof(ScanRun).GetField("result", Flags)!.GetValue(run)!;
        var nodes = (List<ScanNode>)typeof(ScanRun).GetField("nodes", Flags)!.GetValue(run)!;
        var map = (System.Collections.Concurrent.ConcurrentDictionary<FolderEntry, ScanNode>)typeof(ScanRun).GetField("nodeMap", Flags)!.GetValue(run)!;
        var root = new ScanNode(result.Root, null, null, "") { Enumerated = true }; nodes.Add(root); map[result.Root] = root;
        string[] names = Enumerable.Range(0, unique).Select(i => "file-" + i + ".bin").ToArray();
        for (int copy = 0; copy < copies; copy++)
        {
            var node = root;
            if (copy != 0)
            {
                var folder = new FolderEntry(Path.Combine(result.Root.Path, "copy-" + copy), result.Root);
                result.Root.Children.Add(folder); node = new(folder, root, null, "copy-" + copy) { Enumerated = true };
                root.Children.Add(node); nodes.Add(node); map[folder] = node;
            }
            for (int i = 0; i < unique; i++) node.Files.Add(new(names[i], 100, kind == "unknown" ? null : kind == "zero" ? 0 : 4096,
                DateTime.MinValue, 1, (ulong)i + 1, kind == "hard", 0, 0, kind == "unknown", ""));
        }
        typeof(ScanRun).GetMethod("AggregateObservations", Flags)!.Invoke(run, null);
        return (result, nodes);
    }
    static IEnumerable<ScanNode> WithUnreadTail(ScanNode node)
    { yield return node; throw new Exception("Capacity estimator kept reading after its budget was reached."); }
    static int Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        try
        {
            var stale = Snapshot(20_000, 8);
            Check(stale.Result.PhysicalLinks.Count == 20_000 && stale.Result.PhysicalLinks.EnsureCapacity(0) <= 40_000,
                "160k stale observations of 20k IDs do not eagerly reserve for every observation");
            Check(stale.Result.HardLinkAliases == 0 && stale.Result.Files.All(f => !f.HardLinked && !stale.Result.CanUsePath(f.Path)),
                "Repeated single-link observations remain blocked stale paths without false hard-link labels");
            var growing = Snapshot(40_000, 1);
            Check(growing.Result.PhysicalLinks.Count == 40_000 && growing.Result.Root.FileCount == 40_000
                && growing.Result.Root.LogicalBytes == 4_000_000 && growing.Result.Root.AllocatedBytes == 163_840_000,
                "Dictionary grows beyond initial hint and preserves all 40k identities and totals");
            Check(growing.Result.Files.Count == 5 && growing.Result.FileCandidates!.Count() == 40_000,
                "Initial hint and top-K do not truncate the repair candidate pool");
            var unknown = Snapshot(20_000, 1, "unknown");
            Check(unknown.Result.PhysicalLinks.Count == 0 && unknown.Result.PhysicalLinks.EnsureCapacity(0) == 0 && unknown.Result.Root.LogicalBytes == 2_000_000,
                "Unknown allocations keep logical observations without a speculative identity table");
            var zero = Snapshot(250, 1, "zero");
            Check(zero.Result.PhysicalLinks.Count == 250 && zero.Result.Root.AllocatedBytes == 0 && zero.Result.Root.LogicalBytes == 25_000,
                "Zero-byte allocation remains a known allocation with identity tracking");
            var hard = Snapshot(1000, 20, "hard");
            Check(hard.Result.PhysicalLinks.Count == 1000 && hard.Result.PhysicalLinks.EnsureCapacity(0) < 4000 && hard.Result.HardLinkAliases == 19_000,
                "Known hard-link aliases retain exact sharing without name-sized reservation");
            Check(IdentityCapacity.ForRoots(Enumerable.Repeat(stale.Result, 8)) == 20_000
                && IdentityCapacity.ForRoots([hard.Result]) == 1000,
                "Root hint uses already distinct map entries even when IDs recur across roots");
            Check(IdentityCapacity.ForRoots([growing.Result]) < 40_000 && IdentityCapacity.ForRoots([]) == 0
                && IdentityCapacity.ForObservations([]) == 0,
                "Large and empty root sets keep a bounded initial hint");
            Check(IdentityCapacity.ForObservations(WithUnreadTail(growing.Nodes[0])) < 40_000,
                "Snapshot estimate stops early once the reservation budget is filled");
            log.Add($"ALL CHECKS PASSED ({log.Count})"); Console.WriteLine(log[^1]); return 0;
        }
        catch (Exception ex) { log.Add("FAIL " + ex); Console.WriteLine(ex); return 1; }
        finally { File.WriteAllLines(Path.Combine(args[0], "results.txt"), log); }
    }
}
