// Claude (Opus 5.5) tarafından YerelDisk 1.3.1 için yazılan testler: Codex 1.3.0 incelemesindeki
// P2 bulgusu (ilk hata sonraki kapsam değişimini gizliyor) ve kısmi sonucun "Failed" işareti.
// Kullanım: ClaudeTests131.exe <1.3.1 YerelDisk.dll> <sonuç.txt> <örnek klasör kökü>
// Yalnızca <örnek klasör kökü> altına yazar.
using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.AccessControl;
using System.Security.Principal;

static class ClaudeTests131
{
    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly List<string> log = [];
    static int failures;
    static Assembly app = null!;
    static Type? AppType(string name) => app.GetType(name) ?? System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(app)!.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(app.Location)!, "YerelDisk.Core.dll")).GetType(name);
    static string work = "";

    static object? Get(object o, string n) => o.GetType().GetProperty(n, BF)?.GetValue(o) ?? o.GetType().GetField(n, BF)?.GetValue(o);
    static T V<T>(object o, string n) => (T)Get(o, n)!;
    static List<object> Items(object o, string n) => ((IEnumerable)Get(o, n)!).Cast<object>().ToList();
    static object? Call(object o, string n, params object?[] args) => o.GetType().GetMethod(n, BF)!.Invoke(o, args);
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); log.Add("PASS " + name); }
    static string Dir(string name) { string p = Path.Combine(work, name); Directory.CreateDirectory(p); return p; }
    static Exception Inner(Exception ex) => ex is TargetInvocationException { InnerException: { } i } ? i : ex;
    sealed class Callback<T>(Action action) : IProgress<T> { public void Report(T p) => action(); }
    static object Progress(Action action) => Activator.CreateInstance(typeof(Callback<>).MakeGenericType(AppType("YerelDisk.ScanProgress")!), action)!;
    static Exception Scope(string message) => (Exception)Activator.CreateInstance(AppType("YerelDisk.ScopeChangedException")!, message)!;
    static string Flags(object r) => $"ScopeChanged={V<bool>(r, "ScopeChanged")} Failed={V<bool>(r, "Failed")} Cancelled={V<bool>(r, "Cancelled")}";
    static List<string> Reasons(object r) => Items(r, "Issues").Select(i => V<string>(i, "Reason")).ToList();

    [STAThread]
    static int Main(string[] args)
    {
        app = new AssemblyLoadContext("yeni-1.3.1").LoadFromAssemblyPath(Path.GetFullPath(args[0]));
        work = Path.Combine(Path.GetFullPath(args[2]), "claude131-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        log.Add($"DLL: {app.GetName().Version}");
        Run("FatalOrder", FatalOrder);
        Run("RealRootFailure", RealRootFailure);
        Run("NormalAndCancel", NormalAndCancel);
        Run("MultiRootFailure", MultiRootFailure);

        log.Add("Failures=" + failures);
        File.WriteAllLines(args[1], log);
        foreach (string line in log) Console.WriteLine(line);
        // Fixtures retained for validated cleanup.
        return failures == 0 ? 0 : 1;
    }
    static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception ex) { failures++; log.Add("FAIL " + name + ": " + Inner(ex)); }
    }

    // Codex's counterexample, generalised: inject stop errors into a running 4-worker ScanRun, in
    // a given order, from inside the first progress report (other workers are idle or starting).
    static object RunWithInjected(string fixture, params Exception[] errors)
    {
        Type runType = AppType("YerelDisk.ScanRun")!;
        object run = null!;
        bool injected = false;
        object progress = Progress(() =>
        {
            if (injected) return; injected = true;
            foreach (Exception error in errors) runType.GetMethod("Fail", BF)!.Invoke(run, [error]);
        });
        run = Activator.CreateInstance(runType, BF, null, [fixture, progress, CancellationToken.None, 100000, new HashSet<(uint, ulong)>()], null)!;
        return runType.GetMethod("Execute", BF)!.Invoke(run, [4])!;
    }
    static void FatalOrder()
    {
        string fixture = Dir("hata-sirasi");
        for (int i = 0; i < 20; i++) { string d = Dir(Path.Combine("hata-sirasi", "k" + i)); File.WriteAllBytes(Path.Combine(d, "a.bin"), new byte[10]); }

        object r = RunWithInjected(fixture, new IOException("test: önce G/Ç hatası"), Scope("test: sonra kapsam değişimi"));
        log.Add("DIAG G/Ç -> kapsam: " + Flags(r) + " | " + string.Join(" | ", Reasons(r)));
        Check(V<bool>(r, "ScopeChanged") && V<bool>(r, "Failed") && !V<bool>(r, "Cancelled")
            && Reasons(r).Contains("test: önce G/Ç hatası") && Reasons(r).Contains("test: sonra kapsam değişimi"),
            "Codex karşı örneği: önce G/Ç hatası sonra kapsam değişimi -> ScopeChanged işaretlenir, iki hata da listelenir");

        r = RunWithInjected(fixture, Scope("test: önce kapsam değişimi"), new IOException("test: sonra G/Ç hatası"));
        Check(V<bool>(r, "ScopeChanged") && V<bool>(r, "Failed") && Reasons(r).Contains("test: önce kapsam değişimi") && Reasons(r).Contains("test: sonra G/Ç hatası"),
            "Ters sıra: önce kapsam değişimi sonra G/Ç hatası -> ikisi de korunur");

        r = RunWithInjected(fixture, Scope("test: aynı kapsam iletisi"), Scope("test: aynı kapsam iletisi"));
        Check(V<bool>(r, "ScopeChanged") && !V<bool>(r, "Failed") && Reasons(r).Count(x => x == "test: aynı kapsam iletisi") == 1,
            "Aynı kapsam değişimini iki işçi bildirirse tek uyarı yazılır");

        foreach (bool scopeFirst in new[] { true, false })
        {
            Exception? thrown = null;
            Exception bug = new InvalidOperationException("test: program hatası");
            try { RunWithInjected(fixture, scopeFirst ? [Scope("test: kapsam"), bug] : [bug, Scope("test: kapsam")]); }
            catch (Exception ex) { thrown = Inner(ex); }
            Check(ReferenceEquals(thrown, bug), $"Program hatası, kapsam değişimi {(scopeFirst ? "önce" : "sonra")} gelse de yutulmaz; çağırana iletilir (1.2.1 davranışı)");
        }

        r = RunWithInjected(fixture, new IOException("test: yalnız G/Ç hatası"));
        Check(!V<bool>(r, "ScopeChanged") && V<bool>(r, "Failed") && !V<bool>(r, "Cancelled") && Reasons(r).Contains("test: yalnız G/Ç hatası"),
            "Yalnız kök düzeyi G/Ç hatası -> Failed (kısmi), Cancelled değil, kapsam değişimi değil");
    }

    static string DeniedRoot(string name, out Action restore)
    {
        string denied = Dir(name);
        File.WriteAllBytes(Path.Combine(denied, "gizli.bin"), new byte[10]);
        DirectoryInfo info = new(denied);
        DirectorySecurity acl = info.GetAccessControl();
        FileSystemAccessRule deny = new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        // SetAccessControl writes only modified sections: remove the rule explicitly (1.3.0 report, 1b).
        restore = () => { acl.RemoveAccessRuleSpecific(deny); info.SetAccessControl(acl); };
        return denied;
    }
    static object ScanW(string path, int workers, CancellationToken token = default, object? progress = null)
        => AppType("YerelDisk.DiskScanner")!.GetMethod("ScanCore", BF)!.Invoke(null, [path, progress, token, 100000, new HashSet<(uint, ulong)>(), workers])!;
    static void RealRootFailure()
    {
        string denied = DeniedRoot("kok-erisim-yok", out Action restore);
        try
        {
            foreach (int w in new[] { 1, 4 })
            {
                object r = ScanW(denied, w);
                Check(V<bool>(r, "Failed") && !V<bool>(r, "Cancelled") && !V<bool>(r, "ScopeChanged")
                    && V<long>(r, "IssueCount") == 1 && V<long>(V<object>(r, "Root"), "FileCount") == 0,
                    $"Gerçek kök hatası ({w} işçi): listelenemeyen kök 'Failed' olarak işaretlenir, tek uyarı, 0 dosya");
            }
        }
        finally { restore(); }
        Check(Directory.GetFiles(denied).Length == 1, "Testin erişim reddi kuralı kaldırıldı");
    }

    static void NormalAndCancel()
    {
        string root = Dir("normal");
        for (int i = 0; i < 30; i++) { string d = Dir(Path.Combine("normal", "k" + i)); for (int f = 0; f < 50; f++) File.WriteAllBytes(Path.Combine(d, $"{f}.bin"), new byte[f]); }
        object ok = ScanW(root, 4);
        Check(Flags(ok) == "ScopeChanged=False Failed=False Cancelled=False" && V<long>(V<object>(ok, "Root"), "FileCount") == 1500, "Normal tarama: hiçbir kısmi işaret yok");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        object cancelled = ScanW(root, 4, cts.Token);
        Check(Flags(cancelled) == "ScopeChanged=False Failed=False Cancelled=True", "Kullanıcı iptali yalnız 'Cancelled' işaretler");
    }

    static void MultiRootFailure()
    {
        string good = Dir("coklu-iyi");
        File.WriteAllBytes(Path.Combine(good, "x.bin"), new byte[10]);
        string denied = DeniedRoot("coklu-erisim-yok", out Action restore);
        try
        {
            object r = AppType("YerelDisk.MultiScan")!.GetMethod("Scan", BF)!.Invoke(null, [new[] { denied, good }, null, CancellationToken.None, 100000])!;
            Check(V<bool>(r, "Failed") && !V<bool>(r, "Cancelled") && V<long>(V<object>(r, "Root"), "FileCount") == 1 && Items(V<object>(r, "Root"), "Children").Count == 2,
                "Çoklu kök: bir kök hatayla durursa birleşik sonuç 'Failed' olur, diğer kök yine taranır");
        }
        finally { restore(); }
    }

}
