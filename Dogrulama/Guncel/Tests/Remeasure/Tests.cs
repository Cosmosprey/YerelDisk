using YerelDisk;
using YerelDisk.Presentation;

static class Tests
{
    static readonly List<string> log=[];
    static readonly Guid volume=Guid.Parse("63c1ab1c-9afc-4a7b-a4db-53d31ea2ae21");
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static string Csv(WorkspaceViewModel m){using var w=new StringWriter();ReportSnapshot.Capture(m).Write(w);return w.ToString();}
    static TaskCompletionSource<ScanOutcome> Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        try{await Workflow();await Failures();await MultiRoot();await Lifetime();await Native(args[0]);log.Add($"ALL CHECKS PASSED ({log.Count})");return 0;}
        catch(Exception ex){log.Add("FAIL "+ex);return 1;}
        finally{File.WriteAllLines(Path.Combine(args[0],"results.txt"),log);foreach(var line in log)Console.WriteLine(line);}
    }
    static async Task Workflow()
    {
        var service=new Fake();var provider=new Inventory();using var vm=new WorkspaceViewModel(service,provider);
        Check(!vm.CanRemeasure,"No replacement is available before a result exists");await vm.RemeasureAsync();Check(service.Calls==0,"Remeasure without a result never invokes scanning");
        vm.Roots.Add(@"C:\scan");vm.Exclusions.Add(@"C:\scan\skip");await vm.ScanAsync();
        await vm.EnterAsync((FolderEntry)vm.Rows[0]);vm.SelectedRow=vm.Rows[0];
        await vm.Inventory.InspectAsync();vm.Inventory.ExportHints=true;
        var old=vm.Scan;var rows=vm.Rows;var selected=vm.SelectedRow;var summary=vm.Summary;var time=vm.MeasurementCompletedAt;var report=ReportSnapshot.Capture(vm);string csv=Csv(vm);string size=vm.LogicalSize;var index=vm.Inventory.Index;
        vm.Roots.Clear();vm.Roots.Add(@"D:\future");vm.Exclusions.Clear();vm.Exclusions.Add(@"D:\future\skip");vm.Workers=6;vm.FileLimit=10000;
        var gate=Gate();service.Next=(_,_,_)=>gate.Task;var operation=vm.RemeasureAsync();
        Check(service.Last!.Roots.SequenceEqual([@"C:\scan"]) && service.Last.Exclusions.SequenceEqual([@"C:\scan\skip"]),"Replacement freezes result scope instead of editable next-scan roots and exclusions");
        Check(service.Last.Workers==6 && service.Last.FileLimit==10000,"Current worker and retention settings are captured in the new request");
        Check(vm.IsBusy && vm.IsRemeasuring && vm.CanStop && !vm.CanRemeasure && !vm.CanExport && !vm.CanCompareMeasurements,"Replacement disables conflicting actions while retaining Stop");
        Check(ReferenceEquals(vm.Scan,old)&&ReferenceEquals(vm.Rows,rows)&&ReferenceEquals(vm.SelectedRow,selected)&&ReferenceEquals(vm.Summary,summary)&&vm.MeasurementCompletedAt==time,"Scan rows selection summary and timestamp remain the exact previous objects while preparing");
        using(var writer=new StringWriter()){report.Write(writer);Check(vm.LogicalSize==size && writer.ToString()==csv && vm.Status.Contains("Eski sonuç"),"Old metrics and captured report are not mixed with replacement preparation");}
        Check(ReferenceEquals(vm.Inventory.Index,index)&&vm.Inventory.ExportHints&&provider.Calls==1,"Replacement does not erase or automatically reread existing inventory hints");
        Check(ReferenceEquals(operation,vm.RemeasureAsync())&&ReferenceEquals(operation,vm.ScanAsync())&&service.Calls==2,"Repeated scan and replacement commands share the running task");
        await vm.SetViewAsync(WorkspaceView.Files);await vm.SortAsync("Name");vm.Search="changed";vm.SelectedRow=null;
        bool fullPath=vm.FullFolderPath;vm.FullFolderPath=!fullPath;
        Check(vm.FullFolderPath==fullPath && vm.AppliedQuery!.FullFolderPath==fullPath,"Folder-path option cannot diverge from frozen query during replacement");
        Check(vm.View==WorkspaceView.Folders && vm.Search=="" && ReferenceEquals(vm.SelectedRow,selected),"Navigation sort search and selection cannot disturb the frozen old view");
        vm.CancelScan();gate.SetResult(Example(200));await operation;
        Check(ReferenceEquals(vm.Scan,old)&&ReferenceEquals(vm.Rows,rows)&&vm.MeasurementCompletedAt==time&&Csv(vm)==csv,"Even a cancellation-ignoring service cannot publish a late result");
        Check(vm.Status.Contains("durduruldu")&&!vm.HasError&&!vm.IsBusy&&vm.CanRemeasure,"Cancelled replacement explains rollback and permits retry");
        Check(ReferenceEquals(vm.Inventory.Index,index)&&vm.Inventory.ExportHints,"Cancellation preserves inventory and export consent");

        var next=Example(300);bool coherent=true;vm.PropertyChanged+=(_,_)=>{if(ReferenceEquals(vm.Scan,next.Scan))coherent &= vm.Rows.All(r=>r is not FolderEntry f || Ancestor(f,next.Scan.Root)) && vm.AppliedQuery?.Folder?.Path==@"C:\scan\child";};
        service.Next=(_,_,_)=>Task.FromResult(next);await vm.RemeasureAsync();
        Check(ReferenceEquals(vm.Scan,next.Scan)&&vm.MeasurementCompletedAt>time&&vm.LogicalSize!=size,"Completed replacement publishes new scan metrics and measurement time");
        Check(coherent && vm.Rows.Count==1 && vm.FolderPath==@"C:\scan\child" && vm.SelectedRow is FolderEntry && !ReferenceEquals(vm.SelectedRow,selected),"Every notification sees coherent new rows and query with mapped folder and selection");
        Check(!vm.Inventory.HasSnapshot&&!vm.Inventory.ExportHints&&provider.Calls==1,"Successful replacement drops old hints without automatically reading inventory");
        Check(vm.Roots.SequenceEqual([@"D:\future"])&&vm.Exclusions.SequenceEqual([@"D:\future\skip"]),"Replacement never overwrites the editable future scope");
        using(var w=new StringWriter()){report.Write(w);Check(w.ToString()==csv,"Previously captured CSV remains immutable after replacement");}
        Check(old!.Root.LogicalBytes==100 && rows.Count==1,"Previous engine result and row list are never mutated by commit");
        service.Next=(_,_,_)=>Task.FromResult(Example(400,false));await vm.RemeasureAsync();
        Check(vm.FolderPath==@"C:\scan\child"&&vm.Rows.Count==0&&vm.SelectedRow==null,"Removed selected row clears selection while retaining an existing folder");
        var pendingQuery=vm.RefreshAsync(true);int calls=service.Calls;await vm.RemeasureAsync();
        Check(!vm.CanRemeasure&&service.Calls==calls,"A pending view query blocks replacement rather than discarding its applied state");await pendingQuery;
        Check(vm.CanRemeasure,"Completed view query restores replacement availability");
    }
    static bool Ancestor(FolderEntry f,FolderEntry root){for(var p=f;p!=null;p=p.Parent)if(ReferenceEquals(p,root))return true;return false;}
    static async Task Failures()
    {
        var unavailable=Example();unavailable.Scan.Failed=true;unavailable.Scan.Root.Measurement=MeasurementState.Unknown;unavailable.Scan.UnverifiedPaths.Add(unavailable.Scan.Root.Path);
        var missingService=new Fake{Next=(_,_,_)=>Task.FromResult(unavailable)};using(var missingModel=new WorkspaceViewModel(missingService,new Inventory()))
        {
            missingModel.Roots.Add(@"C:\scan");await missingModel.ScanAsync();await missingModel.SetViewAsync(WorkspaceView.Review);
            Check(!missingModel.CanCompareMeasurements && missingModel.EmptyMessage.Contains("doğrulanmış ölçüm yok"),"Entirely failed root disables reference actions and explains missing trusted review data");
        }
        var service=new Fake();using var vm=new WorkspaceViewModel(service,new Inventory());vm.Roots.Add(@"C:\scan");vm.Exclusions.Add(@"C:\scan\skip");await vm.ScanAsync();
        var old=vm.Scan!;var rows=vm.Rows;var time=vm.MeasurementCompletedAt;
        service.Next=(_,_,_)=>Task.FromException<ScanOutcome>(new IOException("fixture unavailable"));await vm.RemeasureAsync();
        Check(ReferenceEquals(vm.Scan,old)&&ReferenceEquals(vm.Rows,rows)&&vm.HasError&&vm.MeasurementCompletedAt==time,"Service exception preserves old scan rows and timestamp");
        foreach(string fault in new[]{"cancelled","failed","scope","retry","roots","exclusions","volume","missing-volume","root-id","same-object","prepare"})
        {
            var next=Example(200);switch(fault){case "cancelled":next.Scan.Cancelled=true;break;case "failed":next.Scan.Failed=true;break;case "scope":next.Scan.ScopeChanged=true;break;case "retry":next.Scan.NeedsRetry=true;break;case "roots":next.Scan.RootPaths[0]=@"D:\other";break;case "exclusions":next.Scan.ExcludedPaths=[];break;case "volume":next.Scan.RootVolumeIds=new Dictionary<string,Guid>{{@"C:\scan",Guid.NewGuid()}};break;case "missing-volume":next.Scan.RootVolumeIds=null;break;case "root-id":next.Scan.Root.ObservedId++;break;case "same-object":next=new(old,[]);break;case "prepare":next.Scan.Root.Children.Add(null!);break;}
            service.Next=(_,_,_)=>Task.FromResult(next);await vm.RemeasureAsync();
            Check(ReferenceEquals(vm.Scan,old)&&ReferenceEquals(vm.Rows,rows)&&vm.MeasurementCompletedAt==time&&vm.HasError,"Rejected replacement keeps previous generation: "+fault);
        }
        var partial=Example(210);partial.Scan.Root.Measurement=MeasurementState.Partial;partial.Scan.Root.Children[0].Measurement=MeasurementState.Unknown;partial.Scan.UnverifiedPaths.Add(@"C:\scan\child");partial.Scan.IssueCount=1;
        service.Next=(_,_,_)=>Task.FromResult(partial);await vm.RemeasureAsync();
        Check(ReferenceEquals(vm.Scan,partial.Scan)&&!vm.HasError&&vm.ShowScopeNotice&&vm.Status.Contains("kısmi"),"Ordinary inaccessible descendant warnings can publish an explicitly partial fresh result");
        var gate=new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);var provider=new Inventory{Gate=gate};using var blocked=new WorkspaceViewModel(new Fake(),provider);blocked.Roots.Add(@"C:\scan");await blocked.ScanAsync();var reading=blocked.Inventory.InspectAsync();
        Check(!blocked.CanRemeasure,"Replacement waits while an explicit inventory read is in flight");await blocked.RemeasureAsync();gate.SetResult(provider.Snapshot);await reading;Check(blocked.CanRemeasure,"Inventory completion restores replacement availability");
    }
    static async Task MultiRoot()
    {
        var service=new Fake();using var vm=new WorkspaceViewModel(service,new Inventory());service.Next=(r,_,_)=>Task.FromResult(Multi(r));vm.Roots.Add(@"C:\");vm.Roots.Add(@"D:\");await vm.ScanAsync();
        var c=vm.Scan!.Root.Children.Single(f=>f.Path==@"C:\");await vm.EnterAsync(c);await vm.EnterAsync((FolderEntry)vm.Rows[0]);
        service.Next=(r,_,_)=>Task.FromResult(Multi(r,false));await vm.RemeasureAsync();
        Check(vm.FolderPath==@"C:\"&&vm.Rows.Count==0,"Multi-root missing folder falls back to the nearest real root even when its path is shorter than the synthetic label");
        Check(vm.Scan!.RootPaths.SequenceEqual([@"C:\",@"D:\"]),"Replacement retains all original roots together");
    }
    static async Task Lifetime()
    {
        var service=new Fake();var vm=new WorkspaceViewModel(service,new Inventory());vm.Roots.Add(@"C:\scan");vm.Exclusions.Add(@"C:\scan\skip");await vm.ScanAsync();var old=vm.Scan;var time=vm.MeasurementCompletedAt;
        var gate=Gate();service.Next=(_,_,_)=>gate.Task;var op=vm.RemeasureAsync();int events=0;vm.PropertyChanged+=(_,_)=>events++;vm.Dispose();int before=events;gate.SetResult(Example(200));await op;
        Check(ReferenceEquals(vm.Scan,old)&&vm.MeasurementCompletedAt==time&&events==before,"Disposed workspace rejects late replacement without publishing notifications");
    }
    static async Task Native(string output)
    {
        string fixtures=Path.Combine(output,"own-fixtures"),root=Path.Combine(fixtures,"tree"),skip=Path.Combine(root,"skip");Directory.CreateDirectory(skip);Directory.CreateDirectory(Path.Combine(root,"child"));
        File.WriteAllBytes(Path.Combine(skip,"canary.bin"),new byte[999]);string file=Path.Combine(root,"child","file.bin");File.WriteAllBytes(file,new byte[11]);
        using var vm=new WorkspaceViewModel(new NativeScanService(),new Inventory());vm.Roots.Add(root);vm.Exclusions.Add(skip);await vm.ScanAsync();var old=vm.Scan!;Check(old.Root.LogicalBytes==11,"Native fixture initial scope omits excluded content");
        vm.Roots.Clear();vm.Roots.Add(Path.Combine(fixtures,"never-selected"));vm.Exclusions.Clear();File.WriteAllBytes(file,new byte[22]);await vm.RemeasureAsync();
        Check(!vm.HasError&&vm.Scan!.Root.LogicalBytes==22&&vm.Scan.RootPaths.Single()==root&&vm.Scan.ExcludedPaths.Single()==skip,"Native replacement uses original scope and exclusions after future scope edits");
        Check(old.Root.LogicalBytes==11&&new FileInfo(Path.Combine(skip,"canary.bin")).Length==999,"Native replacement preserves old metadata and excluded file contents");
        var latest=vm.Scan;Directory.Move(root,Path.Combine(fixtures,"old-tree"));Directory.CreateDirectory(root);Directory.CreateDirectory(skip);File.WriteAllBytes(Path.Combine(root,"different.bin"),new byte[55]);await vm.RemeasureAsync();
        Check(ReferenceEquals(vm.Scan,latest)&&vm.HasError,"Native root identity replacement cannot silently replace the previous scan");
    }
    static ScanOutcome Example(long bytes=100,bool nested=true)
    {
        var root=new FolderEntry(@"C:\scan",null){ObservedId=11,LogicalBytes=bytes,AllocatedBytes=4096,FileCount=1};var child=new FolderEntry(root.Path+@"\child",root){ObservedId=22,LogicalBytes=bytes,AllocatedBytes=4096,FileCount=1};root.Children.Add(child);
        if(nested)child.Children.Add(new(child.Path+@"\nested",child){ObservedId=33,LogicalBytes=bytes,AllocatedBytes=4096,FileCount=1});
        return new(new(root){ExcludedPaths=[root.Path+@"\skip"],RootVolumeIds=new Dictionary<string,Guid>{{root.Path,volume}},Files=[new(child.Path+@"\file.bin",bytes,4096,DateTime.MinValue,false,"")]},[]);
    }
    static ScanOutcome Multi(ScanRequest r,bool child=true)
    {
        var root=new FolderEntry("Seçilen klasörler (2)",null);var scan=new ScanResult(root){ExcludedPaths=r.Exclusions.ToArray()};scan.RootPaths.Clear();scan.RootPaths.AddRange(r.Roots);
        foreach(string path in r.Roots){var f=new FolderEntry(path,root){ObservedId=11};root.Children.Add(f);if(child)f.Children.Add(new(Path.Combine(path,"a"),f));}return new(scan,[]);
    }
    sealed class Fake:IScanService
    {
        internal int Calls;internal ScanRequest? Last;internal Func<ScanRequest,IProgress<ScanProgress>?,CancellationToken,Task<ScanOutcome>> Next=(_,_,_)=>Task.FromResult(Example());
        public string[] NormalizeRoots(IEnumerable<string> paths)=>paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest r,IProgress<ScanProgress>? p,CancellationToken t){Calls++;Last=r;return Next(r,p,t);}
    }
    sealed class Inventory:IInventoryProvider
    {
        internal int Calls;internal TaskCompletionSource<InventorySnapshot>? Gate;internal InventorySnapshot Snapshot=new(DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,[],[new("fixture",InventoryReadState.Complete,0,0,0)]);
        public Task<InventorySnapshot> ReadAsync(CancellationToken t){Calls++;return Gate?.Task??Task.FromResult(Snapshot);}
    }
}
