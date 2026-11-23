using System.Diagnostics;
using System.Runtime.InteropServices;
using YerelDisk;
using YerelDisk.Presentation;

internal static class Tests
{
    static readonly List<string> log=[];
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateHardLinkW(string alias,string original,nint security);
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        string fixtures=Path.Combine(output,"own-fixtures"),root=Path.Combine(fixtures,"tree"),a=Path.Combine(root,"a"),b=Path.Combine(root,"b");Directory.CreateDirectory(a);Directory.CreateDirectory(b);
        try
        {
            File.WriteAllBytes(Path.Combine(a,"tiny.bin"),[1]);File.WriteAllBytes(Path.Combine(a,"empty.txt"),[]);File.WriteAllBytes(Path.Combine(b,"video.mp4"),new byte[8192]);
            Check(CreateHardLinkW(Path.Combine(b,"alias.bin"),Path.Combine(a,"tiny.bin"),0),"Create shared physical data with two observed names");
            var single=VerifiedScan.Scan([root],null,default,1,4);var analysis=FilePopulationAnalysis.Create(single,single.Root);
            Check(analysis is{Complete:true,Count:4,Small:3,Empty:1,Bytes:8194} && single.Files.Count==1,"Native snapshot captures every file beyond the retained winner");
            Check(single.FileCandidates==null && single.ObservedEvidence!.Population.Owners.Count==2,"Native finalization releases file snapshots after bounded summaries");
            var multi=VerifiedScan.Scan([a,b],null,default,1,4);var merged=FilePopulationAnalysis.Create(multi,multi.Root);
            Check(merged.Complete && merged.Count==4 && merged.Bytes==8194 && multi.HardLinkAliases==1,"Multi-root names reconcile despite physical hard-link deduplication");
            var excluded=VerifiedScan.Scan([root],null,default,1,4,[b]);var scoped=FilePopulationAnalysis.Create(excluded,excluded.Root);
            Check(scoped.Complete && scoped.Count==2 && scoped.Bytes==1 && scoped.Coverage.Contains("hariç"),"Native exclusions preserve scoped totals without reading the omitted subtree");
            string outside=Path.Combine(fixtures,"outside"),link=Path.Combine(root,"junction");Directory.CreateDirectory(outside);string canary=Path.Combine(outside,"not-scanned.gguf");File.WriteAllBytes(canary,new byte[9999]);
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-Command");start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+outside.Replace("'","''")+"' | Out-Null");
            using(var process=Process.Start(start)!){string error=process.StandardError.ReadToEnd();process.StandardOutput.ReadToEnd();process.WaitForExit();if(process.ExitCode!=0)throw new Exception(error);}
            var junction=VerifiedScan.Scan([root],null,default,1,4);var guarded=FilePopulationAnalysis.Create(junction,junction.Root);
            Check(!guarded.Complete && guarded.Count==4 && guarded.Types.All(r=>r.Extension!=".gguf") && new FileInfo(canary).Length==9999,"Skipped junction target contributes no file population and remains untouched");
            string repair=Path.Combine(fixtures,"repair");Directory.CreateDirectory(repair);File.WriteAllBytes(Path.Combine(repair,"old.bin"),[1]);bool added=false;
            var refreshed=VerifiedScan.Scan([repair],new ProgressAction(p=>{if(!added && p.Phase==ScanPhase.Verification){added=true;File.WriteAllBytes(Path.Combine(repair,"new.gguf"),new byte[8192]);}}),default,1,4);
            var fresh=FilePopulationAnalysis.Create(refreshed,refreshed.Root);
            Check(added && refreshed.RetryCount==1 && fresh.Complete && fresh.Count==2 && fresh.Small==1,"Targeted repair replaces old summaries rather than accumulating prior observations");
            Check(fresh.Types.Single(r=>r.Extension==".gguf").Count==1 && refreshed.FileCandidates==null,"Newly added suffix survives snapshot release and a one-file limit");
            string streams=Path.Combine(fixtures,"streams");Directory.CreateDirectory(streams);File.WriteAllBytes(Path.Combine(streams,"boundary.bin"),new byte[4096]);File.WriteAllBytes(Path.Combine(streams,"boundary.bin:extra"),[1]);
            var ads=VerifiedScan.Scan([streams],null,default,1,1);var withStreams=FilePopulationAnalysis.Create(ads,ads.Root);
            Check(withStreams is{Complete:true,Count:1,Bytes:4097,Small:0},"Small-file threshold uses measured logical size including named data streams");
            using var stop=new CancellationTokenSource();stop.Cancel();var cancelled=VerifiedScan.Scan([root],null,stop.Token,1,1);var stopped=FilePopulationAnalysis.Create(cancelled,cancelled.Root);
            Check(!stopped.Available || !stopped.Complete,"Cancelled native scan never masquerades as a complete empty analysis");
            log.Add($"ALL CHECKS PASSED ({log.Count})");return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);return 1;}
        finally{File.WriteAllLines(Path.Combine(output,"results.txt"),log);foreach(string line in log)Console.WriteLine(line);}
    }
    sealed class ProgressAction(Action<ScanProgress> callback):IProgress<ScanProgress>{public void Report(ScanProgress progress)=>callback(progress);}
}
