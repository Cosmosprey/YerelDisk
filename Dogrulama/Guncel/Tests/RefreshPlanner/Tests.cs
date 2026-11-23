using System.Diagnostics;
using YerelDisk;

static class Tests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);log.Add("PASS "+name);}
    static void Reject(Action a,string label){try{a();}catch(ArgumentOutOfRangeException){Check(true,label);return;}throw new Exception(label);}
    static int Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        try
        {
            var t=Fixture.Old();var plan=Fixture.Plan(t);Check(plan.Action==RefreshAction.MeasureSelected&&plan.Reason==RefreshReason.SelectedScanRequired,"Trusted old snapshot requests selected measurement before approving any merge");
            Check(plan.ScopeRoots.SequenceEqual([@"C:\scope"])&&plan.SelectedPath==@"C:\scope\a"&&plan.SelectedIndexBudgetBytes>0,"Selected plan freezes original scope and grants a bounded capture budget");
            Check(plan.ReservedMetadataBytes<=Fixture.Limits.OperationBytes&&plan.SelectedIndexBudgetBytes<=Fixture.Limits.IndexBytes,"Combined old fresh replacement and identity reservation fits the operation budget");
            var fresh=Fixture.Fresh();var ready=Fixture.Plan(t,fresh);Check(ready.Action==RefreshAction.MergeSelected,"Fresh trustworthy selected measurement can become merge eligible");
            Check(ready.KeepsOutsideMeasurements&&ready.Explanation.Contains("önceki ölçümü"),"Partial-refresh decision explicitly preserves older outside measurements");
            Check(t.Scan.Root.FileCount==2&&t.Scan.RefreshIndex!.FileCount==2&&fresh.FileCount==1,"Decision does not mutate old or new observations");
            var frozen=Fixture.Plan(t);t.Scan.RootPaths[0]=@"D:\edited";t.Scan.ExcludedPaths=[@"D:\edited\skip"];
            Check(frozen.ScopeRoots.Single()==@"C:\scope"&&frozen.Exclusions.Count==0&&frozen.ScopeRoots is not string[],"Published decision scope cannot change with source or caller array edits");
            t=Fixture.Old();t.Scan.RefreshIndex=null;plan=Fixture.Plan(t);Check(plan.Action==RefreshAction.MeasureScope&&plan.Reason==RefreshReason.MissingIndex,"Missing index automatically uses the original full scope");
            t=Fixture.Old();t.Scan.RootVolumeIds=null;Check(Fixture.Plan(t).Action==RefreshAction.KeepPrevious,"Unknown original root identity keeps the old result");
            t=Fixture.Old();t.Live[0]=t.Live[0]with{Id=999};Check(Fixture.Plan(t).Action==RefreshAction.KeepPrevious,"Changed live root directory ID stops automatic refresh");
            t=Fixture.Old();t.Live[0]=t.Live[0]with{VolumeId=Guid.NewGuid()};Check(Fixture.Plan(t).Reason==RefreshReason.RootIdentityChanged,"Changed live disk GUID stops despite the same directory ID");
            t=Fixture.Old();t.Live[0]=t.Live[0]with{VolumeId=null};Check(Fixture.Plan(t).Action==RefreshAction.KeepPrevious,"Unavailable live identity never silently expands scope");
            t=Fixture.Old();t.Scan.RefreshIndex=Fixture.Seal(t.Scan.Root,[(t.Selected,Fixture.File(1)),(t.Scan.Root.Children[1],Fixture.File(3))],[],1<<20,cachedSerial:99).RefreshIndex;
            Check(Fixture.Plan(t).Reason==RefreshReason.IndexMismatch,"Synthetic root serial mismatch accepted by capture is rejected before planning");
            t=Fixture.Old();t.Scan.RefreshIndex=Fixture.Seal(t.Scan.Root,[(t.Selected,Fixture.File(1)),(t.Scan.Root.Children[1],Fixture.File(3))],[],1<<20,cachedGuid:Guid.NewGuid()).RefreshIndex;
            Check(Fixture.Plan(t).Reason==RefreshReason.IndexMismatch,"Synthetic cached GUID cannot replace the authoritative original root GUID");
            t=Fixture.Old();
            t=Fixture.Old();Check(RefreshPlanner.Evaluate(t.Scan,t.Selected,[],Fixture.Limits).Action==RefreshAction.KeepPrevious,"Missing live root facts prevent execution");
            Check(RefreshPlanner.Evaluate(t.Scan,t.Scan.Root,t.Live,Fixture.Limits).Reason==RefreshReason.WholeScope,"Selecting the complete result routes directly to full-scope measurement");
            var impostor=new FolderEntry(t.Selected.Path,t.Scan.Root){ObservedId=t.Selected.ObservedId};
            Check(RefreshPlanner.Evaluate(t.Scan,impostor,t.Live,Fixture.Limits).Reason==RefreshReason.OutsideScope,"Matching path and ID do not authorize a foreign folder object");
            t=Fixture.Old(excluded:[@"C:\scope\a"]);Check(Fixture.Plan(t).Reason==RefreshReason.Excluded,"Excluded selection stops rather than following or expanding it");
            t=Fixture.Old(configure:(r,a,b)=>{a.Measurement=MeasurementState.Partial;a.IssueCount=1;r.Measurement=MeasurementState.Partial;});
            Check(Fixture.Plan(t).Action==RefreshAction.MeasureScope,"Uncertain selected subtree requests full-scope measurement");
            t=Fixture.Old(configure:(r,a,b)=>{var stub=new FolderEntry(r.Path+@"\skipped",r){Measurement=MeasurementState.Unknown};r.Children.Add(stub);r.Measurement=MeasurementState.Partial;r.IssueCount=1;});
            Check(t.Scan.RefreshIndex!.HasUncertainty&&Fixture.Plan(t).Action==RefreshAction.MeasureSelected,"Unobserved zero-byte outside placeholder stays unknown without blocking a healthy selected area");
            t=Fixture.Old(configure:(r,a,b)=>{b.Measurement=MeasurementState.Previous;r.Measurement=MeasurementState.Partial;});
            Check(Fixture.Plan(t).Action==RefreshAction.MeasureScope,"Stale outside observations cannot enter an incremental merge");
            t=Fixture.Old(inside:1,outside:1);plan=Fixture.Plan(t);Check(plan.Action==RefreshAction.MeasureScope&&plan.Reason==RefreshReason.SharedOutside,"Known cross-boundary hard-link identity requests full scope before selected I/O");
            t=Fixture.Old();plan=Fixture.Plan(t,Fixture.Fresh(fileId:3));Check(plan.Reason==RefreshReason.SharedOutside,"Newly created alias of an outside file invalidates the initial fast decision");
            t=Fixture.Old(inside:1,outside:1);Check(Fixture.Plan(t,Fixture.Fresh(count:0)).Reason==RefreshReason.SharedOutside,"Deleted first alias still requires outside link-state verification");
            t=Fixture.Old(outsideName:"A");Check(Fixture.Plan(t).Action==RefreshAction.MeasureSelected,"Case-distinct sibling remains outside the selected subtree");
            t=Fixture.Old(excluded:[@"C:\scope\A\skip"]);plan=Fixture.Plan(t);
            Check(plan.SelectedExclusions.Single()==@"C:\scope\A\skip","Inherited exclusions retain the existing case-insensitive policy");
            Check(Fixture.Plan(t,Fixture.Fresh(excluded:plan.SelectedExclusions.ToArray())).Action==RefreshAction.MergeSelected,"Matching inherited exclusion scope permits a fresh selected measurement");
            var prefix=Fixture.Old(inside:1,outside:1,outsideName:"ab");
            Check(Fixture.Plan(prefix).Reason==RefreshReason.SharedOutside,"Path prefix sibling is outside the selected scope");
            prefix=Fixture.Old(outsideName:"ab");
            Check(Fixture.Plan(prefix,Fixture.Fresh(fileId:3)).Reason==RefreshReason.SharedOutside,"Fresh alias in prefix sibling requires full original scope");
            Check(RefreshPlanner.Below(@"C:\scope\a",@"C:\")&&RefreshPlanner.Below(@"C:\scope\a\nested",@"C:\scope\a\"),"Drive roots and trailing separators accept real descendants");
            Check(RefreshPlanner.Below(@"C:\scope\a",@"C:\scope\a")&&!RefreshPlanner.Below(@"C:\scope\A",@"C:\scope\a"),"Exact selected path is inside but case-distinct sibling is outside");
            t=Fixture.Old();Check(Fixture.Plan(t,Fixture.Fresh(excluded:[@"C:\scope\a\unexpected"])).Action==RefreshAction.KeepPrevious,"Fresh exclusion mismatch cannot change the user's scope");
            Check(Fixture.Plan(t,Fixture.Fresh(path:@"D:\other")).Reason==RefreshReason.FreshScopeMismatch,"Fresh result from another path is rejected");
            Check(Fixture.Plan(t,Fixture.Fresh(rootId:222)).Reason==RefreshReason.SelectedIdentityChanged,"Replaced selected directory widens within the verified original roots");
            Check(Fixture.Plan(t,Fixture.Fresh(volume:Guid.NewGuid())).Action==RefreshAction.KeepPrevious,"Fresh selected measurement on another disk is not published");
            Check(Fixture.Plan(t,Fixture.Fresh(configure:r=>r.Measurement=MeasurementState.Partial)).Action==RefreshAction.MeasureScope,"Uncertain fresh selected result requires full scope");
            Check(Fixture.Plan(t,Fixture.Fresh(budget:1)).Reason==RefreshReason.FreshUnavailable,"Fresh cache exhaustion safely falls back to the original scope");
            var allocationFailed=RefreshIndex.Capture(AllocationFailure(),Fixture.Fresh().Roots,[],1<<20)!;
            Check(allocationFailed.State==RefreshIndexState.AllocationFailed,"Injected allocation exception is represented without exhausting real memory");
            var allocationPlan=Fixture.Plan(t,allocationFailed);
            Check(allocationPlan.Action==RefreshAction.KeepPrevious&&allocationPlan.Reason==RefreshReason.AllocationFailed,"Current fresh allocation failure stops instead of requesting a larger full scan");
            var uncachedFailure=Fixture.Old();uncachedFailure.Scan.RefreshIndex=null;
            Check(Fixture.Plan(uncachedFailure,allocationFailed).Action==RefreshAction.KeepPrevious,"Current allocation failure takes priority over a missing historical cache");
            var historical=Fixture.Old();historical.Scan.RefreshIndex=allocationFailed;
            Check(Fixture.Plan(historical).Action==RefreshAction.MeasureScope,"Historical unavailable cache does not permanently block a later deliberate measurement");
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();Check(Fixture.Plan(t,token:cancel.Token).Action==RefreshAction.KeepPrevious,"Cancellation produces a nonexecuting keep-previous decision");}
            Check(Fixture.Plan(t,limits:new(0,4<<20,64<<10)).Action==RefreshAction.MeasureScope,"Disabled index policy automatically uses full measurement");
            Check(Fixture.Plan(t,limits:new(1<<20,0,64<<10)).Reason==RefreshReason.MetadataBudget,"Insufficient combined operation budget cannot start a selected scan");
            Check(Fixture.Plan(t,limits:new(1<<20,4<<20,0)).Reason==RefreshReason.IdentityBudget,"Identity workspace limit is checked before allocating the ID array");
            var empty=Fixture.Old(count:0);Check(Fixture.Plan(empty,Fixture.Fresh(count:0),new(1<<20,4<<20,0)).Action==RefreshAction.MergeSelected,"Empty selected measurements need no identity array allocation");
            Check(Fixture.Plan(t,Fixture.Fresh(),new(128<<10,4<<20,64<<10)).Reason==RefreshReason.MetadataBudget,"Fresh producer must respect its granted capture budget even if payload happened to be small");
            var larger=Fixture.Old(inside:1000,outside:3,count:512);var freshLarge=Fixture.Fresh(fileId:2000,count:512);
            long cap=Math.Max(larger.Scan.RefreshIndex!.MetadataBytes,freshLarge.MetadataBytes)+4096;
            freshLarge=Fixture.Fresh(fileId:2000,count:512,budget:cap);
            Check(Fixture.Plan(larger,freshLarge,new(cap,8<<20,1<<20)).Reason==RefreshReason.MetadataBudget,"Conservative replacement bound accounts for old and fresh metadata together");
            Reject(()=>new RefreshMemoryLimits(-1,1,1),"Negative index policy rejected");Reject(()=>new RefreshMemoryLimits(1,-1,1),"Negative operation policy rejected");Reject(()=>new RefreshMemoryLimits(1,1,65L<<20),"Excessive identity allocation policy rejected");
            var timer=Stopwatch.StartNew();for(int i=0;i<100;i++)Fixture.Plan(Fixture.Old());timer.Stop();log.Add($"MEASURE 100 small synthetic decisions {timer.ElapsedMilliseconds} ms; not disk I/O");
            log.Add($"ALL CHECKS PASSED ({log.Count(s=>s.StartsWith("PASS "))})");return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);return 1;}
        finally{File.WriteAllLines(Path.Combine(args[0],"results.txt"),log);foreach(var line in log)Console.WriteLine(line);}
    }
    static IEnumerable<ScanResult> AllocationFailure()
    { yield return ThrowAllocationFailure(); }
    static ScanResult ThrowAllocationFailure()=>throw new OutOfMemoryException("Injected small test exception; no memory exhaustion.");
}
