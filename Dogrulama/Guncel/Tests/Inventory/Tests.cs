using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using YerelDisk;
using YerelDisk.Presentation;

internal static class InventoryTests
{
    static readonly List<string> log=[]; static void Check(bool value,string name){if(!value)throw new Exception(name);log.Add("PASS "+name);}
    static InventorySnapshot Snapshot(params InventoryApplication[] entries)=>new(DateTimeOffset.Now,DateTimeOffset.Now,entries,[new("fixture",InventoryReadState.Complete,entries.Length,0,0)]);
    static InventoryApplication App(string path,string name="PRIVATE-APP-CANARY")=>new(name,"fixture publisher","1.0",path,"fixture source");
    static FolderEntry Add(FolderEntry parent,string name,long bytes=1){var f=new FolderEntry(Path.Combine(parent.Path,name),parent){LogicalBytes=bytes,FileCount=1};parent.Children.Add(f);return f;}
    static async Task<int> Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);CultureInfo.DefaultThreadCurrentCulture=CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            foreach(string path in new[]{@"\\host\share",@"\\?\C:\App",@"C:relative",@"C:\",@"C:\App\..\Else",@"C:\App\.",@"C:\App:stream",@"C:\%ROOT%\App",@"C:\A*",@"C:\A?",@"C:\App.\child",@"C:\App \child"}) Check(InventoryPaths.Normalize(path)==null,"Invalid/ambiguous registry path rejected: "+path);
            Check(InventoryPaths.Normalize(" \"d:/Apps/Editor/\" ")==@"d:\Apps\Editor","Quoted local path and slash/trailing separator normalize lexically");
            var root=new FolderEntry(@"D:\Apps",null);var editor=Add(root,"Editor");var nested=Add(editor,"Data");var sibling=Add(root,"EditorPlus");var skipped=Add(root,"Skipped");var old=Add(root,"Old");old.Measurement=MeasurementState.Previous;var unknown=Add(root,"Unknown");unknown.Measurement=MeasurementState.Unknown;
            var scan=new ScanResult(root){ExcludedPaths=[skipped.Path]};var snapshot=Snapshot(App(editor.Path),App(@"D:\Apps\Absent","NOT-OBSERVED"),App(skipped.Path,"EXCLUDED"),App(old.Path,"OLD"),App(unknown.Path,"UNKNOWN"),App(@"C:\Other","OUTSIDE"));
            var index=InventoryPathIndex.Create(scan,snapshot,default);
            Check(index.Matches(editor.Path).Single() is{Contained:false},"Exact observed folder has direct registration-path evidence");
            Check(index.Matches(nested.Path).Single().Application.Name=="PRIVATE-APP-CANARY","Descendant path inherits only lexical registered location evidence");
            Check(index.Matches(sibling.Path).Count==0,"Editor prefix never matches EditorPlus");
            Check(index.Matches(root.Path).Count==1&&index.Matches(root.Path)[0].Contained,"Containing row uses only observed eligible installation targets");
            Check(index.Matches(skipped.Path).Count==0&&index.Matches(old.Path).Count==0&&index.Matches(unknown.Path).Count==0,"Excluded/previous/unknown targets are never positive evidence");
            Check(index.Matches(@"D:\Apps\Editor\NotObserved").Count==0,"Unobserved foreign/stale row path cannot get a relationship");
            Check(index.Details(sibling.Path).Contains("kaldırıldığı")&&index.Details(sibling.Path).Contains("anlamına gelmez"),"No match does not mean removed or unnecessary");
            Check(index.Details(editor.Path).Contains("etkin kullanım doğrulanmadı")&&index.Details(editor.Path).Contains("fixture source"),"Detail retains method, source and usage uncertainty");
            var shared=InventoryPathIndex.Create(scan,Snapshot(App(root.Path,"SUITE"),App(editor.Path,"EDITOR"),App(editor.Path,"EDITOR-SECOND")),default);
            Check(shared.Matches(editor.Path).Count==3&&shared.Matches(editor.Path)[0].Application.InstallPath==editor.Path,"Overlapping registrations preserve all sources with specific path first");
            Check(shared.Matches(editor.Path).Select(h=>h.Application.Name).Distinct().Count()==3,"No single-owner inference from overlapping paths");
            var metaRoot=new FolderEntry("Seçili konumlar",null);var c=new FolderEntry(@"C:\User",metaRoot);var d=new FolderEntry(@"D:\IDE",metaRoot){LogicalBytes=1};metaRoot.Children.AddRange([c,d]);var multi=new ScanResult(metaRoot);multi.RootPaths.Clear();multi.RootPaths.AddRange([c.Path,d.Path]);
            Check(InventoryPathIndex.Create(multi,Snapshot(App(d.Path)),default).Matches(d.Path).Count==1,"Separate D installation is recognized under multi-root scan");
            scan.ScopeChanged=true;Check(InventoryPathIndex.Create(scan,snapshot,default).Matches(editor.Path).Count==0,"Unresolved global scope change suppresses registration matches");scan.ScopeChanged=false;
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool stopped=false;try{InventoryPathIndex.Create(scan,snapshot,cancel.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped,"Index construction observes cancellation");}

            var native=new NativeFake();var backend=new WindowsInventoryRegistryBackend(native);
            Check(backend.Open(new(true,true),out var key)==RegistryReadResult.Success&&key!=null,"Native shim opens fixed local source without touching real registry");
            using(key)
            {
                Check(native.OpenedNames.SequenceEqual(new[]{"Software","Microsoft","Windows","CurrentVersion","Uninstall"})&&native.Rights.All(r=>r==0x109)&&native.Options.All(o=>o==8),"Every path component uses no-follow option and read/enumerate rights with explicit64 view");
                native.Raw=Encoding.Unicode.GetBytes(@"D:\App");Check(key!.ReadText("InstallLocation",65536).Text==@"D:\App","Non-null-terminated REG_SZ decodes without reading past the buffer");
                native.Type=2;native.Raw=Encoding.Unicode.GetBytes(@"%PRIVATE%\App\"+"\0");Check(key!.ReadText("InstallLocation",65536).Text!.Contains("%PRIVATE%"),"REG_EXPAND_SZ is never expanded");native.Type=1;
                int dataCalls=native.DataCalls;native.ForcedSize=100000;Check(key!.ReadText("DisplayName",65536).State==RegistryReadResult.Invalid&&native.DataCalls==dataCalls,"Oversized value rejected before data allocation/read");native.ForcedSize=null;
                native.Raw=Encoding.Unicode.GetBytes("one\0two\0");Check(key!.ReadText("DisplayName",65536).State==RegistryReadResult.Invalid,"Embedded null cannot hide extra registry text");
                native.Raw=[0,216];Check(key!.ReadText("DisplayName",65536).State==RegistryReadResult.Invalid,"Invalid UTF16 is skipped");
                native.Raw=[1];Check(key!.ReadText("DisplayName",65536).State==RegistryReadResult.Invalid,"Odd byte length is rejected before decode");
                native.Raw=Encoding.Unicode.GetBytes("safe");native.DataError=234;Check(key!.ReadText("DisplayName",65536).State==RegistryReadResult.Invalid,"Growing value is skipped without unbounded resize");native.DataError=0;
                native.DataType=3;Check(key!.ReadText("DisplayName",65536).State==RegistryReadResult.Invalid,"Type change between size/data queries is skipped");native.DataType=null;
                native.DataType=2;Check(key!.ReadText("DisplayName",65536).State==RegistryReadResult.Invalid,"REG_SZ changing into REG_EXPAND_SZ is skipped");
                native.Type=2;native.DataType=1;Check(key!.ReadText("DisplayName",65536).State==RegistryReadResult.Invalid,"REG_EXPAND_SZ changing into REG_SZ is skipped");native.Type=1;native.DataType=null;
                int calls=native.SizeCalls;Check(key!.ReadText("UninstallString",65536).State==RegistryReadResult.Invalid&&native.SizeCalls==calls,"Command and unrelated fields are blocked at native reader boundary");
                using var canceled=new CancellationTokenSource();canceled.Cancel();bool stopped=false;try{key!.ReadText("DisplayName",65536,canceled.Token.ThrowIfCancellationRequested);}catch(OperationCanceledException){stopped=true;}Check(stopped,"Cancellation checkpoint stops before OS value read");
            }
            var linkNative=new NativeFake{LinkAt="Microsoft"};Check(new WindowsInventoryRegistryBackend(linkNative).Open(new(false,false),out _) ==RegistryReadResult.Link&&!linkNative.OpenedNames.Contains("Windows"),"Registry symbolic link stops traversal before target components");
            var denied=new NativeFake{OpenError=5};Check(new WindowsInventoryRegistryBackend(denied).Open(new(true,true),out _)==RegistryReadResult.Denied,"Denied source is not a successful empty list");
            var imports=typeof(WindowsInventoryRegistryNative).GetMethods(BindingFlags.NonPublic|BindingFlags.Static).Select(m=>m.GetCustomAttribute<DllImportAttribute>()).OfType<DllImportAttribute>().ToArray();
            Check(imports.Length==4&&imports.All(a=>a.EntryPoint is "RegOpenKeyExW" or "RegQueryValueExW" or "RegEnumKeyExW"),"Production native imports contain only open/query/enumerate, no writes or execution");

            var fakeBackend=new FakeBackend();var provider=new RegistryInventoryProvider(fakeBackend);var result=await provider.ReadAsync(default);
            Check(result.Applications.Count==1&&result.Applications[0].Sources.Split(',').Length==4,"Identical registrations deduplicate across four source views while retaining sources");
            Check(fakeBackend.Fields.Distinct().Order().SequenceEqual(new[]{"DisplayName","DisplayVersion","InstallLocation","Publisher"}.Order()),"Provider requests exactly four allowlisted application fields");
            Check(fakeBackend.Disposals==8&&fakeBackend.Opens==4,"All four source and four entry handles are disposed");
            Check(result.Coverage.Contains("tüm yazılımların eksiksiz listesi değildir")&&!result.Partial,"Completed source collection still disclaims global inventory completeness");
            var limited=await new RegistryInventoryProvider(new FakeBackend(),new(Keys:1)).ReadAsync(default);Check(limited.Sources.Any(s=>s.State==InventoryReadState.Limited)&&limited.Partial,"Key budget exhaustion is partial, not no-registration evidence");
            var bytesLimit=await new RegistryInventoryProvider(new FakeBackend(),new(Bytes:4)).ReadAsync(default);Check(bytesLimit.Applications.Count==0&&bytesLimit.Partial,"Text budget bounds reader and prevents partial-path matching");
            var deniedBackend=new FakeBackend{Denied=true};var failed=await new RegistryInventoryProvider(deniedBackend).ReadAsync(default);Check(!failed.Readable&&failed.Partial&&failed.Applications.Count==0,"All denied sources remain failed and contain no application evidence");
            var missing=await new RegistryInventoryProvider(new FakeBackend{MissingLocation=true}).ReadAsync(default);Check(missing.Applications.Count==0&&missing.Sources.All(s=>s.Unusable==1),"Missing locations are counted without inventing paths");

            var service=new FakeScan(new(scan,[]));var fakeProvider=new FakeProvider(snapshot);using var vm=new WorkspaceViewModel(service,fakeProvider);
            Check(fakeProvider.Calls==0&&!vm.Inventory.CanInspect&&!vm.Inventory.ExportHints,"Startup never reads inventory and export starts off");
            vm.Roots.Add(root.Path);await vm.ScanAsync();await vm.SetViewAsync(WorkspaceView.Review);await vm.SetReviewCollectionAsync(1);vm.ReviewMinimum=0;await vm.RefreshAsync();vm.SelectedRow=editor;
            Check(fakeProvider.Calls==0&&vm.Inventory.CanInspect,"Scan completion and review navigation never auto-read inventory");
            await vm.Inventory.InspectAsync();Check(fakeProvider.Calls==1&&vm.Inventory.HasSnapshot&&vm.Details.FullText.Contains("PRIVATE-APP-CANARY"),"Explicit read enriches current selection without a new scan");
            Check(vm.Details.FullText.Contains("kullanım")&&service.Calls==1,"Enrichment leaves scan count and usage caveat unchanged");
            var offReport=ReportSnapshot.Capture(vm);var off=new StringWriter();offReport.Write(off);Check(!off.ToString().Contains("PRIVATE-APP-CANARY")&&offReport.Inventory==null,"Default CSV contains no collected application names");
            vm.Inventory.ExportHints=true;var onReport=ReportSnapshot.Capture(vm);var on=new StringWriter();onReport.Write(on);Check(on.ToString().Contains("PRIVATE-APP-CANARY")&&!on.ToString().Contains("OUTSIDE")&&!on.ToString().Contains("NOT-OBSERVED"),"Opt-in CSV exports related rows only, not full machine inventory");
            vm.Inventory.Forget();var again=new StringWriter();onReport.Write(again);Check(again.ToString()==on.ToString()&&!vm.Inventory.HasSnapshot&&!vm.Inventory.ExportHints&&!vm.Details.FullText.Contains("PRIVATE-APP-CANARY"),"Forget drops live hints but leaves explicitly captured report immutable");
            fakeProvider.Gate=new(TaskCreationOptions.RunContinuationsAsynchronously);var late=vm.Inventory.InspectAsync();vm.Inventory.Forget();fakeProvider.Gate.SetResult(snapshot);await late;
            Check(!vm.Inventory.HasSnapshot&&!vm.Inventory.IsBusy,"Late provider completion after forget cannot restore inventory");
            fakeProvider.Gate=new(TaskCreationOptions.RunContinuationsAsynchronously);var oldRead=vm.Inventory.InspectAsync();await vm.ScanAsync();fakeProvider.Gate.SetResult(snapshot);await oldRead;
            Check(!vm.Inventory.HasSnapshot&&!vm.Inventory.ExportHints&&fakeProvider.Calls==3,"New scan invalidates in-flight inventory without automatic reread");
            var secretFailure=new FakeProvider(snapshot){Fail=true};using var control=new InventoryController(secretFailure);control.Bind(scan);await control.InspectAsync();Check(!control.HasSnapshot&&!control.Status.Contains("SECRET-FAILURE"),"Provider exception details never leak raw data to UI status");
            log.Add($"ALL CHECKS PASSED ({log.Count})");File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(ex);return 1;}
    }
    sealed class NativeFake:IInventoryRegistryNative
    {
        internal List<string> OpenedNames=[];internal List<uint> Rights=[],Options=[];internal string? LinkAt;internal int OpenError,SizeCalls,DataCalls,DataError;internal uint Type=1;internal uint? DataType,ForcedSize;internal byte[] Raw=Encoding.Unicode.GetBytes("valid\0");long id=100;readonly Dictionary<nint,string> names=[];
        public int Open(SafeRegistryHandle parent,string name,uint options,uint access,out SafeRegistryHandle handle){OpenedNames.Add(name);Rights.Add(access);Options.Add(options);handle=new(new IntPtr(++id),false);names[handle.DangerousGetHandle()]=name;return OpenError;}
        public int QuerySize(SafeRegistryHandle key,string field,out uint type,ref uint size){SizeCalls++;if(field=="SymbolicLinkValue"){type=6;size=20;return names[key.DangerousGetHandle()]==LinkAt?0:2;}type=Type;size=ForcedSize??(uint)Raw.Length;return 0;}
        public int QueryData(SafeRegistryHandle key,string field,out uint type,byte[] data,ref uint size){DataCalls++;type=DataType??Type;Raw.CopyTo(data,0);size=(uint)Raw.Length;return DataError;}
        public int Enumerate(SafeRegistryHandle key,uint index,StringBuilder name,ref uint length)=>259;
    }
    sealed class FakeBackend:IInventoryRegistryBackend
    {
        internal List<string> Fields=[];internal int Disposals,Opens;internal bool Denied,MissingLocation;
        public RegistryReadResult Open(InventorySource source,out IInventoryRegistryKey? key,Action? checkpoint=null){checkpoint?.Invoke();Opens++;key=Denied?null:new Key(this,false);return Denied?RegistryReadResult.Denied:RegistryReadResult.Success;}
        sealed class Key(FakeBackend owner,bool child):IInventoryRegistryKey
        {public RegistryReadResult Name(uint i,out string name){name="record";return i==0?RegistryReadResult.Success:RegistryReadResult.End;}public RegistryReadResult OpenChild(string n,out IInventoryRegistryKey? key,Action? checkpoint=null){checkpoint?.Invoke();key=new Key(owner,true);return RegistryReadResult.Success;}public RegistryText ReadText(string field,int cap,Action? checkpoint=null){checkpoint?.Invoke();owner.Fields.Add(field);if(field=="InstallLocation"&&owner.MissingLocation)return new(RegistryReadResult.Missing);string text=field switch{"InstallLocation"=>@"D:\Apps\Editor","DisplayName"=>"PRIVATE-APP-CANARY","Publisher"=>"fixture","DisplayVersion"=>"1",_=>throw new Exception("Unexpected field")};int bytes=Encoding.Unicode.GetByteCount(text)+2;return bytes>cap?new(RegistryReadResult.Invalid):new(RegistryReadResult.Success,text,bytes);}public void Dispose(){_ = child;owner.Disposals++;}}
    }
    sealed class FakeProvider(InventorySnapshot snapshot):IInventoryProvider
    {internal int Calls;internal bool Fail;internal TaskCompletionSource<InventorySnapshot>? Gate;public Task<InventorySnapshot> ReadAsync(CancellationToken token){Calls++;if(Fail)throw new IOException("SECRET-FAILURE");return Gate?.Task??Task.FromResult(snapshot);}}
    sealed class FakeScan(ScanOutcome outcome):IScanService
    {internal int Calls;public string[] NormalizeRoots(IEnumerable<string> p)=>p.ToArray();public Task<ScanOutcome> ScanAsync(ScanRequest r,IProgress<ScanProgress>? p,CancellationToken t){Calls++;return Task.FromResult(outcome);}}
}
