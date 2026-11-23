using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using YerelDisk;

internal static class Tests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);string fixtures=Path.Combine(output,"own-fixtures");Directory.CreateDirectory(fixtures);
        try
        {
            string outside=Path.Combine(fixtures,"outside");Directory.CreateDirectory(outside);string store=Path.Combine(outside,"profiles.json");
            var saved=new ScanProfiles(store);saved.Save("Offline scope",[@"R:\unmounted"],[]);saved.SetRemember(true);
            Check(saved.Persist()==null,"Own preference file writes through the explicit local persistence path");
            Check(ScanProfiles.Load(store).Profiles.Single().Roots.Single()==@"R:\unmounted","Own-store read does not probe a saved offline root");
            using(var directory=NativeDirectory.OpenRoot(outside,false))
            {
                using(var metadata=directory.OpenFile("profiles.json"))
                {
                    Check((NativeMetadata.Attributes(metadata)&FileAttributes.Directory)==0,"Ordinary scan opens still support metadata queries");
                    bool denied=false;try{using var stream=new FileStream(metadata,FileAccess.Read);stream.ReadByte();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){denied=true;}
                    Check(denied,"Ordinary scan opens still cannot read file contents");
                }
                using var data=directory.OpenFileForRead("profiles.json");using var input=new FileStream(data,FileAccess.Read);
                Check(input.ReadByte()=='{',"Only the explicit owned-store read requests file data access");
            }
            byte[] original=File.ReadAllBytes(store);
            string junction=Path.Combine(fixtures,"linked-parent");
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-Command");start.ArgumentList.Add("$ErrorActionPreference='Stop';New-Item -ItemType Junction -Path '"+junction.Replace("'","''")+"' -Target '"+outside.Replace("'","''")+"'|Out-Null");
            using(var process=Process.Start(start)!){string error=process.StandardError.ReadToEnd();process.StandardOutput.ReadToEnd();process.WaitForExit();if(process.ExitCode!=0)throw new Exception(error);}
            var linked=ScanProfiles.Load(Path.Combine(junction,"profiles.json"));
            Check(linked.Profiles.Count==0 && linked.LoadWarning!=null,"Preference parent junction is rejected before reading the linked payload");
            linked.Save("Blocked write",[@"C:\"],[]);linked.SetRemember(true);
            Check(linked.Persist()!=null && File.ReadAllBytes(store).SequenceEqual(original),"Preference writes cannot replace a payload reached through a parent junction");
            var directoryPayload=ScanProfiles.Load(junction);
            Check(directoryPayload.LoadWarning!=null && directoryPayload.Profiles.Count==0,"A directory/reparse item cannot masquerade as a profile JSON file");
            var file=new FileInfo(store);string acl=file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            try
            {
                var denied=file.GetAccessControl();denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.ReadData,AccessControlType.Deny));file.SetAccessControl(denied);
                var locked=ScanProfiles.Load(store);
                Check(locked.LoadWarning!=null && locked.Profiles.Count==0,"Read-data denial yields a visible empty-session warning without a fallback read");
                using var directory=NativeDirectory.OpenRoot(outside,false);using var metadata=directory.OpenFile("profiles.json");
                Check((NativeMetadata.Attributes(metadata)&FileAttributes.Directory)==0,"Read-data ACL denial does not prevent metadata-only scanning");
            }
            finally{var restore=new FileSecurity();restore.SetSecurityDescriptorSddlForm(acl,AccessControlSections.Access);file.SetAccessControl(restore);}
            Check(ScanProfiles.Load(store).Profiles.Count==1 && File.ReadAllBytes(store).SequenceEqual(original),"Restoring fixture permissions restores access with unchanged contents");
            log.Add($"ALL CHECKS PASSED ({log.Count})");return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);return 1;}
        finally{File.WriteAllLines(Path.Combine(output,"results.txt"),log);foreach(string line in log)Console.WriteLine(line);}
    }
}
