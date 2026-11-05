using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using YerelDisk;
internal static class NativeEvidenceTests
{
    static readonly List<string> log=[];
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateHardLinkW(string link,string source,nint security);
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);string home=Path.Combine(output,"own-fixtures","user"),cache=Path.Combine(home,".gradle","caches"),a=Path.Combine(cache,"a"),b=Path.Combine(cache,"b");Directory.CreateDirectory(a);Directory.CreateDirectory(b);
        var rules=new ReviewRules(home,Path.Combine(home,"local"),Path.Combine(home,"data"),@"C:\Windows");
        try
        {
            string first=Path.Combine(a,"mail.pst"),alias=Path.Combine(b,"alias.pst");System.IO.File.WriteAllBytes(first,new byte[1]);System.IO.File.WriteAllBytes(Path.Combine(cache,"big.bin"),new byte[8192]);
            Check(CreateHardLinkW(alias,first,nint.Zero),"Create cross-folder hard link fixture");
            var single=VerifiedScan.Scan([cache],null,CancellationToken.None,1,4);var c=rules.Analyze(single).Single();
            Check(single.Root.LogicalBytes==8194&&single.Root.FileCount==3&&single.HardLinkAliases==1&&c.Evidence is{ClueCount:2,Examples.Count:1},"Physical deduplication and two observed names remain distinct concepts");
            var multi=VerifiedScan.Scan([a,b],null,CancellationToken.None,1,4);var cs=rules.Analyze(multi);
            Check(multi.Root.LogicalBytes==2&&multi.Root.FileCount==2&&multi.HardLinkAliases==1&&cs.Count==2&&cs.Sum(x=>x.Evidence!.ClueCount)==2,"Cross-root linked names retain two scoped evidence records");
            string outside=Path.Combine(output,"own-fixtures","outside"),link=Path.Combine(cache,"link");Directory.CreateDirectory(outside);System.IO.File.WriteAllBytes(Path.Combine(outside,"outside.vhdx"),new byte[9]);
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-Command");start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+outside.Replace("'","''")+"' | Out-Null");
            using(var process=Process.Start(start)!){string error=process.StandardError.ReadToEnd();process.StandardOutput.ReadToEnd();process.WaitForExit();if(process.ExitCode!=0)throw new Exception(error);}
            var junction=VerifiedScan.Scan([cache],null,CancellationToken.None,1,4);var jc=rules.Analyze(junction).Single();
            Check(jc.Evidence!.DataRisks==FileDataRisk.Mail&&jc.Evidence.ClueCount==2&&jc.Incomplete&&junction.Issues.Any(i=>i.Path==link),"Skipped junction target never contributes virtual-disk evidence");
            string denied=Path.Combine(cache,"denied");Directory.CreateDirectory(denied);System.IO.File.WriteAllBytes(Path.Combine(denied,"private.gguf"),new byte[1]);var info=new DirectoryInfo(denied);string original=info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            try
            {
                var acl=info.GetAccessControl();acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.ListDirectory,AccessControlType.Deny));info.SetAccessControl(acl);
                var scan=VerifiedScan.Scan([cache],null,CancellationToken.None,1,4);var dc=rules.Analyze(scan).Single();
                Check(dc.Incomplete&&dc.Evidence!.DataRisks==FileDataRisk.Mail&&dc.Evidence.ClueCount==2,"Unreadable subtree produces incomplete scope, not invented model evidence");
            }
            finally{var acl=new DirectorySecurity();acl.SetSecurityDescriptorSddlForm(original,AccessControlSections.Access);info.SetAccessControl(acl);}
            Check(System.IO.File.ReadAllBytes(Path.Combine(denied,"private.gguf")).Length==1,"Own fixture ACL restored and data preserved");
            log.Add($"ALL CHECKS PASSED ({log.Count})");System.IO.File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);System.IO.File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(ex);return 1;}
    }
}
