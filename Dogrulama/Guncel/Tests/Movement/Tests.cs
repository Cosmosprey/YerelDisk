using System.Runtime.InteropServices;
using YerelDisk;
string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);string fixtures=Path.Combine(output,"own-fixture");Directory.CreateDirectory(fixtures);var log=new List<string>();
void Check(bool ok,string message){if(!ok)throw new Exception(message);log.Add("PASS "+message);}
try
{
    foreach(int workers in new[]{1,8})foreach(int limit in new[]{1,2})
    {
        string root=Path.Combine(fixtures,$"case-{workers}-{limit}"),a=Path.Combine(root,"a"),b=Path.Combine(root,"b");Directory.CreateDirectory(a);Directory.CreateDirectory(b);
        File.WriteAllBytes(Path.Combine(a,"moves.bin"),new byte[9000]);File.WriteAllBytes(Path.Combine(a,"winner.bin"),new byte[8000]);File.WriteAllBytes(Path.Combine(a,"runner-up.bin"),new byte[7000]);File.WriteAllBytes(Path.Combine(b,"small.bin"),new byte[200]);bool moved=false;
        var result=VerifiedScan.Scan([a,b],new Forward(p=>{if(!moved&&p.Current=="Doğrulama: "+b){moved=true;File.Move(Path.Combine(a,"moves.bin"),Path.Combine(b,"moves.bin"));File.WriteAllBytes(Path.Combine(b,"moves.bin"),new byte[100]);}}),CancellationToken.None,limit,workers);
        Check(moved&&result.Files.Select(f=>f.Name).SequenceEqual(new[]{"winner.bin","runner-up.bin"}.Take(limit)), $"Move+shrink refills source candidates: workers={workers}, K={limit}");
        Check(result.Root.LogicalBytes==15300&&result.Root.FileCount==4,$"Move+shrink totals remain exact: {workers}/{limit}");
        Check(result.Files.All(f=>!f.HardLinked)&&!result.CanUsePath(Path.Combine(a,"moves.bin")),$"Stale moved name is blocked without false hard-link labels: {workers}/{limit}");
    }
    string single=Path.Combine(fixtures,"single");Directory.CreateDirectory(single);File.WriteAllBytes(Path.Combine(single,"file.bin"),new byte[3]);
    var one=VerifiedScan.Scan([single],null,CancellationToken.None,1,1);
    Check(one.Files.Single().Size==3&&!one.HasUnverified,"Single-root normal scan unchanged");
    Check(one.FileCandidates==null,"Single-root result releases temporary candidate snapshot");
    string first=Path.Combine(fixtures,"link-a"),second=Path.Combine(fixtures,"link-b");Directory.CreateDirectory(first);Directory.CreateDirectory(second);File.WriteAllBytes(Path.Combine(first,"original.bin"),new byte[8192]);
    if(!Native.CreateHardLinkW(Path.Combine(second,"alias.bin"),Path.Combine(first,"original.bin"),0))throw new System.ComponentModel.Win32Exception();
    var links=VerifiedScan.Scan([first,second],null,CancellationToken.None,2,8);var reference=MultiScan.ScanConfigured([first,second],null,CancellationToken.None,2,8);
    Check(links.Root.AllocatedBytes==reference.Root.AllocatedBytes&&links.HardLinkAliases==1,"Real cross-root hard links still count physical data once");
    Check(links.Files.Count==2&&links.Files.All(f=>f.HardLinked)&&!links.HasUnverified,"Real aliases remain actionable shared-data observations");
    log.Add($"RESULT PASS={log.Count}; Failures=0");
}
catch(Exception ex){log.Add("FAIL "+ex);File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);return 1;}
File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);return 0;
sealed class Forward(Action<ScanProgress> action):IProgress<ScanProgress>{public void Report(ScanProgress p)=>action(p);}
static class Native{[DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern bool CreateHardLinkW(string path,string target,nint security);}
