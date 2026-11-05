using YerelDisk;
using YerelDisk.Presentation;

internal static class FilteringTests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static FolderEntry Add(FolderEntry p,string name,long bytes){var f=new FolderEntry(Path.Combine(p.Path,name),p){LogicalBytes=bytes,AllocatedBytes=bytes,FileCount=1};p.Children.Add(f);return f;}
    static ReviewCandidate Candidate(FolderEntry f,ReviewKind kind)=>new(f.Path,f.LogicalBytes,f.AllocatedBytes,1,"fixture","fixture","GRADLE-CACHES","fixture","fixture",false,kind);
    static async Task<int> Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        try
        {
            var root=new FolderEntry(@"C:\Filter-Fixture",null);var cache=Add(root,"cache",1000);var pack=Add(root,"Package",2000);var modelFolder=Add(root,"models",3000);var apps=Add(root,"Apps",4000);Add(apps,"nested",4000);var work=Add(root,"Work",2500);Add(root,"Tiny",100);
            var scan=new ScanResult(root);var candidates=new[]{Candidate(cache,ReviewKind.Cache),Candidate(pack,ReviewKind.Protected),Candidate(modelFolder,ReviewKind.Conditional)};
            var query=new ViewQuery(WorkspaceView.Review,root,"",false,0,0,"Size",true);
            var all=ResultQuery.Execute(scan,candidates,query,default);
            Check(all.Counts is{Available:3,Hidden:0}&&all.Rows.Count==3,"Unfiltered counts describe eligible candidates, not all tree nodes");
            var combined=ResultQuery.Execute(scan,candidates,query with{ReviewMinimum=1500,ReviewKind=3,Search="no-match"},default);
            Check(combined.Counts is{Available:3,BySize:1,ByKind:1,BySearch:1,Hidden:3}&&combined.Rows.Count==0,"Each hidden row has one reason in size-kind-search order");
            Check(combined.Counts!.Available==combined.Rows.Count+combined.Counts.Hidden,"Shown plus hidden reconciles to the available list");
            Check(ResultQuery.Execute(scan,candidates,query with{ReviewMinimum=9000,ReviewKind=3,Search="no-match"},default).Counts is{BySize:3,ByKind:0,BySearch:0},"Later filters never double count already hidden rows");
            scan.ExcludedPaths=[pack.Path];var eligible=ResultQuery.Execute(scan,candidates,query,default);
            Check(eligible.Counts is{Available:2,Hidden:0}&&eligible.Rows.Count==2,"Unsafe/excluded candidates are not mislabeled as user-filtered records");scan.ExcludedPaths=[];
            var other=ResultQuery.Execute(scan,candidates,query with{Unrecognized=true,ReviewMinimum=2000,ReviewKind=3,Search="Apps"},default);
            Check(other.Counts is{Available:3,BySize:1,ByKind:0,BySearch:1}&&ReferenceEquals(other.Rows.Single(),apps),"Unknown frontier counts respect size/search and ignore hidden kind setting");
            Check(other.Areas!.Folders.Count==3&&other.Counts!.Available==3,"Unknown available count is the disjoint frontier, not descendant count");
            using(var canceled=new CancellationTokenSource()){canceled.Cancel();bool stopped=false;try{ResultQuery.Execute(scan,candidates,query,canceled.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped,"Canceled query publishes no partial filter statistics");}
            var fake=new Fake(new(scan,candidates.ToList()));using var vm=new WorkspaceViewModel(fake);vm.Roots.Add(root.Path);await vm.ScanAsync();await vm.SetViewAsync(WorkspaceView.Review);
            Check(vm.AppliedCounts is{Available:3,Hidden:3}&&vm.ListCaption.Contains("0 / 3")&&vm.CanClearReviewFilters,"Default size threshold explains why a nonempty candidate set looks empty");
            string[] roots=vm.Roots.ToArray();int workers=vm.Workers,limit=vm.FileLimit;
            await vm.ClearReviewFiltersAsync();Check(vm.Rows.Count==3&&vm.ReviewMinimum==0&&vm.ReviewKindFilter==0&&vm.Search==""&&!vm.HasReviewFilters,"Single action removes all active recognized filters");
            Check(fake.Calls==1&&vm.Roots.SequenceEqual(roots)&&vm.Workers==workers&&vm.FileLimit==limit,"Clearing filters never rescans or changes scan configuration");
            vm.ReviewKindFilter=3;await vm.RefreshAsync();vm.SelectedRow=vm.Rows.Single();var snapshot=ReportSnapshot.Capture(vm);
            Check(vm.AppliedCounts is{Available:3,ByKind:2,Hidden:2}&&vm.ListCaptionDetail.Contains("tür 2"),"Caption tooltip identifies the effective filter reason");
            Check(snapshot.Metadata.Any(r=>r[0]=="Filtre öncesi uygun liste kaydı"&&r[1]=="3"&&r[3]=="2")&&snapshot.Items.Length==1,"CSV metadata and exported rows describe the same applied query");
            vm.Search="no-match";var delayed=vm.RefreshAsync(true);await vm.ClearReviewFiltersAsync();await delayed;
            Check(vm.Rows.Count==3&&vm.AppliedCounts is{Available:3,Hidden:0}&&vm.AppliedSearch=="","Clear cancels a pending debounced query and prevents stale counts from winning");
            Check(snapshot.Items.Length==1&&snapshot.Metadata.Any(r=>r[0]=="Filtre öncesi uygun liste kaydı"&&r[3]=="2"),"Previously captured report remains immutable after filters clear");
            vm.ReviewKindFilter=3;await vm.RefreshAsync();await vm.SetReviewCollectionAsync(1);vm.ReviewMinimum=2000;vm.Search="Apps";await vm.RefreshAsync();
            Check(vm.IsUnrecognized&&vm.Rows.Count==1&&vm.AppliedCounts is{Available:3,Hidden:2},"Other areas exposes its own available and hidden totals");
            await vm.ClearReviewFiltersAsync();Check(vm.Rows.Count==3&&vm.ReviewKindFilter==3&&!vm.HasReviewFilters,"Other-area clear leaves the hidden recognized kind preference untouched");
            var otherReport=ReportSnapshot.Capture(vm);Check(otherReport.Unrecognized&&otherReport.Metadata.Any(r=>r[0]=="Sayaç kapsamı"&&r[1].Contains("tüm diskin")),"Unknown report explicitly limits counter meaning to this list");
            await vm.SetReviewCollectionAsync(0);Check(vm.Rows.Count==1&&vm.ReviewKindFilter==3,"Returning to recognized list preserves its independent type choice");
            await vm.SetViewAsync(WorkspaceView.Folders);int count=vm.Rows.Count;await vm.ClearReviewFiltersAsync();Check(vm.Rows.Count==count&&vm.ReviewKindFilter==3&&!vm.CanClearReviewFilters,"Clear is inert outside review mode");
            await vm.SetViewAsync(WorkspaceView.Review);fake.Gate=new(TaskCreationOptions.RunContinuationsAsynchronously);var pendingScan=vm.ScanAsync();
            Check(vm.IsBusy&&vm.AppliedCounts==null&&!vm.CanClearReviewFilters&&!vm.ListCaption.Contains(" / 3"),"A new running scan discards old counters and disables clearing");
            fake.Gate.SetResult(new(new(new FolderEntry(root.Path,null)),[]));await pendingScan;
            Check(vm.AppliedCounts is{Available:0,Hidden:0}&&vm.Rows.Count==0,"Fresh empty scan does not inherit counts from the old tree");
            log.Add($"ALL CHECKS PASSED ({log.Count})");File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(ex);return 1;}
    }
    sealed class Fake(ScanOutcome outcome):IScanService
    {internal int Calls;internal TaskCompletionSource<ScanOutcome>? Gate;public string[] NormalizeRoots(IEnumerable<string> paths)=>paths.ToArray();public Task<ScanOutcome> ScanAsync(ScanRequest request,IProgress<ScanProgress>? progress,CancellationToken token){Calls++;return Gate?.Task??Task.FromResult(outcome);}}
}
