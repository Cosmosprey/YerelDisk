using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using YerelDisk;

internal static class NativeExclusionTests
{
    static readonly List<string> log = [];
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool CreateHardLinkW(string link, string source, IntPtr security);
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); log.Add("PASS " + label); }
    static ScanResult Scan(string root, params string[] excluded) => VerifiedScan.Scan([root], null, CancellationToken.None, 100, 4, excluded);
    static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        string fixtures=Path.Combine(output,"own-fixtures"), root=Path.Combine(fixtures,"root"), outside=Path.Combine(fixtures,"outside"), omit=Path.Combine(root,"omit");
        Directory.CreateDirectory(omit);Directory.CreateDirectory(outside);File.WriteAllBytes(Path.Combine(outside,"outside.bin"),new byte[9000]);
        try
        {
            string original=Path.Combine(omit,"data.bin"), kept=Path.Combine(root,"kept.bin");File.WriteAllBytes(original,new byte[4096]);
            Check(CreateHardLinkW(kept,original,IntPtr.Zero),"Create same-volume excluded/included hard link");
            var single=Scan(root,omit);
            Check(single.Root.LogicalBytes==4096 && single.Root.FileCount==1 && single.HardLinkAliases==0 && single.Root.AllocatedBytes>0,"Excluded hard link name does not suppress included allocation or inflate alias count");
            Check(CreateHardLinkW(Path.Combine(root,"alias.bin"),original,IntPtr.Zero),"Create second included hard link");
            var two=Scan(root,omit);
            Check(two.Root.LogicalBytes==8192 && two.Root.FileCount==2 && two.HardLinkAliases==1 && two.Root.AllocatedBytes==single.Root.AllocatedBytes,"Only included hard link names participate in physical deduplication");
            string junction=Path.Combine(root,"junction");
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
            start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+junction.Replace("'","''")+"' -Target '"+outside.Replace("'","''")+"' | Out-Null");
            using(var process=Process.Start(start)!){string error=process.StandardError.ReadToEnd();process.StandardOutput.ReadToEnd();process.WaitForExit();if(process.ExitCode!=0)throw new Exception(error);}
            Check((File.GetAttributes(junction)&FileAttributes.ReparsePoint)!=0,"Create real junction fixture");
            var skipLink=Scan(root,omit,junction);
            Check(skipLink.Root.FileCount==2 && skipLink.IssueCount==0 && !skipLink.HasUnverified && skipLink.Root.Children.Count==0,"Excluded junction is skipped before open or access-warning generation");
            var noRule=Scan(root,omit);
            Check(noRule.Root.FileCount==2 && noRule.Issues.Any(i=>i.Path==junction) && noRule.Root.Children.Single().Measurement==MeasurementState.Unknown,"Unexcluded junction still obeys existing no-follow policy");
            var directory=new DirectoryInfo(omit);string acl=directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            try
            {
                var changed=directory.GetAccessControl();changed.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.ListDirectory,AccessControlType.Deny));directory.SetAccessControl(changed);
                var denied=Scan(root,omit,junction);
                Check(denied.Root.FileCount==2 && denied.IssueCount==0 && !denied.HasUnverified,"Excluded denied directory is not opened or counted as error");
            }
            finally {var restored=new DirectorySecurity();restored.SetSecurityDescriptorSddlForm(acl,AccessControlSections.Access);directory.SetAccessControl(restored);}
            Check(File.ReadAllBytes(original).Length==4096 && File.ReadAllBytes(Path.Combine(outside,"outside.bin")).Length==9000,"Fixture ACL restored and excluded/outside contents preserved");
            log.Add($"ALL CHECKS PASSED ({log.Count})");File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(ex);return 1;}
    }
}
