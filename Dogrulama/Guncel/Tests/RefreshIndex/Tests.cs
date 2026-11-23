using System.Diagnostics;
using System.Runtime.CompilerServices;
using YerelDisk;

static class Tests
{
    static readonly List<string> log=[];
    static readonly Guid volume=Guid.Parse("1f8e5dcb-680d-4215-9e8c-a2a855d97f6e");
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);log.Add("PASS "+name);}
    static void Reject(Action action,string name){try{action();}catch(ArgumentOutOfRangeException){Check(true,name);return;}throw new Exception("Expected rejection: "+name);}
    static IEnumerable<T> Forbidden<T>(){yield return Unexpected<T>();}
    static T Unexpected<T>()=>throw new Exception("Unrequested input was enumerated");
    static RefreshRoot Root(ScanResult s)=>new(s.Root.Path,7,s.Root.ObservedId,volume);
    static RefreshIndex Cache(ScanResult s,long budget=1024*1024,CancellationToken token=default)=>YerelDisk.RefreshIndex.Capture([s],[Root(s)],s.ExcludedPaths,budget,token)!;
    static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        try
        {
            Check(YerelDisk.RefreshIndex.Capture(Forbidden<ScanResult>(),Forbidden<RefreshRoot>(),Forbidden<string>(),0)==null,"Disabled capture enumerates no source scope or exclusions");
            Check(new ScanRequest([@"C:\synthetic"],1,1).RefreshIndexBudgetBytes==0,"Application request defaults to no retained index");
            Reject(()=>new ScanRequest([@"C:\synthetic"],1,1,refreshIndexBudgetBytes:-1),"Negative request budget rejected before scanning");
            Reject(()=>YerelDisk.RefreshIndex.Capture([],[],[],YerelDisk.RefreshIndex.MaxBudgetBytes+1),"Over-maximum capture budget rejected");
            YerelDisk.RefreshIndex.ValidateBudget(YerelDisk.RefreshIndex.MaxBudgetBytes);Check(true,"Maximum supported budget validates without allocating its capacity");
            var minimal=YerelDisk.RefreshIndex.Capture(Forbidden<ScanResult>(),Forbidden<RefreshRoot>(),Forbidden<string>(),1)!;
            Check(minimal.State==RefreshIndexState.BudgetExceeded&&minimal.Files.Count()==0&&minimal.DirectoryCount==0,"Too-small budget returns a fallback without enumerating or retaining payload");
            var source=Source(130);var cached=Cache(source);
            Check(cached.Captured&&!cached.RequiresFullScan&&cached.FileCount==130&&cached.DirectoryCount==2,"Complete observations survive more than two storage pages");
            Check(cached.MetadataBytes<=cached.BudgetBytes&&cached.MetadataBytes>0,"Conservative retained data charge stays within one requested budget");
            var files=cached.Files.ToArray();Check(files[0].Name.EndsWith("00000000.bin")&&files[^1].Name.EndsWith("00000129.bin"),"File sequence remains exact across page boundaries");
            Check(files[2] is{Id:3,Volume:7,Size:3,Allocated:4096,HardLinked:true,ExtraStreams:1,ExtraStreamBytes:1},"Identity allocation link and ADS metadata are retained together");
            var dirs=cached.Directories.ToArray();Check(dirs[0].FileStart==0&&dirs[0].FileCount==0&&dirs[1].FileStart==0&&dirs[1].FileCount==130&&dirs[1].Number==1,"Owner ranges partition the complete flat file sequence");
            Check(dirs[0].Data.Fingerprint.Count==130&&dirs[1].Data.Id==43&&dirs[1].Data.OwnStreams.StreamsKnown,"Directory identity fingerprint and own metadata survive capture");
            var oldPath=cached.Roots.Single().Path;source.Root.LogicalBytes=999;source.Root.Children[0].ObservedId=999;source.Root.Children.Clear();source.FileCandidates=null;source.RefreshDirectoryCandidates=null;
            Check(cached.Roots.Single().Path==oldPath&&cached.Directories.Last().Data.Id==43&&cached.FileCount==130,"Mutating or releasing source graph does not mutate captured metadata");
            Check(cached.Files is not IList<FileObservation>&&cached.Directories is not IList<RefreshDirectory>,"Published collections do not expose writable arrays");
            source=Source(3);var roots=new[]{Root(source)};string[] excluded=[@"C:\synthetic\skip"];
            cached=YerelDisk.RefreshIndex.Capture([source],roots,excluded,1024*1024)!;roots[0]=new(@"D:\changed",9,9,Guid.NewGuid());excluded[0]=@"D:\changed\skip";
            Check(cached.Roots.Single().Path==source.Root.Path&&cached.Exclusions.Single()==@"C:\synthetic\skip","Caller scope arrays cannot mutate retained scope");
            var exact=Cache(source);long boundary=exact.MetadataBytes;
            Check(Cache(source,boundary).Captured,"A complete index fits its exact conservative charged boundary");
            var shortCache=Cache(source,boundary-1);Check(shortCache.State==RefreshIndexState.BudgetExceeded&&shortCache.FileCount==0&&shortCache.MetadataBytes==0,"One-byte-under budget exposes no truncated cache");
            Check(Cache(Source(0)).Captured&&Cache(Source(0)).FileCount==0,"An observed empty tree is a complete empty index");
            using(var stop=new CancellationTokenSource()){stop.Cancel();var stopped=YerelDisk.RefreshIndex.Capture(Forbidden<ScanResult>(),Forbidden<RefreshRoot>(),Forbidden<string>(),100000,stop.Token)!;Check(stopped.State==RefreshIndexState.Cancelled&&stopped.FileCount==0,"Pre-cancellation prevents input enumeration");}
            source=Source(20);using(var stop=new CancellationTokenSource()){var child=source.Root.Children[0];source.FileCandidates=CancelDuring(child,stop);var stopped=Cache(source,token:stop.Token);Check(stopped.State==RefreshIndexState.Cancelled&&!stopped.Captured&&stopped.FileCount==0,"Mid-capture cancellation discards all payload pages");}
            foreach(string fault in new[]{"cancelled","failed","scope","retry","missing-files","missing-directories","file-count","directory-count","owner-order","bad-name"})
            {
                source=Source(2);switch(fault){case "cancelled":source.Cancelled=true;break;case "failed":source.Failed=true;break;case "scope":source.ScopeChanged=true;break;case "retry":source.NeedsRetry=true;break;case "missing-files":source.FileCandidates=null;break;case "missing-directories":source.RefreshDirectoryCandidates=null;break;case "file-count":source.Root.FileCount++;break;case "directory-count":source.Folders++;break;case "owner-order":source.FileCandidates=[(new FolderEntry(@"C:\other",null),Obs(0))];break;case "bad-name":source.FileCandidates=[(source.Root.Children[0],Obs(0)with{Name=".."})];break;}
                var bad=Cache(source);Check(bad.RequiresFullScan&&bad.FileCount==0&&bad.DirectoryCount==0,"Incomplete or inconsistent metadata produces only fallback: "+fault);
            }
            source=Source(2);var unknown=YerelDisk.RefreshIndex.Capture([source],[Root(source)with{VolumeId=null}],[],1024*1024)!;
            Check(unknown.State==RefreshIndexState.IdentityUnavailable,"Unknown volume identity is never a complete cache");
            source=Source(2);source.IssueCount=1;source.Root.Measurement=MeasurementState.Partial;source.Root.Children[0].Measurement=MeasurementState.Unknown;
            var partial=Cache(source);Check(partial.Captured&&partial.HasUncertainty&&partial.Directories.Last().Data.Measurement==MeasurementState.Unknown,"Captured observations preserve uncertainty without claiming refresh eligibility");
            source=Source(1);source.FileCandidates=[(source.Root.Children[0],Obs(0)with{Id=0,Allocated=null})];
            Check(Cache(source).HasUncertainty,"Unknown per-file identity and allocation preserve uncertainty even without summary flags");
            source=Source(0);source.RefreshDirectoryCandidates=source.RefreshDirectoryCandidates!.Select(p=>(p.Owner,p.Data with{Enumerated=false}));
            Check(Cache(source).HasUncertainty,"Incomplete directory observations preserve uncertainty even without summary flags");
            var one=Source(200,@"C:\one");var two=Source(200,@"D:\two");long oneBudget=Cache(one).MetadataBytes+8;
            Check(Cache(one,oneBudget).Captured&&Cache(two,oneBudget).Captured,"Each individual root fits the test budget");
            var joint=YerelDisk.RefreshIndex.Capture([one,two],[Root(one),Root(two)],[],oneBudget)!;
            Check(joint.State==RefreshIndexState.BudgetExceeded&&joint.FileCount==0,"Multiple roots share one global budget instead of multiplying it");
            source=Source(0);source.RefreshDirectoryCandidates=source.RefreshDirectoryCandidates!.Select(p=>(p.Owner,p.Data with{OwnStreams=p.Data.OwnStreams with{StreamError=new string('e',100000)}}));
            Check(Cache(source,64000).State==RefreshIndexState.BudgetExceeded,"Directory stream error text is charged to the budget");
            var consumed=new Counter();source=Source(500000,counter:consumed);var limited=Cache(source,1024*1024);
            Check(limited.RequiresFullScan&&consumed.Count<500000&&limited.FileCount==0,"Budget exhaustion stops capture early instead of building the whole pool");
            Cache(Source(100));long start=GC.GetAllocatedBytesForCurrentThread();var timer=Stopwatch.StartNew();var large=Cache(Source(100000),32L*1024*1024);timer.Stop();long allocated=GC.GetAllocatedBytesForCurrentThread()-start;
            Check(large.Captured&&large.FileCount==100000&&allocated<=large.MetadataBytes+1024*1024,"Conservative charge bounds allocations in 100k unique-name capture without grouping tables");
            log.Add($"MEASURE 100k synthetic capture: {timer.ElapsedMilliseconds} ms, allocated={allocated}, charged={large.MetadataBytes}; metadata only, no disk I/O");
            var residue=DiscardedPayload();Collect();long before=GC.GetTotalMemory(true);var discarded=DiscardedPayload();Collect();long after=GC.GetTotalMemory(true);
            Check(discarded.State==RefreshIndexState.BudgetExceeded&&after-before<1024*1024,"Failed budget capture retains no megabyte-scale partial payload after collection");GC.KeepAlive(residue);GC.KeepAlive(discarded);
            await Task.CompletedTask;log.Add($"ALL CHECKS PASSED ({log.Count(s=>s.StartsWith("PASS "))})");return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);return 1;}
        finally{File.WriteAllLines(Path.Combine(args[0],"results.txt"),log);foreach(string line in log)Console.WriteLine(line);}
    }
    static void Collect(){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
    [MethodImpl(MethodImplOptions.NoInlining)]static RefreshIndex DiscardedPayload()=>Cache(Source(500000),8L*1024*1024);
    static IEnumerable<(FolderEntry,FileObservation)> CancelDuring(FolderEntry owner,CancellationTokenSource stop){yield return(owner,Obs(0));stop.Cancel();yield return(owner,Obs(1));}
    static FileObservation Obs(int i)
    {
        string name=string.Create(40,i,static(span,n)=>{span.Fill('f');n.TryFormat(span[^12..^4],out _,"D8");".bin".AsSpan().CopyTo(span[^4..]);});
        return new(name,i+1,4096,new DateTime(2026,1,1),7,(ulong)i+1,i==2,i==2?1:0,i==2?1:0,false,"");
    }
    static ScanResult Source(int count,string path=@"C:\synthetic",Counter? counter=null)
    {
        var root=new FolderEntry(path,null){ObservedId=42,FileCount=count};var child=new FolderEntry(Path.Combine(path,"child"),root){ObservedId=43,FileCount=count};root.Children.Add(child);
        var result=new ScanResult(root){Folders=2};result.RefreshDirectoryCandidates=Directories();result.FileCandidates=Files();return result;
        IEnumerable<(FolderEntry,RefreshDirectoryData)> Directories(){foreach(var folder in new[]{root,child})yield return(folder,new(folder.Path,folder.Parent?.Path,folder.ObservedId,new(0,0,1,7,folder.ObservedId,FileAttributes.Directory,0,0,true,""),new(count,2,3,4,5),folder.Measurement,true,folder.LogicalBytes,folder.AllocatedBytes,folder.FileCount,folder.IssueCount));}
        IEnumerable<(FolderEntry,FileObservation)> Files(){for(int i=0;i<count;i++){if(counter!=null)counter.Count++;yield return(child,Obs(i));}}
    }
    sealed class Counter{internal int Count;}
}
