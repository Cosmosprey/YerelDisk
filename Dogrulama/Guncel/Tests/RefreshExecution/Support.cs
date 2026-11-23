using YerelDisk;

static class Support
{
    internal static readonly List<string> Log = [];
    internal static readonly RefreshMemoryLimits Limits = new(4 << 20, 16 << 20, 1 << 20);
    internal static void Check(bool value, string message) { if (!value) throw new Exception(message); Log.Add("PASS " + message); }
    internal static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
    internal static ScanResult Scan(string[] roots, int limit = 1, string[]? excluded = null, long budget = 4 << 20)
        => VerifiedScan.Scan(roots, null, default, limit, 1, excluded, budget);
    internal static FolderEntry Find(ScanResult scan, string path) => Walk(scan.Root).Single(folder => folder.Path == path);
    internal static IEnumerable<FolderEntry> Walk(FolderEntry root)
    {
        var todo = new Stack<FolderEntry>(); todo.Push(root);
        while (todo.TryPop(out var next)) { yield return next; foreach (var child in next.Children) todo.Push(child); }
    }
    internal static string Digest(ScanResult scan) => string.Join("|", Walk(scan.Root).OrderBy(f => f.Path).Select(f => $"{f.Path}:{f.Parent?.Path}:{f.LogicalBytes}:{f.AllocatedBytes}:{f.FileCount}:{f.IssueCount}:{f.Measurement}:{f.ObservedId}"))
        + "#" + string.Join("|", scan.Files.Select(f => f.ToString())) + "#" + scan.RefreshIndex?.FileCount;
    internal sealed class Forward(Action<ScanProgress> action) : IProgress<ScanProgress> { public void Report(ScanProgress value) => action(value); }
    internal static (string Root, string A, string B, string Skip) Tree(string output, string name)
    {
        string root = Path.Combine(output, "own-fixtures", name), a = Path.Combine(root, "a"), b = Path.Combine(root, "b"), skip = Path.Combine(a, "skip");
        Directory.CreateDirectory(skip); Directory.CreateDirectory(b);
        File.WriteAllBytes(Path.Combine(a, "winner.bin"), new byte[900]); File.WriteAllBytes(Path.Combine(a, "mail.pst"), new byte[100]);
        File.WriteAllBytes(Path.Combine(b, "runner-up.bin"), new byte[500]); File.WriteAllBytes(Path.Combine(skip, "canary.bin"), new byte[9000]);
        return (root, a, b, skip);
    }
}
