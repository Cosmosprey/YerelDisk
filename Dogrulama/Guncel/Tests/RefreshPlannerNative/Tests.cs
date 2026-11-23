using System.Diagnostics;
using System.Runtime.InteropServices;
using YerelDisk;

static class Tests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateHardLinkW(string alias,string original,nint security);
    static RefreshRoot Identity(string path){using var pin=NativeDirectory.OpenRoot(path,false);var id=NativeMetadata.Identity(pin.Handle);return new(path,id.Volume,id.Id,pin.TryGetVolumeId());}
    static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);string fixture=Path.Combine(output,"own-fixtures"),root=Path.Combine(fixture,"root"),a=Path.Combine(root,"a"),b=Path.Combine(root,"b"),skip=Path.Combine(a,"skip");Directory.CreateDirectory(skip);Directory.CreateDirectory(b);
        try
        {
            File.WriteAllBytes(Path.Combine(a,"one.bin"),new byte[11]);File.WriteAllBytes(Path.Combine(b,"three.bin"),new byte[33]);File.WriteAllBytes(Path.Combine(skip,"canary.bin"),new byte[999]);
            var limits=new RefreshMemoryLimits(4<<20,16<<20,1<<20);var previous=VerifiedScan.Scan([root],null,default,2,1,[skip],4<<20);var selected=previous.Root.Children.Single(f=>f.Path==a);var live=new[]{Identity(root)};long bytes=previous.Root.LogicalBytes;
            var initial=RefreshPlanner.Evaluate(previous,selected,live,limits);
            Check(initial.Action==RefreshAction.MeasureSelected&&initial.SelectedExclusions.Single()==skip,"Native healthy subtree gets a selected-scan grant and inherited exclusions");
            var fresh=VerifiedScan.Scan([a],null,default,2,1,initial.SelectedExclusions,initial.SelectedIndexBudgetBytes);
            Check(RefreshPlanner.Evaluate(previous,selected,[Identity(root)],limits,fresh.RefreshIndex).Action==RefreshAction.MergeSelected,"Native fresh subtree is checked again before merge eligibility");
            File.WriteAllBytes(Path.Combine(a,"one.bin"),new byte[22]);fresh=VerifiedScan.Scan([a],null,default,2,1,initial.SelectedExclusions,initial.SelectedIndexBudgetBytes);
            Check(RefreshPlanner.Evaluate(previous,selected,[Identity(root)],limits,fresh.RefreshIndex).Action==RefreshAction.MergeSelected,"Size changes within a stable selected directory remain eligible");
            string alias=Path.Combine(a,"new-alias.bin");Check(CreateHardLinkW(alias,Path.Combine(b,"three.bin"),0),"Create real new alias crossing the selected boundary");
            fresh=VerifiedScan.Scan([a],null,default,2,1,initial.SelectedExclusions,initial.SelectedIndexBudgetBytes);
            var widened=RefreshPlanner.Evaluate(previous,selected,[Identity(root)],limits,fresh.RefreshIndex);
            Check(widened.Action==RefreshAction.MeasureScope&&widened.Reason==RefreshReason.SharedOutside,"Alias created after the initial decision automatically requests original full scope");
            Check(widened.ScopeRoots.Single()==root&&widened.Exclusions.Single()==skip&&previous.Root.LogicalBytes==bytes,"Fallback keeps original scope exclusions and the previous result unchanged");
            var shared=VerifiedScan.Scan([root],null,default,2,1,[skip],4<<20);var sharedSelected=shared.Root.Children.Single(f=>f.Path==a);
            Check(RefreshPlanner.Evaluate(shared,sharedSelected,[Identity(root)],limits).Reason==RefreshReason.SharedOutside,"Already observed boundary link chooses full scope before new selected I/O");
            File.Delete(alias);
            var basic=VerifiedScan.Scan([root],null,default,2,1,[skip]);Check(RefreshPlanner.Evaluate(basic,basic.Root.Children.Single(f=>f.Path==a),[Identity(root)],limits).Action==RefreshAction.MeasureScope,"Normal UI-style scan without cache chooses full measurement automatically");
            var multi=VerifiedScan.Scan([a,b],null,default,2,1,[skip],4<<20);var ma=multi.Root.Children.Single(f=>f.Path==a);var mp=RefreshPlanner.Evaluate(multi,ma,[Identity(a),Identity(b)],limits);
            Check(mp.Action==RefreshAction.MeasureSelected&&mp.ScopeRoots.SequenceEqual([a,b]),"One real root of a multi-root result can be selected without losing full fallback scope");
            fresh=VerifiedScan.Scan([a],null,default,2,1,mp.SelectedExclusions,mp.SelectedIndexBudgetBytes);
            Check(RefreshPlanner.Evaluate(multi,ma,[Identity(a),Identity(b)],limits,fresh.RefreshIndex).Action==RefreshAction.MergeSelected,"Multi-root native selected measurement passes the second phase");
            string outside=Path.Combine(fixture,"outside");Directory.CreateDirectory(outside);File.WriteAllBytes(Path.Combine(outside,"never-follow.bin"),new byte[888]);string link=Path.Combine(root,"link");
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var p in new[]{"-NoProfile","-NonInteractive","-Command","$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+outside.Replace("'","''")+"'|Out-Null"})start.ArgumentList.Add(p);
            using(var proc=Process.Start(start)!){var stdout=proc.StandardOutput.ReadToEndAsync();var stderr=proc.StandardError.ReadToEndAsync();proc.WaitForExit();if(proc.ExitCode!=0)throw new Exception(stdout.Result+stderr.Result);}
            var partial=VerifiedScan.Scan([root],null,default,2,1,[skip],4<<20);var pa=partial.Root.Children.Single(f=>f.Path==a);
            Check(partial.HasUnverified&&RefreshPlanner.Evaluate(partial,pa,[Identity(root)],limits).Action==RefreshAction.MeasureSelected,"Unrelated unknown junction does not unnecessarily disable a healthy selected area");
            var unknown=partial.Root.Children.Single(f=>f.Path==link);var unknownDecision=RefreshPlanner.Evaluate(partial,unknown,[Identity(root)],limits);
            Check(unknownDecision.Action!=RefreshAction.MeasureSelected&&new FileInfo(Path.Combine(outside,"never-follow.bin")).Length==888,"Unknown junction selection never authorizes following its target");
            Directory.Move(root,Path.Combine(fixture,"old-root"));Directory.CreateDirectory(root);
            Check(RefreshPlanner.Evaluate(previous,selected,[Identity(root)],limits).Action==RefreshAction.KeepPrevious,"Real root replacement retains previous results instead of scanning the new target");
            log.Add($"ALL CHECKS PASSED ({log.Count})");return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);return 1;}
        finally{File.WriteAllLines(Path.Combine(output,"results.txt"),log);foreach(var line in log)Console.WriteLine(line);}
    }
}
