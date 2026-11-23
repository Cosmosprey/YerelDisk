using System.Diagnostics;
using YerelDisk;
using YerelDisk.Presentation;

var log = new List<string>();
string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
try
{
    var root = new FolderEntry(@"C:\population-example", null);
    var a = new FolderEntry(root.Path + @"\many", root); var b = new FolderEntry(root.Path + @"\videos", root);
    root.Children.AddRange([a,b]);
    var scan = Snapshot(root, [(a, Obs("empty.txt",0)), (a,Obs("edge.bin",4096)), (a,Obs("above.BIN",4097)), (b,Obs("movie.mp4",10*1024*1024)), (root,Obs("README",0))]);
    scan.Files = [new(b.Path + @"\movie.mp4",10*1024*1024,null,default,false,"")];
    var result = FilePopulationAnalysis.Create(scan,root);
    Check(result.Available && result.Complete && result.Count == 5, "Analysis includes all names despite a one-file retained list");
    Check(result.Small == 3 && result.Empty == 2, "4 KiB boundary is inclusive and empty files are a subset");
    Check(result.Types.Single(r=>r.Extension==".bin").Count==2 && result.Types.Single(r=>r.Extension==".bin").Bytes==8193, "Extension case folds invariantly and preserves total bytes");
    Check(result.Types.Single(r=>r.Extension=="Uzantısız").Count==1, "Extensionless files have an explicit group");
    Check(result.Types.Sum(r=>r.Count)==5 && result.Types.Sum(r=>r.Bytes)==result.Bytes, "Type rows reconcile with all analyzed names and bytes");
    Check(result.Areas.Sum(r=>r.Count??0)==5 && result.Areas.Sum(r=>r.Bytes??0)==result.Bytes, "Disjoint child areas and direct files reconcile without double counting");
    Check(result.Areas[0].Path==a.Path && result.Areas[0].Count==3 && result.Areas[0].Fraction==0.6, "Dense child ranks ahead of the larger video child");
    Check(result.Areas.Single(r=>r.Caption!=null).Count==1, "Direct files remain a separate non-folder area");
    scan.FileCandidates=null;
    Check(FilePopulationAnalysis.Create(scan,root).Count==5, "Analysis needs no surviving per-file candidates");
    Check(!FilePopulationAnalysis.Create(scan,new FolderEntry(root.Path,null)).Available, "An unrelated folder object with identical text is rejected");
    root.Measurement=MeasurementState.Previous;
    Check(!FilePopulationAnalysis.Create(scan,a).Available, "Previous ancestor prevents a misleading descendant analysis");
    root.Measurement=MeasurementState.Measured;
    a.Measurement=MeasurementState.Unknown;
    var partial=FilePopulationAnalysis.Create(scan,root);
    Check(!partial.Complete && partial.Count==2 && partial.Areas.Single(r=>r.Path==a.Path).Count==null, "Unknown child is not represented as an empty measured area");
    a.Measurement=MeasurementState.Measured;
    a.Measurement=MeasurementState.Partial;
    Check(!FilePopulationAnalysis.Create(scan,root).Complete && FilePopulationAnalysis.Create(scan,root).Count==5, "A partial child qualifies the whole analysis even when observed counts happen to match");
    a.Measurement=MeasurementState.Measured;
    scan.UnverifiedPaths.Add(a.Path);
    Check(FilePopulationAnalysis.Create(scan,root).Count==2, "Unsafe subtree summaries are not counted");
    scan.UnverifiedPaths.Clear();
    scan.Cancelled=true;
    Check(!FilePopulationAnalysis.Create(scan,root).Complete && FilePopulationAnalysis.Create(scan,root).Coverage.Contains("durdurulmuştu"), "Cancelled scans remain explicitly partial");
    scan.Cancelled=false;
    Check(!FilePopulationAnalysis.Create(new ScanResult(root),root).Available, "Missing summaries never fall back to the top-K list");
    using(var cancelled=new CancellationTokenSource())
    { cancelled.Cancel(); bool seen=false; try{FilePopulationAnalysis.Create(scan,root,cancelled.Token);}catch(OperationCanceledException){seen=true;} Check(seen,"Cancelled analysis stops before publishing"); }

    var drive = new FolderEntry(@"C:\",null);
    var uncertain = new ScanResult(drive); uncertain.UnverifiedPaths.Add(@"C:\uncertain.bin");
    uncertain.FileCandidates=[(drive,Obs("uncertain.bin",1)),(drive,Obs("good.bin",2))]; drive.FileCount=2;
    uncertain.ObservedEvidence=ObservedFileEvidence.Capture([uncertain]);
    Check(FilePopulationAnalysis.Create(uncertain,drive).Count==1, "File-level uncertainty at a drive root is excluded without dropping healthy siblings");
    var oddRoot=new FolderEntry(@"C:\odd-inputs",null);
    var odd=Snapshot(oddRoot,[(oddRoot,Obs("good.bin",1)),(oddRoot,Obs("bad.bin",2) with {Unverified=true}),(oddRoot,Obs(@"..\escape.bin",3)),(oddRoot,Obs("negative.bin",-1))]);
    Check(FilePopulationAnalysis.Create(odd,oddRoot).Count==1 && !FilePopulationAnalysis.Create(odd,oddRoot).Complete, "Unverified, malformed and negative observations cannot become positive evidence");
    var excludedRoot=new FolderEntry(@"C:\excluded-inputs",null);
    var excluded=new ScanResult(excludedRoot); excluded.FileCandidates=[(excludedRoot,Obs("omit.bin",1)),(excludedRoot,Obs("keep.bin",2))]; excludedRoot.FileCount=1;
    var policy=ScanExclusions.Create([excludedRoot.Path],[excludedRoot.Path+@"\omit.bin"]); excluded.ExcludedPaths=policy.Paths;
    excluded.ObservedEvidence=ObservedFileEvidence.Capture([excluded],policy);
    Check(FilePopulationAnalysis.Create(excluded,excludedRoot).Count==1 && FilePopulationAnalysis.Create(excluded,excludedRoot).Coverage.Contains("hariç"), "Excluded entries stay out and scope remains visible");

    var budgetRoot=new FolderEntry(@"C:\budgets",null);
    var budgetSource=new ScanResult(budgetRoot);
    var builder=new FilePopulationEvidence.Builder(maxOwners:2,maxTypes:2,maxTypesPerOwner:1); builder.BeginSource(budgetSource,null);
    for(int i=0;i<3;i++) builder.Add(new FolderEntry(budgetRoot.Path+"\\d"+i,budgetRoot),Obs("sample.bin",1));
    var bounded=builder.Complete();
    Check(bounded.OwnerLimitReached && bounded.Owners.Count==2,"Owner budget is explicit and bounded");
    builder=new FilePopulationEvidence.Builder(maxTypes:1); builder.BeginSource(budgetSource,null);
    builder.Add(budgetRoot,Obs("first.bin",2));builder.Add(budgetRoot,Obs("second.txt",3));builder.Add(budgetRoot,Obs("third.BIN",4));
    var boundedTypes=builder.Complete().Owners[budgetRoot];
    Check(boundedTypes.Count==3 && boundedTypes.Other==new FileTypePopulation(1,3) && boundedTypes.Types.Single().Value==new FileTypePopulation(2,6), "Type budget keeps counts and still updates previously tracked types");
    var varied=Snapshot(budgetRoot,Enumerable.Range(0,60).Select(i=>(budgetRoot,Obs("sample.e"+i,i))));
    var variedResult=FilePopulationAnalysis.Create(varied,budgetRoot);
    Check(variedResult.Count==60 && variedResult.Types.Sum(r=>r.Count)==60 && variedResult.Types.Count<=13 && variedResult.Coverage.Contains("bütçesi"), "Per-owner and display limits conserve the remainder in an explicit group");
    var longType=Snapshot(budgetRoot,[(budgetRoot,Obs("file."+new string('x',100),5))]);
    Check(FilePopulationAnalysis.Create(longType,budgetRoot).Types.Single().Extension.Contains("özetlenemeyen"), "Long suffixes do not become unbounded retained strings");
    var manyRoot=new FolderEntry(@"C:\many-areas",null);
    var manyItems=new List<(FolderEntry,FileObservation)>();
    for(int i=0;i<40;i++){var child=new FolderEntry(manyRoot.Path+"\\child-"+i,manyRoot);manyRoot.Children.Add(child);manyItems.Add((child,Obs("file.bin",i)));}
    var many=FilePopulationAnalysis.Create(Snapshot(manyRoot,manyItems),manyRoot);
    Check(many.Areas.Count<=14 && many.Areas.Sum(r=>r.Count??0)==40 && many.Areas.Any(r=>r.Caption?.StartsWith("Diğer ")==true), "Child ranking is bounded and hidden siblings are not lost");
    foreach(bool partialFirst in new[]{true,false})
    {
        var regressionRoot=new FolderEntry(@"C:\visible-partial-"+partialFirst,null);
        var visiblePartial=new FolderEntry(regressionRoot.Path+@"\partial",regressionRoot){Measurement=MeasurementState.Partial};
        for(int i=0;i<12;i++)regressionRoot.Children.Add(new(regressionRoot.Path+"\\empty-"+i,regressionRoot));
        if(partialFirst)regressionRoot.Children.Insert(0,visiblePartial);else regressionRoot.Children.Add(visiblePartial);
        var regression=FilePopulationAnalysis.Create(Snapshot(regressionRoot,[(visiblePartial,Obs("one.bin",1))]),regressionRoot);
        var other=regression.Areas.Single(r=>r.Caption?.StartsWith("Diğer ")==true);
        Check(!regression.Complete && other is{Count:0,Bytes:0,Partial:false},"Visible partial child cannot taint measured hidden empty areas; insertion first="+partialFirst);
    }
    var hiddenRoot=new FolderEntry(@"C:\hidden-partial",null);
    var hiddenUnknown=new FolderEntry(hiddenRoot.Path+@"\unknown",hiddenRoot){Measurement=MeasurementState.Unknown};hiddenRoot.Children.Add(hiddenUnknown);
    var knownItems=new List<(FolderEntry,FileObservation)>();
    for(int i=0;i<12;i++){var c=new FolderEntry(hiddenRoot.Path+"\\known-"+i,hiddenRoot);hiddenRoot.Children.Add(c);knownItems.Add((c,Obs("one.bin",1)));}
    var hiddenAnalysis=FilePopulationAnalysis.Create(Snapshot(hiddenRoot,knownItems),hiddenRoot);
    Check(hiddenAnalysis.Areas.Single(r=>r.Caption?.StartsWith("Diğer ")==true) is{Count:null,Bytes:null,Partial:true},"A genuinely unknown hidden area stays unknown after the ranking evicts it");
    var linked=Snapshot(budgetRoot,[(budgetRoot,Obs("one.bin",10) with{HardLinked=true}),(budgetRoot,Obs("two.bin",10) with{HardLinked=true})]);
    Check(FilePopulationAnalysis.Create(linked,budgetRoot).Count==2 && FilePopulationAnalysis.Create(linked,budgetRoot).Bytes==20 && FilePopulationAnalysis.Limits.Contains("Hard link"), "Logical name counts stay distinct from physical hard-link allocation");
    var overflow=Snapshot(budgetRoot,[(budgetRoot,Obs("one.bin",long.MaxValue)),(budgetRoot,Obs("two.bin",1))]);
    Check(FilePopulationAnalysis.Create(overflow,budgetRoot) is {Bytes:long.MaxValue,Complete:false}, "Overflow saturates with a qualified result instead of wrapping negative");
    var emptyRoot=new FolderEntry(@"C:\empty-scope",null);
    var empty=FilePopulationAnalysis.Create(Snapshot(emptyRoot,[]),emptyRoot);
    Check(empty is {Count:0,Available:true,Complete:true} && empty.Explanation.Contains("dosya bulunmadı"), "A genuinely empty snapshot is distinguished from unavailable evidence");

    var largeRoot=new FolderEntry(@"C:\streamed-metadata",null){FileCount=200_000};
    var large=new ScanResult(largeRoot){FileCandidates=Repeated(largeRoot,200_000)};
    long before=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();var captured=ObservedFileEvidence.Capture([large]);watch.Stop();long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
    Check(captured.Population.Owners.Count==1 && captured.Population.Owners[largeRoot].Count==200_000 && allocated<256*1024, "Streaming 200k synthetic observations does not allocate per-file wrappers");
    log.Add($"Synthetic metadata stream (same reused name, not a filesystem benchmark): {watch.ElapsedMilliseconds} ms, {allocated} allocated bytes.");
    log.Add($"ALL CHECKS PASSED ({log.Count(s=>s.StartsWith("PASS "))})"); return 0;
}
catch(Exception ex){log.Add("FAIL "+ex);return 1;}
finally{File.WriteAllLines(Path.Combine(output,"results.txt"),log);foreach(string line in log)Console.WriteLine(line);}

void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
FileObservation Obs(string name,long size)=>new(name,size,null,default,0,0,false,0,0,false,"");
ScanResult Snapshot(FolderEntry root,IEnumerable<(FolderEntry Owner,FileObservation File)> input)
{
    var items=input.ToArray(); var nodes=new Stack<FolderEntry>();nodes.Push(root);
    while(nodes.TryPop(out var node)){node.FileCount=0;node.LogicalBytes=0;foreach(var child in node.Children)nodes.Push(child);}
    foreach(var item in items)for(FolderEntry? owner=item.Owner;owner!=null;owner=owner.Parent){owner.FileCount++;owner.LogicalBytes=FileTypePopulation.Sum(owner.LogicalBytes,Math.Max(0,item.File.Size));}
    var scan=new ScanResult(root){FileCandidates=items};scan.ObservedEvidence=ObservedFileEvidence.Capture([scan]);return scan;
}
IEnumerable<(FolderEntry,FileObservation)> Repeated(FolderEntry owner,int n){for(int i=0;i<n;i++)yield return(owner,Obs("reused.bin",1));}
