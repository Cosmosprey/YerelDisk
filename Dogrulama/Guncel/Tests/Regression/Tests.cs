using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

static class Regression
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly app = null!;
    static Type? AppType(string name) => app.GetType(name) ?? System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(app)!.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(app.Location)!, "YerelDisk.Core.dll")).GetType(name);
    static string fixtures = "";
    static readonly List<string> log = [];
    static object? Get(object o, string n) => o.GetType().GetProperty(n, Flags)?.GetValue(o) ?? o.GetType().GetField(n, Flags)?.GetValue(o);
    static T V<T>(object o, string n) => (T)Get(o, n)!;
    static object? Call(object o, string n, params object?[] args) => o.GetType().GetMethod(n, Flags)!.Invoke(o, args);
    static object Scan(string p, object? progress = null, CancellationToken token = default, int cap = 100000)
        => AppType("YerelDisk.DiskScanner")!.GetMethod("Scan", Flags)!.Invoke(null, [p, progress, token, cap])!;
    static List<object> Files(object r) => ((IEnumerable)Get(r, "Files")!).Cast<object>().ToList();
    static object Root(object r) => Get(r, "Root")!;
    static void Check(bool yes, string name)
    {
        if (!yes) throw new Exception(name);
        log.Add("PASS " + name);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string p, string original, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetCompressedFileSizeW(string p, out uint high);
    static long Physical(string p) { uint low = GetCompressedFileSizeW(p, out uint high); return ((long)high << 32) | low; }
    static string Dir(string name) { string p = Path.Combine(fixtures, name); Directory.CreateDirectory(p); return p; }
    sealed class Callback<T>(Action<object> action) : IProgress<T> { public void Report(T p) => action(p!); }
    static object Progress(Action<object> action) => Activator.CreateInstance(typeof(Callback<>).MakeGenericType(AppType("YerelDisk.ScanProgress")!), action)!;

    [STAThread]
    static int Main(string[] args)
    {
        fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        app = Assembly.LoadFrom(args[0]);
        int failures = 0;
        foreach (var test in new Action[] { Ads, DirectoryAds, RootRace, ChildRace, MidCancel })
        {
            try { test(); }
            catch (Exception ex) { failures++; log.Add("FAIL " + test.Method.Name + ": " + ex); }
        }
        log.Add("Failures=" + failures);
        File.WriteAllLines(args[1], log);
        foreach (string line in log) Console.WriteLine(line);
        return failures == 0 ? 0 : 1;
    }

    static void Ads()
    {
        string d = Dir("ads"), p = Path.Combine(d, "tiny.bin");
        File.WriteAllBytes(p, [42]);
        using (FileStream f = new(p + ":large", FileMode.Create, FileAccess.Write, FileShare.Read)) f.Write(new byte[8 * 1024 * 1024]);
        using (FileStream f = new(p + ":second", FileMode.Create, FileAccess.Write, FileShare.Read)) f.Write(new byte[300]);
        File.WriteAllBytes(Path.Combine(d, "medium.bin"), new byte[2 * 1024 * 1024]);
        var r = Scan(d, cap: 1); var row = Files(r).Single();
        Check(V<string>(row, "Path") == p && V<long>(row, "Size") == 8 * 1024 * 1024 + 301, "YD-03 all streams affect ranking and file size");
        Check(V<int>(row, "ExtraStreams") == 2 && V<long>(row, "ExtraStreamBytes") == 8 * 1024 * 1024 + 300, "YD-03 named stream count and bytes visible");
        Check(V<long>(Root(r), "LogicalBytes") == 10 * 1024 * 1024 + 301 && V<long>(r, "IssueCount") == 0, "YD-03 folder totals include streams without false warning");
        Check(Convert.ToInt64(Get(row, "Allocated")) >= Physical(p + ":large") + Physical(p + ":second"), "YD-03 physical allocation includes named streams");
        long physicalBefore = V<long>(Root(Scan(d)), "AllocatedBytes");
        Check(CreateHardLinkW(Path.Combine(d, "alias.bin"), p, IntPtr.Zero), "Create named-stream hard link fixture");
        var linked = Scan(d);
        Check(V<long>(Root(linked), "AllocatedBytes") == physicalBefore && V<long>(linked, "HardLinkAliases") == 1, "YD-03 physical stream allocation deduplicated across hard links");
    }

    static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
        using var process = Process.Start(psi)!;
        string text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception(text);
    }
    static void DirectoryAds()
    {
        string d = Dir("directory-ads"), child = Path.Combine(d, "child"); Directory.CreateDirectory(child);
        using (FileStream f = new(d + ":root-payload", FileMode.Create, FileAccess.Write, FileShare.Read)) f.Write(new byte[8 * 1024 * 1024]);
        using (FileStream f = new(child + ":child-payload", FileMode.Create, FileAccess.Write, FileShare.Read)) f.Write(new byte[1024 * 1024]);
        File.WriteAllBytes(Path.Combine(child, "ordinary.bin"), new byte[23]);
        var r = Scan(d); var root = Root(r);
        var node = ((IEnumerable)Get(root, "Children")!).Cast<object>().Single();
        Check(V<long>(root,"LogicalBytes") == 9 * 1024 * 1024 + 23 && V<long>(node,"LogicalBytes") == 1024 * 1024 + 23,
            "YD-03 directory ADS counted once in root and descendant totals");
        Check(V<int>(root,"OwnStreams") == 1 && V<long>(root,"OwnStreamBytes") == 8 * 1024 * 1024 && V<int>(node,"OwnStreams") == 1,
            "YD-03 directory own-stream metadata exposed separately");
        Check(V<long>(root,"AllocatedBytes") >= Physical(d + ":root-payload") + Physical(child + ":child-payload") && V<long>(r,"IssueCount") == 0,
            "YD-03 directory stream physical allocation counted without false warnings");
        Check(V<long>(root,"FileCount") == 1 && Files(r).Count == 1, "YD-03 directory streams do not inflate file count or duplicate file rows");
        var empty = Scan(Dir("empty-directory"));
        Check(V<long>(Root(empty),"LogicalBytes") == 0 && V<long>(Root(empty),"AllocatedBytes") == 0 && V<long>(empty,"IssueCount") == 0,
            "YD-03 empty directory filesystem index is not counted as file data");
    }
    static void RootRace()
    {
        string d = Dir("root-race"), entry = Path.Combine(d, "entry"), old = Path.Combine(d, "old"), outside = Path.Combine(d, "outside");
        Directory.CreateDirectory(Path.Combine(entry, "root")); Directory.CreateDirectory(Path.Combine(outside, "root"));
        File.WriteAllBytes(Path.Combine(entry, "root", "inside.bin"), new byte[11]);
        File.WriteAllBytes(Path.Combine(outside, "root", "outside-marker.bin"), new byte[999]);
        bool attempted = false, changed = false, blocked = false;
        var progress = Progress(_ =>
        {
            if (attempted) return; attempted = true;
            try { Directory.Move(entry, old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { blocked = true; return; }
            changed = true; Junction(entry, outside);
        });
        try
        {
            var r = Scan(Path.Combine(entry, "root"), progress);
            log.Add($"ROOT_RACE diagnostic scope={V<bool>(r,"ScopeChanged")} issues={V<long>(r,"IssueCount")} logical={V<long>(Root(r),"LogicalBytes")} changed={changed} blocked={blocked} details=" +
                string.Join(" | ", ((IEnumerable)Get(r,"Issues")!).Cast<object>().Select(x => V<string>(x,"Reason"))));
            Check(blocked ? V<long>(Root(r),"LogicalBytes") == 11 : V<bool>(r, "ScopeChanged") && V<long>(r, "IssueCount") >= 1,
                "YD-01 ancestor swap is prevented, or detected as explicit partial scope");
            Check(!Files(r).Any(f => V<string>(f, "Name") == "outside-marker.bin") && V<long>(Root(r), "LogicalBytes") != 999, "YD-01 original root race never scans replacement target");
            if (blocked)
            {
                Directory.Move(entry, old); Directory.Move(old, entry);
                Check(true, "YD-01 anchored handles released after scan; ancestor can be renamed again");
            }
        }
        finally
        {
            if (Directory.Exists(entry) && File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(entry);
            if (Directory.Exists(old) && !Directory.Exists(entry)) Directory.Move(old, entry);
        }
    }
    static void ChildRace()
    {
        string root = Dir("child-race"), branch = Path.Combine(root, "branch"), old = Path.Combine(fixtures, "child-old"), outside = Dir("child-outside");
        Directory.CreateDirectory(branch);
        File.WriteAllBytes(Path.Combine(branch, "inside.bin"), new byte[11]);
        File.WriteAllBytes(Path.Combine(outside, "outside-marker.bin"), new byte[999]);
        int reports = 0; bool changed = false;
        var progress = Progress(_ =>
        {
            reports++;
            if (reports == 1) { Thread.Sleep(210); return; }
            if (!changed) { changed = true; Directory.Move(branch, old); Junction(branch, outside); }
        });
        try
        {
            var r = Scan(root, progress);
            Check(changed && V<bool>(r, "ScopeChanged") && V<long>(r, "IssueCount") > 0, "YD-01 child swapped after enumeration is rejected");
            Check(!Files(r).Any(f => V<string>(f, "Name") == "outside-marker.bin"), "YD-01 child replacement target is never traversed");
        }
        finally
        {
            if (Directory.Exists(branch) && File.GetAttributes(branch).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(branch);
            if (Directory.Exists(old) && !Directory.Exists(branch)) Directory.Move(old, branch);
        }
    }
    static void MidCancel()
    {
        string d = Dir("cancel"); for (int i = 0; i < 500; i++) File.WriteAllBytes(Path.Combine(d, $"{i:D4}.bin"), new byte[i + 1]);
        using var c = new CancellationTokenSource();
        var progress = Progress(p => { if (V<long>(p, "Files") > 0) c.Cancel(); else Thread.Sleep(210); });
        var r = Scan(d, progress, c.Token);
        Check(V<bool>(r, "Cancelled") && !V<bool>(r, "ScopeChanged") && V<long>(Root(r), "FileCount") is > 0 and < 500,
            "Handle traversal retains live cancellation semantics");
    }
}
