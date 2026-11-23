using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using YerelDisk;
using YerelDisk.Presentation;

static class Tests
{
    static readonly List<string> log=[];
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateHardLinkW(string alias,string original,nint security);
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static async Task<int> Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);string fixtures=Path.Combine(output,"own-fixtures"),root=Path.Combine(fixtures,"tree"),a=Path.Combine(root,"selected"),b=Path.Combine(root,"stable"),skip=Path.Combine(root,"skip");
        Directory.CreateDirectory(a);Directory.CreateDirectory(b);Directory.CreateDirectory(skip);
        try
        {
            string shared=Path.Combine(a,"DATA-NAME-INDEX-ONLY.vhdx");File.WriteAllBytes(shared,new byte[8193]);File.WriteAllBytes(shared+":extra",new byte[3]);
            Check(CreateHardLinkW(Path.Combine(b,"alias.vhdx"),shared,0),"Create real cross-subtree hard link");
            File.WriteAllBytes(Path.Combine(a,"tiny.bin"),[1]);File.WriteAllBytes(Path.Combine(b,"plain.bin"),new byte[99]);File.WriteAllBytes(Path.Combine(b,"empty.gguf"),[]);
            File.WriteAllBytes(Path.Combine(skip,"excluded-canary.pst"),new byte[12345]);File.WriteAllBytes(root+":folder-extra",new byte[7]);
            var baseline=VerifiedScan.Scan([root],null,default,1,1,[skip]);var indexed=VerifiedScan.Scan([root],null,default,1,1,[skip],4*1024*1024);
            Check(baseline.RefreshIndex==null&&baseline.RefreshDirectoryCandidates==null&&baseline.FileCandidates==null,"Default scan leaves no index or new transient directory pool");
            var index=indexed.RefreshIndex!;
            Check(index.Captured&&index.FileCount==5&&index.DirectoryCount==3,"Requested native index captures every file beyond the one-file list");
            Check(Signature(baseline)==Signature(indexed),"Index opt-in preserves logical physical file-count and top-K results");
            Check(indexed.FileCandidates==null&&indexed.RefreshDirectoryCandidates==null,"Opt-in finalization also releases transient ScanNode-bound enumerables");
            Check(index.Files.Count(f=>f.HardLinked)==2&&index.Files.Where(f=>f.HardLinked).Select(f=>(f.Volume,f.Id)).Distinct().Count()==1,"Both hard-link observations preserve the same identity");
            Check(index.Files.Where(f=>f.HardLinked).All(f=>f.ExtraStreams==1&&f.ExtraStreamBytes==3),"File ADS survives independent observation capture");
            var rootMetadata=index.Directories.Single(d=>d.Data.Path==root).Data;
            Check(rootMetadata.OwnStreams.ExtraStreams==1&&rootMetadata.OwnStreams.ExtraStreamBytes==7,"Directory ADS metadata retained separately for later aggregation");
            Check(index.Roots.Single().Id==indexed.Root.ObservedId&&index.Roots.Single().VolumeId==indexed.RootVolumeIds![root],"Index root identity matches the pinned completed scan");
            Check(index.Exclusions.Single()==skip&&!index.Files.Any(f=>f.Name.Contains("excluded-canary"))&&!index.Directories.Any(d=>d.Data.Path==skip),"Excluded subtree contributes scope policy but no cached entries");
            Check(new FileInfo(Path.Combine(skip,"excluded-canary.pst")).Length==12345,"Excluded canary content remains untouched");
            var limited=VerifiedScan.Scan([root],null,default,1,1,[skip],4096);
            Check(limited.RefreshIndex is{State:RefreshIndexState.BudgetExceeded,FileCount:0,DirectoryCount:0}&&Signature(limited)==Signature(baseline),"Exhausted native cache budget keeps scan totals and exposes only a full-scan fallback");
            var service=new NativeScanService();var requested=await service.ScanAsync(new ScanRequest([root],1,1,[skip],4*1024*1024),null,default);
            Check(requested.Scan.RefreshIndex?.Captured==true,"Application service passes an explicit budget to the engine");
            var normal=await service.ScanAsync(new ScanRequest([root],1,1,[skip]),null,default);
            Check(normal.Scan.RefreshIndex==null,"Application default still performs no index retention");
            string measurement=Path.Combine(fixtures,"reference.json");FolderMeasurements.Capture(indexed,DateTimeOffset.UtcNow).Write(measurement);
            using(var json=JsonDocument.Parse(File.ReadAllText(measurement)))
                Check(!File.ReadAllText(measurement).Contains("DATA-NAME-INDEX-ONLY")&&!json.RootElement.TryGetProperty("RefreshIndex",out _),"Folder reference format never serializes the new per-file index");
            var multiBase=VerifiedScan.Scan([a,b],null,default,1,1);var multi=VerifiedScan.Scan([a,b],null,default,1,1,refreshIndexBudgetBytes:4*1024*1024);
            Check(multi.RefreshIndex is{Captured:true,FileCount:5,DirectoryCount:2}&&multi.RefreshIndex.Roots.Count()==2,"Multi-root finalization retains one complete root-ordered index");
            Check(Signature(multiBase)==Signature(multi)&&multi.HardLinkAliases==1,"Cross-root physical dedup remains identical with retention enabled");
            string outside=Path.Combine(fixtures,"outside");Directory.CreateDirectory(outside);File.WriteAllBytes(Path.Combine(outside,"not-read.pst"),new byte[8888]);string junction=Path.Combine(root,"junction");
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string part in new[]{"-NoProfile","-NonInteractive","-Command","$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+junction.Replace("'","''")+"' -Target '"+outside.Replace("'","''")+"' | Out-Null"})start.ArgumentList.Add(part);
            using(var process=Process.Start(start)!){var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();process.WaitForExit();if(process.ExitCode!=0)throw new Exception(stdout.Result+stderr.Result);}
            var guarded=VerifiedScan.Scan([root],null,default,1,1,[skip],4*1024*1024);
            log.Add($"DIAG junction: cache={guarded.RefreshIndex?.State}, uncertain={guarded.RefreshIndex?.HasUncertainty}, files={guarded.RefreshIndex?.FileCount}, folders={guarded.Folders}, issues={guarded.IssueCount}, partial={guarded.HasUnverified}; children="+string.Join(",",guarded.Root.Children.Select(f=>f.Name+":"+f.Measurement)));
            Check(guarded.RefreshIndex is{Captured:true,HasUncertainty:true,FileCount:5}&&!guarded.RefreshIndex.Files.Any(f=>f.Name=="not-read.pst"),"Skipped junction remains explicit uncertainty without caching its target");
            Check(guarded.RefreshIndex!.Directories.Any(d=>d.Data.Path==junction&&d.Data.Measurement==MeasurementState.Unknown&&!d.Data.Enumerated),"Skipped junction row remains an unenumerated unknown directory instead of disappearing from the index");
            using var cancel=new CancellationTokenSource();cancel.Cancel();var stopped=VerifiedScan.Scan([root],null,cancel.Token,1,1,[skip],4*1024*1024);
            Check(stopped.Cancelled&&stopped.RefreshIndex is{State:RefreshIndexState.Cancelled,FileCount:0},"Cancelled scan cannot produce a usable refresh index");
            log.Add($"ALL CHECKS PASSED ({log.Count(s=>s.StartsWith("PASS "))})");return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);return 1;}
        finally{File.WriteAllLines(Path.Combine(output,"results.txt"),log);foreach(var line in log)Console.WriteLine(line);}
    }
    static string Signature(ScanResult result)=>JsonSerializer.Serialize(new{result.Root.LogicalBytes,result.Root.AllocatedBytes,result.Root.FileCount,result.HardLinkAliases,result.UnknownAllocation,
        result.IssueCount,result.ScopeChanged,result.Cancelled,Files=result.Files.Select(f=>new{f.Path,f.Size,f.Allocated,f.HardLinked,f.ExtraStreams,f.ExtraStreamBytes,f.Unverified,f.Hint})});
}
