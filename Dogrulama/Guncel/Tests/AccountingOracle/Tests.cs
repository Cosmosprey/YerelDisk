using YerelDisk;

// Expected values come from the declared fixture, never from a native scan,
// RefreshRebuild, ObservationAggregate, or another production sum.
static class Tests
{
    sealed record Spec(int Owner, string Name, long Bytes, long? Allocation, uint Volume, ulong Id,
        bool Linked = false, int Streams = 0, long StreamBytes = 0, bool Unverified = false);
    static readonly List<string> log = [];
    static Spec File(string name, long bytes, long? allocation, ulong id, int owner = 0, uint volume = 1, bool linked = false)
        => new(owner, name, bytes, allocation, volume, id, linked);
    static int Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        try
        {
            Case("shared identity", [File("one",123,4096,10,linked:true),File("alias",123,4096,10,1,linked:true)]);
            Case("different disks same identifier", [File("one",10,4096,1),File("two",20,8192,1,1,volume:2)]);
            Case("zero identity is never deduplicated", [File("one",10,4096,0),File("two",20,8192,0,1)]);
            Case("unknown allocation is not fabricated", [File("unknown",500,null,1),File("known",100,4096,2,1)]);
            Case("zero-byte file is still counted", [File("empty",0,0,1),File("nonempty",1,4096,2)]);
            Case("top-K cannot change totals", [File("a",90,4096,1),File("b",10,4096,2),File("c",50,4096,3,1)], limit:1);
            Case("more list slots than files", [File("a",5,4096,1)], limit:10);
            Case("equal-size candidates retain the largest sizes", [File("a",12,4096,1),File("b",12,4096,2),File("c",1,4096,3)], limit:2);
            Case("empty owners retain own streams", [], ownBytes:7, ownAllocation:4096);
            Case("directory streams are independent", [File("a",10,4096,1)], ownBytes:3, ownAllocation:8192);
            Case("file streams are already included in size", [new(0,"mail.pst",103,8192,1,1,true,1,3),new(1,"alias.pst",103,8192,1,1,true,1,3)]);
            Case("unverified observations retain their flag", [new(0,"uncertain",50,4096,1,1,false,0,0,true)]);
            Case("reversed owners do not alter global accounting", [File("alias",13,4096,1,1,linked:true),File("one",13,4096,1,linked:true),File("other",99,8192,2)], reverse:true);
            for (int seed = 0; seed < 64; seed++)
            {
                var random = new Random(8401 + seed);
                var files = new List<Spec>();
                for (int i = 1; i <= 80; i++)
                {
                    long bytes = random.Next(0,10000); long? allocation = i % 9 == 0 ? null : ((bytes + 4095) / 4096) * 4096;
                    uint volume = (uint)(1 + random.Next(2)); ulong id = i % 11 == 0 ? 0 : (ulong)i;
                    bool linked = i % 7 == 0 && id != 0;
                    files.Add(File("file-"+i, bytes, allocation, id, random.Next(2), volume, linked));
                    if (linked) files.Add(File("alias-"+i, bytes, allocation, id, random.Next(2), volume, true));
                }
                var shuffled = files.OrderBy(_ => random.Next()).ToArray();
                Case("seed-"+seed, shuffled, 1+seed%17, seed%2==0, ownBytes:seed%5, ownAllocation:4096);
            }
            // A moved file observed twice without hard-link evidence is stale, not a valid alias.
            var stale = Run([File("old",12,4096,1),File("new",12,4096,1,1)]);
            Require(stale.Stale.Count == 2 && stale.Scan.HardLinkAliases == 0, "Duplicate unlinked observations must flag both owners as stale");
            log.Add("PASS Unlinked duplicate observations are not accepted as healthy hard links");
            Require(stale.Folders.Sum(f=>f.LogicalBytes)==12 && stale.Folders.Sum(f=>f.FileCount)==1 && stale.Scan.Files.Count==1,
                "Moved identity must contribute once to logical bytes, count and retained list");
            Require(stale.Scan.Files.Single().Unverified && stale.Scan.FileCandidates!.Single().File.Unverified,
                "Representative moved observation remains explicitly unverified in list and index input");
            log.Add("PASS Moved identity is counted once and remains uncertain");
            foreach(bool reverse in new[]{false,true})
            {
                var moved=Run([File("small",3,null,1),File("large",20,8192,1,1),File("other",7,4096,2)],limit:2,reverse:reverse);
                Require(moved.Folders.Sum(f=>f.LogicalBytes)==27 && moved.Folders.Sum(f=>f.FileCount)==2,
                    "Uncertain allocation and traversal order cannot double-count a moved identity");
                Require(moved.Scan.Files.Select(f=>f.Size).SequenceEqual(new long[]{20,7}) && moved.Scan.Files[0].Unverified,
                    "Largest uncertain observation is retained once without evicting a genuine top-K file");
                log.Add("PASS Moved representative and top-K independent of traversal order: "+reverse);
            }
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); bool threw = false;
                try { Run([File("a",1,4096,1)], token:cancelled.Token); } catch (OperationCanceledException) { threw=true; }
                Require(threw,"Accounting must honor cancellation"); log.Add("PASS Cancelled accounting never publishes successful totals");
            }
            long before=GC.GetAllocatedBytesForCurrentThread();
            var tiny=Run([File("tiny",1,4096,1)],limit:1_000_000);
            Require(GC.GetAllocatedBytesForCurrentThread()-before<1_048_576,"Small input with a huge list limit must not reserve the whole limit");
            GC.KeepAlive(tiny.Scan);log.Add("PASS Large configured list limit does not cause oversized allocation for one file");
            before=GC.GetAllocatedBytesForCurrentThread();var empty=Run([],limit:1_000_000);
            Require(GC.GetAllocatedBytesForCurrentThread()-before<1_048_576,"Empty owners must not reserve a large candidate array");
            GC.KeepAlive(empty.Scan);log.Add("PASS Empty owners do not eagerly reserve the configured list limit");
            log.Add($"ALL CHECKS PASSED ({log.Count})"); return 0;
        }
        catch (Exception ex) { log.Add("FAIL " + ex); return 1; }
        finally { System.IO.File.WriteAllLines(Path.Combine(args[0],"results.txt"),log); foreach(string line in log)Console.WriteLine(line); }
    }
    static void Require(bool condition,string message) { if (!condition) throw new Exception(message); }
    static void Case(string name, Spec[] files, int limit=3, bool reverse=false, long ownBytes=0, long ownAllocation=0)
    {
        var result = Run(files,limit,reverse,ownBytes,ownAllocation);
        // GroupBy on declared keys is deliberately different from the production
        // online dictionary, folder traversal, priority queue and native path.
        long expectedAllocation = ownAllocation + files.Select((file,index)=>(file,index))
            .Where(x=>x.file.Allocation.HasValue)
            .GroupBy(x=>(x.file.Volume,x.file.Id,UnknownId:x.file.Id==0?x.index:-1))
            .Sum(group=>group.First().file.Allocation!.Value);
        Require(result.Folders.Sum(f=>f.AllocatedBytes)==expectedAllocation,"Reference allocation mismatch: "+name);
        Require(result.Folders.Sum(f=>f.LogicalBytes)==ownBytes+files.Sum(f=>f.Bytes),"Reference logical size mismatch: "+name);
        Require(result.Folders.Sum(f=>f.FileCount)==files.Length,"Reference file count mismatch: "+name);
        long expectedAliases=files.Where(f=>f.Allocation.HasValue&&f.Id!=0).GroupBy(f=>(f.Volume,f.Id)).Sum(group=>(long)group.Count()-1);
        Require(result.Scan.HardLinkAliases==expectedAliases,"Reference alias count mismatch: "+name);
        Require(result.Scan.Files.Select(f=>f.Size).SequenceEqual(files.Select(f=>f.Bytes).OrderDescending().Take(limit)),"Reference top-K size mismatch: "+name);
        foreach(var retained in result.Scan.Files)
        {
            var spec=files.Single(f=>f.Name==retained.Name);
            Require(retained.ExtraStreams==spec.Streams&&retained.ExtraStreamBytes==spec.StreamBytes&&retained.Unverified==spec.Unverified,"Retained risk/stream fields differ: "+name);
        }
        Require(result.Stale.Count==0,"Trusted fixture unexpectedly reported stale evidence: "+name);
        log.Add("PASS Independent accounting: "+name);
    }
    static (ScanResult Scan,FolderEntry[] Folders,List<string?> Stale) Run(Spec[] files,int limit=3,bool reverse=false,long ownBytes=0,long ownAllocation=0,CancellationToken token=default)
    {
        var root=new FolderEntry(@"C:\declared-fixture",null);
        var folders=new[]{new FolderEntry(root.Path+@"\a",root),new FolderEntry(root.Path+@"\b",root)};root.Children.AddRange(folders);
        var scan=new ScanResult(root);var stale=new List<string?>();
        var owners=folders.Select((folder,index)=>new ObservationOwner(folder,
            new FileMetadata(index==0?ownBytes:0,index==0?ownAllocation:0,1,1,(ulong)(900+index),FileAttributes.Directory,index==0&&ownBytes>0?1:0,index==0?ownBytes:0,true,""),
            files.Where(file=>file.Owner==index).Select(file=>new FileObservation(file.Name,file.Bytes,file.Allocation,DateTime.MinValue,file.Volume,file.Id,file.Linked,file.Streams,file.StreamBytes,file.Unverified,"")),files.Count(file=>file.Owner==index))).ToArray();
        ObservationAggregate.Apply(scan,reverse?owners.Reverse():owners,limit,0,(_,name)=>stale.Add(name),token);
        return (scan,folders,stale);
    }
}
