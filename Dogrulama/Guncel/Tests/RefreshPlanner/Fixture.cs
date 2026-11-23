using YerelDisk;

static class Fixture
{
    internal static readonly Guid Volume=Guid.Parse("6b957104-63e5-48ba-84c9-e299aa9a89d0");
    internal static readonly RefreshMemoryLimits Limits=new(1024*1024,4*1024*1024,64*1024);
    internal sealed record Tree(ScanResult Scan,FolderEntry Selected,RefreshRoot[] Live);
    internal static FileObservation File(ulong id,uint volume=7)=>new("f"+id+".bin",(long)id+1,4096,DateTime.MinValue,volume,id,false,0,0,false,"");
    internal static Tree Old(ulong inside=1,ulong outside=3,string outsideName="b",Action<FolderEntry,FolderEntry,FolderEntry>? configure=null,int count=1,string[]? excluded=null)
    {
        var root=new FolderEntry(@"C:\scope",null){ObservedId=10};var a=new FolderEntry(root.Path+@"\a",root){ObservedId=20};var b=new FolderEntry(root.Path+"\\"+outsideName,root){ObservedId=30};root.Children.AddRange([a,b]);
        configure?.Invoke(root,a,b);
        var files=Enumerable.Range(0,count).Select(i=>(a,File(inside+(ulong)i))).Append((b,File(outside))).ToArray();
        var scan=Seal(root,files,excluded??[],1024*1024);return new(scan,a,[new(root.Path,7,root.ObservedId,Volume)]);
    }
    internal static RefreshIndex Fresh(string path=@"C:\scope\a",ulong rootId=20,ulong fileId=2,int count=1,long budget=1024*1024,string[]? excluded=null,uint serial=7,Guid? volume=null,Action<FolderEntry>? configure=null)
    {
        var root=new FolderEntry(path,null){ObservedId=rootId};configure?.Invoke(root);
        return Seal(root,Enumerable.Range(0,count).Select(i=>(root,File(fileId+(ulong)i,serial))).ToArray(),excluded??[],budget,serial,volume??Volume).RefreshIndex!;
    }
    internal static ScanResult Seal(FolderEntry root,(FolderEntry Owner,FileObservation File)[] files,string[] excluded,long budget,uint serial=7,Guid? volume=null,uint? cachedSerial=null,Guid? cachedGuid=null)
    {
        var nodes=Walk(root).ToArray();foreach(var f in nodes){f.FileCount=0;f.LogicalBytes=0;f.AllocatedBytes=0;}
        foreach(var entry in files)for(var f=entry.Owner;f!=null;f=f.Parent){f.FileCount++;f.LogicalBytes+=entry.File.Size;f.AllocatedBytes+=entry.File.Allocated??0;}
        var scan=new ScanResult(root){Folders=nodes.Length,ExcludedPaths=excluded,RootVolumeIds=new Dictionary<string,Guid>{{root.Path,volume??Volume}},FileCandidates=files};
        scan.RefreshDirectoryCandidates=nodes.Select(f=>(f,new RefreshDirectoryData(f.Path,f.Parent?.Path,f.ObservedId,new(0,0,1,serial,f.ObservedId,FileAttributes.Directory,0,0,true,""),default,f.Measurement,f.Measurement is not MeasurementState.Unknown and not MeasurementState.Previous,f.LogicalBytes,f.AllocatedBytes,f.FileCount,f.IssueCount)));
        scan.RefreshIndex=RefreshIndex.Capture([scan],[new(root.Path,cachedSerial??serial,root.ObservedId,cachedGuid??volume??Volume)],excluded,budget);
        scan.FileCandidates=null;scan.RefreshDirectoryCandidates=null;return scan;
    }
    internal static IEnumerable<FolderEntry> Walk(FolderEntry root){yield return root;foreach(var c in root.Children)foreach(var f in Walk(c))yield return f;}
    internal static RefreshPlan Plan(Tree tree,RefreshIndex? fresh=null,RefreshMemoryLimits? limits=null,CancellationToken token=default)=>RefreshPlanner.Evaluate(tree.Scan,tree.Selected,tree.Live,limits??Limits,fresh,token);
}
