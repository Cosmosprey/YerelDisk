using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using YerelDisk;

var log=new List<string>();string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
string fixtures=Path.Combine(output,"own-fixtures");Directory.CreateDirectory(fixtures);
if(args.Length>1 && args[1]=="--worker")return RunWorker(args);
try
{
    string store=Path.Combine(fixtures,"profiles.json");var library=new ScanProfiles(store);
    var profile=library.Save(" C genel ",[@"C:\",@"C:\Users"],[@"C:\Windows",@"C:\Windows\Temp"]);
    Check(profile.Name=="C genel" && profile.Roots.SequenceEqual([@"C:\"]) && profile.Exclusions.SequenceEqual([@"C:\Windows"]),"Profile names and lexical scope canonicalize without filesystem probes");
    Check(!library.Remember && library.Persist()==null && !File.Exists(store),"Session profiles create no preference file");
    string[] input=[@"R:\disconnected"];var immutable=library.Save("Offline",input,[]);input[0]=@"C:\changed";
    Check(immutable.Roots.SequenceEqual([@"R:\disconnected"]),"Caller mutation cannot change a saved scope");
    bool readOnly=false;try{((IList<string>)immutable.Roots)[0]=@"C:\changed";}catch(NotSupportedException){readOnly=true;}
    Check(readOnly,"Published profile paths are read-only");
    Reject(()=>library.Save("c GENEL",[@"C:\"],[]),"Case-insensitive duplicate names are rejected");
    Reject(()=>library.Save("",[@"C:\"],[]),"Empty profile names are rejected");
    Reject(()=>library.Save("bad\nname",[@"C:\"],[]),"Control characters in names are rejected");
    Reject(()=>library.Save("long name"+new string('x',80),[@"C:\"],[]),"Oversized names are rejected");
    foreach(string path in new[]{@"relative\root",@"\\server\share",@"C:\wild*",@"C:\bad.\leaf","C:\\bad\nname",@"C:\bad|name"})
        Reject(()=>library.Save("bad path",[path],[]),"Invalid local profile root rejected: "+path.Replace("\n","\\n"));
    Reject(()=>library.Save("no roots",[],[]),"Empty scan scope is rejected");
    Reject(()=>library.Save("outside",[@"C:\inside"],[@"D:\outside"]),"Excluded paths must belong to the selected roots");
    Reject(()=>library.Save("exclude root",[@"C:\inside"],[@"C:\inside"]),"A profile cannot exclude its entire selected root");
    Reject(()=>library.Save("too many roots",Enumerable.Range(0,65).Select(i=>@"C:\r"+i),[]),"Root count is bounded before normalization");
    Reject(()=>library.Save("too many exclusions",[@"C:\"],Enumerable.Range(0,129).Select(i=>@"C:\x"+i)),"Exclusion count is bounded");
    var updated=library.Save("C proje",[@"C:\projects"],[],profile.Id);
    Check(updated.Id==profile.Id && library.Profiles.Count==2 && updated.Roots.Single()==@"C:\projects","Updating preserves ID and replaces scope instead of appending");
    Reject(()=>library.Save("missing",[@"C:\"],[],Guid.NewGuid()),"Updating an unknown profile fails");
    library.Remove(immutable.Id);
    Check(library.Profiles.Count==1 && !File.Exists(store),"Removing a session profile only changes the in-memory library");
    library.SetRemember(true);Check(library.Persist()==null && File.Exists(store),"Explicit remember writes the local profile file");
    var reopened=ScanProfiles.Load(store);
    Check(reopened.Remember && reopened.LoadWarning==null && reopened.Profiles.Single().Name=="C proje","Profiles round-trip with explicit consent");
    Check(reopened.Profiles.Single().Roots.SequenceEqual([@"C:\projects"]),"Loading a missing root restores its text without opening that root");

    var first=ScanProfiles.Load(store);var second=ScanProfiles.Load(store);
    var firstNew=first.Save("First",[@"D:\one"],[]);Check(first.Persist()==null,"First window saves an independent addition");
    second.Save("Second",[@"D:\two"],[]);Check(second.Persist()==null && second.Profiles.Count==3,"Second window merges additions made by the first");
    first.Remove(profile.Id);Check(first.Persist()==null && ScanProfiles.Load(store).Profiles.All(p=>p.Id!=profile.Id),"An ID deletion preserves the other window's independent profile");
    second.Save("Second updated",[@"D:\two"],[],second.Profiles.Single(p=>p.Name=="Second").Id);Check(second.Persist()==null && ScanProfiles.Load(store).Profiles.All(p=>p.Id!=profile.Id),"A stale window cannot resurrect a profile it did not edit");
    var conflictA=ScanProfiles.Load(store);var conflictB=ScanProfiles.Load(store);
    conflictA.Save("Same name",[@"C:\a"],[]);Check(conflictA.Persist()==null,"Concurrent name collision fixture starts with a valid save");
    var colliding=conflictB.Save("SAME NAME",[@"C:\b"],[]);byte[] beforeCollision=File.ReadAllBytes(store);
    Check(conflictB.Persist()!=null && File.ReadAllBytes(store).SequenceEqual(beforeCollision),"Concurrent duplicate names do not overwrite the persisted list");
    conflictB.Save("Different name",[@"C:\b"],[],colliding.Id);Check(conflictB.Persist()==null && ScanProfiles.Load(store).Profiles.Count==4,"Renaming the unsaved collision resolves it without dropping either profile");
    var stale=ScanProfiles.Load(store);var optout=ScanProfiles.Load(store);int kept=optout.Profiles.Count;optout.SetRemember(false);
    Check(optout.Persist()==null && !File.Exists(store) && optout.Profiles.Count==kept,"Opt-out removes only the preference file and preserves session profiles");
    stale.Save("Stale addition",[@"C:\stale"],[]);Check(stale.Persist()!=null && !stale.Remember && !File.Exists(store),"A stale remembered window cannot recreate an opted-out store");
    optout.SetRemember(true);Check(optout.Persist()==null && ScanProfiles.Load(store).Profiles.Count==kept,"Explicit opt-in can remember the preserved session again");

    var limited=new ScanProfiles();for(int i=0;i<ScanProfiles.Limit;i++)limited.Save("profile-"+i,[@"C:\"],[]);
    Reject(()=>limited.Save("overflow",[@"C:\"],[]),"Profile library count is bounded");
    Check(limited.Save("renamed",[@"C:\new"],[],limited.Profiles[0].Id).Name=="renamed","Updating an existing profile remains possible at the limit");
    string broken=Path.Combine(fixtures,"broken.json");File.WriteAllText(broken,"{bad");byte[] bad=File.ReadAllBytes(broken);var corrupted=ScanProfiles.Load(broken);
    Check(!corrupted.Remember && corrupted.LoadWarning!=null && File.ReadAllBytes(broken).SequenceEqual(bad),"Malformed preference data produces a warning without rewriting the file");
    corrupted.Save("Session stays usable",[@"C:\"],[]);corrupted.SetRemember(true);
    Check(corrupted.Persist()!=null && File.ReadAllBytes(broken).SequenceEqual(bad),"Explicit save cannot silently overwrite an unreadable existing store");
    string oversized=Path.Combine(fixtures,"oversized.json");File.WriteAllBytes(oversized,new byte[ScanProfiles.MaxStoreBytes+1]);
    Check(ScanProfiles.Load(oversized).LoadWarning!=null,"Oversized saved data is rejected before JSON parsing");
    string invalid=Path.Combine(fixtures,"invalid.json");File.WriteAllText(invalid,JsonSerializer.Serialize(new{Version=1,Consent=Guid.NewGuid(),Profiles=new[]{new{Id=Guid.NewGuid(),Name="Remote",Roots=new[]{@"\\host\share"},Exclusions=Array.Empty<string>()}}}));
    Check(ScanProfiles.Load(invalid).LoadWarning!=null,"Untrusted serialized paths cannot become an active profile");
    var entry=new{Id=Guid.NewGuid(),Name="One",Roots=new[]{@"C:\"},Exclusions=Array.Empty<string>()};
    object[] invalidStores=[new{Version=2,Consent=Guid.NewGuid(),Profiles=new[]{entry}},new{Version=1,Consent=Guid.Empty,Profiles=new[]{entry}},
        new{Version=1,Consent=Guid.NewGuid(),Profiles=new[]{entry,entry with{Name="Two"}}},new{Version=1,Consent=Guid.NewGuid(),Profiles=new[]{entry,entry with{Id=Guid.NewGuid(),Name="ONE"}}}];
    foreach(var envelope in invalidStores){File.WriteAllText(invalid,JsonSerializer.Serialize(envelope));Check(ScanProfiles.Load(invalid).LoadWarning!=null,"Invalid schema, consent or duplicate serialized profile is rejected");}
    var locked=ScanProfiles.Load(store);locked.Save("Locked update",[@"C:\locked"],[]);byte[] original=File.ReadAllBytes(store);
    using(var handle=new FileStream(store,FileMode.Open,FileAccess.Read,FileShare.Read))
        Check(locked.Persist()!=null && File.ReadAllBytes(store).SequenceEqual(original),"Locked replacement keeps the old preference file intact");
    Check(!Directory.EnumerateFiles(fixtures,".yereldisk-profiller-*.tmp").Any(),"Failed replacement leaves no temporary profile payload");
    Check(locked.Persist()==null,"A failed write retains pending edits for retry after unlocking");
    var tooLarge=ScanProfiles.Load(store);
    while(tooLarge.Profiles.Count>0)tooLarge.Remove(tooLarge.Profiles[0].Id);
    for(int p=0;p<20;p++)tooLarge.Save("large-"+p,Enumerable.Range(0,64).Select(i=>@"C:\"+new string('a',900)+"-"+p+"-"+i),[]);
    byte[] beforeLimit=File.ReadAllBytes(store);
    Check(tooLarge.Persist()!=null && File.ReadAllBytes(store).SequenceEqual(beforeLimit),"Serialization budget preserves the previous store without a partial overwrite");
    Check(!Directory.EnumerateFiles(fixtures,".yereldisk-profiller-*.tmp").Any(),"Serialization budget failure occurs before creating a temporary file");
    Check(firstNew.Roots.Single()==@"D:\one","Later library merges never mutate an earlier immutable profile object");

    var seeded=Seed("same-id");var editA=ScanProfiles.Load(seeded.Path);var editB=ScanProfiles.Load(seeded.Path);
    editA.Save("First edit",[@"C:\First"],[],seeded.Id);Check(editA.Persist()==null,"Concurrent first edit commits normally");
    editB.Save("Second edit",[@"C:\Second"],[],seeded.Id);byte[] committed=File.ReadAllBytes(seeded.Path);
    Check(editB.Persist()!=null && editB.Conflict is{Local.Name:"Second edit",Saved.Name:"First edit"} && File.ReadAllBytes(seeded.Path).SequenceEqual(committed),"Same-ID stale update warns and preserves the other window's exact bytes");
    editB.Save("Independent pending",[@"D:\Pending"],[]);
    Check(editB.Persist()!=null && ScanProfiles.Load(seeded.Path).Profiles.Count==1,"A conflicting transaction does not partially publish independent pending additions");
    var pendingConflict=editB.Conflict;
    Reject(()=>editB.CopyConflict("First edit"),"Copy resolution validates duplicate names before discarding the original draft");
    Check(ReferenceEquals(editB.Conflict,pendingConflict) && editB.Profiles.Any(p=>p.Name=="Second edit"),"Invalid copy keeps the conflict and draft intact");
    var copy=editB.CopyConflict(editB.SuggestedCopyName());Check(copy.Id!=seeded.Id && copy.Roots.Single()==@"C:\Second","Copy resolution creates a distinct ID from the preserved local scope");
    Check(editB.Persist()==null && ScanProfiles.Load(seeded.Path).Profiles.Single(p=>p.Id==seeded.Id).Roots.Single()==@"C:\First"
        && ScanProfiles.Load(seeded.Path).Profiles.Any(p=>p.Id==copy.Id) && ScanProfiles.Load(seeded.Path).Profiles.Any(p=>p.Name=="Independent pending"),"Copy resolution preserves both versions and other pending work");
    editB.Save("Third edit",[@"C:\Third"],[],seeded.Id);Check(editB.Persist()==null,"Successful persistence refreshes the baseline for subsequent edits");

    seeded=Seed("delete-update");var deleting=ScanProfiles.Load(seeded.Path);var updating=ScanProfiles.Load(seeded.Path);
    deleting.Remove(seeded.Id);Check(deleting.Persist()==null,"Delete fixture commits before the stale update");
    updating.Save("Stale edit",[@"C:\Stale"],[],seeded.Id);committed=File.ReadAllBytes(seeded.Path);
    Check(updating.Persist()!=null && updating.Conflict is{Saved:null,Local.Name:"Stale edit"} && File.ReadAllBytes(seeded.Path).SequenceEqual(committed),"Stale update cannot silently recreate a deleted ID");
    var restoredCopy=updating.CopyConflict(updating.SuggestedCopyName());
    Check(updating.Persist()==null && ScanProfiles.Load(seeded.Path).Profiles.Single().Id==restoredCopy.Id && restoredCopy.Id!=seeded.Id,"Explicit copy can recreate desired scope under a fresh ID");

    seeded=Seed("use-saved");editA=ScanProfiles.Load(seeded.Path);editB=ScanProfiles.Load(seeded.Path);
    editA.Save("Remote",[@"C:\Remote"],[],seeded.Id);editA.Persist();editB.Save("Local",[@"C:\Local"],[],seeded.Id);editB.Save("Keep pending",[@"D:\Keep"],[]);editB.Persist();
    editA.Save("Newest remote",[@"C:\Newest"],[],seeded.Id);editA.Persist();
    editB.UseSavedConflict();Check(editB.Persist()==null && editB.Profiles.Single(p=>p.Id==seeded.Id).Name=="Newest remote" && editB.Profiles.Any(p=>p.Name=="Keep pending"),"Using disk state re-reads newer data and preserves unrelated pending edits");
    Reject(editB.UseSavedConflict,"Resolution without a conflict is rejected");

    seeded=Seed("update-delete");editA=ScanProfiles.Load(seeded.Path);editB=ScanProfiles.Load(seeded.Path);
    editA.Save("Remote update",[@"C:\Remote"],[],seeded.Id);editA.Persist();editB.Remove(seeded.Id);committed=File.ReadAllBytes(seeded.Path);
    Check(editB.Persist()!=null && editB.Conflict is{Local:null,Saved.Name:"Remote update"} && File.ReadAllBytes(seeded.Path).SequenceEqual(committed),"Stale removal cannot erase another window's newer edit");
    Reject(()=>editB.CopyConflict("not a draft"),"A deletion conflict cannot be copied as an update");
    editB.UseSavedConflict();editB.Persist();editB.Remove(seeded.Id);
    Check(editB.Persist()==null && ScanProfiles.Load(seeded.Path).Profiles.Count==0,"An explicitly reviewed newer profile can be deleted afterwards");

    seeded=Seed("same-intent");editA=ScanProfiles.Load(seeded.Path);editB=ScanProfiles.Load(seeded.Path);
    editA.Save("Identical",[@"C:\Same"],[],seeded.Id);editB.Save("Identical",[@"C:\Same"],[],seeded.Id);
    Check(editA.Persist()==null && editB.Persist()==null && editB.Conflict==null,"Identical concurrent updates converge without a false conflict");
    editA.Remove(seeded.Id);editB.Remove(seeded.Id);
    Check(editA.Persist()==null && editB.Persist()==null,"Repeated concurrent deletion is idempotent");

    seeded=Seed("exclusion-change");editA=ScanProfiles.Load(seeded.Path);editB=ScanProfiles.Load(seeded.Path);
    editA.Save("Original",[@"C:\Original"],[@"C:\Original\A"],seeded.Id);editA.Persist();
    editB.Save("Original",[@"C:\Original"],[@"C:\Original\B"],seeded.Id);
    Check(editB.Persist()!=null && editB.Conflict!=null,"Baseline comparison includes exclusions even when name and roots match");

    seeded=Seed("opt-in-existing");editA=ScanProfiles.Load(seeded.Path);editA.SetRemember(false);editA.Persist();
    var newer=new ScanProfiles(seeded.Path);newer.Save("New consent data",[@"D:\New"],[]);newer.SetRemember(true);newer.Persist();
    editA.SetRemember(true);Check(editA.Persist()==null && editA.Profiles.All(p=>p.Id!=seeded.Id) && editA.Profiles.Single().Name=="New consent data","Re-enabling against an existing new store cannot import unchanged deleted baseline IDs");
    editA.SetRemember(false);editA.Persist();var memoryOnly=editA.Profiles.Single();editA.Save("Memory change",[@"D:\Memory"],[],memoryOnly.Id);editA.SetRemember(true);
    Check(editA.Persist()==null && ScanProfiles.Load(seeded.Path).Profiles.Single().Name=="Memory change","Explicit fresh consent can remember an edited session after opt-out");

    seeded=Seed("copy-limit");var filler=ScanProfiles.Load(seeded.Path);for(int i=1;i<20;i++)filler.Save("Other "+i,[@"D:\Other"],[]);filler.Persist();
    editA=ScanProfiles.Load(seeded.Path);editB=ScanProfiles.Load(seeded.Path);editA.Save("Remote",[@"C:\Remote"],[],seeded.Id);editA.Persist();editB.Save("Local",[@"C:\Local"],[],seeded.Id);editB.Persist();pendingConflict=editB.Conflict;
    Reject(()=>editB.CopyConflict("Copy"),"Copy resolution respects the profile limit before mutating local drafts");
    Check(ReferenceEquals(editB.Conflict,pendingConflict) && editB.Profiles.Count==20 && editB.Profiles.Any(p=>p.Name=="Local"),"Full-library copy failure preserves the original pending edit");

    RunProcesses();
    log.Add($"ALL CHECKS PASSED ({log.Count})");return 0;
}
catch(Exception ex){log.Add("FAIL "+ex);return 1;}
finally{File.WriteAllLines(Path.Combine(output,"results.txt"),log);foreach(string line in log)Console.WriteLine(line);}
void Check(bool condition,string label){if(!condition)throw new Exception(label);log.Add("PASS "+label);}
void Reject(Action action,string label){try{action();}catch(ArgumentException){Check(true,label);return;}throw new Exception("Expected rejection: "+label);}
(string Path,Guid Id) Seed(string name)
{
    string path=Path.Combine(fixtures,"conflict-"+name+".json");var value=new ScanProfiles(path);var profile=value.Save("Original",[@"C:\Original"],[]);value.SetRemember(true);if(value.Persist()!=null)throw new Exception("Seed failed");return(path,profile.Id);
}
int RunWorker(string[] values)
{
    try
    {
        string path=values[2],role=values[3],prefix=values[4];var value=ScanProfiles.Load(path);var p=value.Profiles.Single();value.Save("Process "+role,[@"C:\Process-"+role],[],p.Id);
        using var ready=EventWaitHandle.OpenExisting(prefix+"-ready-"+role);using var go=EventWaitHandle.OpenExisting(prefix+"-go-"+role);ready.Set();if(!go.WaitOne(20000))throw new TimeoutException("Worker gate");
        string? warning=value.Persist();bool ok=role=="A"?warning==null:warning!=null && value.Conflict!=null;
        File.WriteAllText(Path.Combine(values[0],"worker.txt"),role+" expected="+ok+" warning="+warning);return ok?0:2;
    }
    catch(Exception ex){File.WriteAllText(Path.Combine(values[0],"worker.txt"),ex.ToString());return 3;}
}
void RunProcesses()
{
    var seed=Seed("processes");string prefix="YerelDisk-ProfileConflict-"+Guid.NewGuid().ToString("N");
    using var readyA=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"-ready-A");using var readyB=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"-ready-B");
    using var goA=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"-go-A");using var goB=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"-go-B");
    Process Start(string role)
    {
        string folder=Path.Combine(fixtures,"worker-"+role);Directory.CreateDirectory(folder);var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{Assembly.GetExecutingAssembly().Location,folder,"--worker",seed.Path,role,prefix})start.ArgumentList.Add(arg);
        return Process.Start(start)??throw new Exception("Worker start failed");
    }
    using var a=Start("A");using var b=Start("B");var stdoutA=a.StandardOutput.ReadToEndAsync();var errorA=a.StandardError.ReadToEndAsync();var stdoutB=b.StandardOutput.ReadToEndAsync();var errorB=b.StandardError.ReadToEndAsync();
    try
    {
        if(!readyA.WaitOne(20000)||!readyB.WaitOne(20000))throw new TimeoutException("Workers not ready");goA.Set();if(!a.WaitForExit(20000))throw new TimeoutException("A did not finish");goB.Set();if(!b.WaitForExit(20000))throw new TimeoutException("B did not finish");
        Check(a.ExitCode==0 && b.ExitCode==0 && ScanProfiles.Load(seed.Path).Profiles.Single().Name=="Process A","Two real processes sharing a stale baseline preserve the first update and reject the second");
    }
    finally
    {
        if(!a.HasExited)a.Kill(true);if(!b.HasExited)b.Kill(true);
        File.WriteAllText(Path.Combine(fixtures,"worker-output.txt"),stdoutA.GetAwaiter().GetResult()+errorA.GetAwaiter().GetResult()+stdoutB.GetAwaiter().GetResult()+errorB.GetAwaiter().GetResult());
    }
}
