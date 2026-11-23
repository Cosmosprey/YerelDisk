using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using YerelDisk;
using YerelDisk.Presentation;

static class Probe
{
    sealed record Dataset(string Token,int Files,long Bytes,int Directories,int SparseFiles,long SparseBytes);
    static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle file,uint code,nint input,uint inputSize,nint output,uint outputSize,out uint returned,nint overlapped);
    static void Save(string path,object value)=>File.WriteAllText(path,JsonSerializer.Serialize(value,Json));
    static void Require(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
    static async Task<int> Main(string[] args)
    {
        try
        {
            string mode=args[0],root=Path.GetFullPath(args[1]),token=args[2];
            Require(Path.GetFileName(root)=="own-fixtures"&&Guid.TryParseExact(token,"N",out _),"Only a fresh owned fixture tree is supported");
            if(mode=="generate"){Generate(root,token,int.Parse(args[3]),args.Length>4?args[4].Split(','):["many","sparse"]);Console.WriteLine("PASS generated bounded fixtures for requested workloads");return 0;}
            for(string? p=root;p!=null;p=Path.GetDirectoryName(p))Require((File.GetAttributes(p)&FileAttributes.ReparsePoint)==0,"Fixture ancestors cannot be links");
            var data=JsonSerializer.Deserialize<Dataset>(File.ReadAllText(Path.Combine(root,"dataset.json")))!;
            Require(data.Token==token,"Fixture marker differs");
            string workload=args[3],operation=args[4];int workers=int.Parse(args[5]),cache=int.Parse(args[6]);
            Require(workload is "many" or "sparse" && operation is "scan" or "selected-small" or "selected-half" or "whole" or "missing" or "remeasure","Unknown case");
            Require(operation=="scan"||workload=="many","Refresh cases require the declared many-file tree");
            var service=new TrackingRefresh();
            using var model=new WorkspaceViewModel(new NativeScanService(),new EmptyInventory(),service){Workers=workers,FileLimit=args.Length>8?int.Parse(args[8]):100_000,RefreshCacheMiB=cache};
            // Warm the chosen code paths in the fresh child process, without a global cache flush.
            model.Roots.Add(Path.Combine(root,"warmup"));await model.ScanAsync();
            model.Roots.Clear();model.Roots.Add(Path.Combine(root,workload));
            string inside=Path.Combine(root,"many","half0","part00","d0000","asset-0000.bin");
            string outside=Path.Combine(root,"many","half1","part09","d0000","asset-0000.bin");
            WeakReference? old=null;long baselineIndex=0;byte[] original=[0];
            bool changed=false;
            try
            {
                if(operation!="scan")
                {
                    if(operation=="missing")model.RefreshCacheMiB=0;
                    await model.ScanAsync();Validate(model,data,workload,0,false);
                    baselineIndex=model.Scan!.RefreshIndex?.MetadataBytes??0;old=new WeakReference(model.Scan);
                    model.RefreshCacheMiB=128;
                    File.WriteAllBytes(inside,new byte[18]);File.WriteAllBytes(outside,new byte[9]);changed=true;
                    string selected=operation=="selected-half"?Path.Combine(root,"many","half0"):Path.Combine(root,"many","half0","part00");
                    if(operation=="whole")model.SelectedRow=null;
                    else if(operation!="remeasure")Require(await model.InspectComparisonFolderAsync(model.Scan!,selected),"Selected folder navigation failed");
                    model.SelectedRow=null;
                    // An opened subfolder is the target when there is no selected row.
                }
                Metrics.Collect();
                using var metrics=new Metrics();
                if(operation=="scan")await model.ScanAsync();else if(operation=="remeasure")await model.RemeasureAsync();else await model.RefreshSelectedAsync();
                var row=metrics.Finish();
                bool mixed=operation is "selected-small" or "selected-half";
                Validate(model,data,workload,operation=="scan"?0:mixed?17:25,mixed);
                if(mixed)Require(service.Action==RefreshAction.MergeSelected,"The selected case silently fell back to full scope");
                if(operation=="whole")Require(service.Reason==RefreshReason.WholeScope,"Expected whole-scope reason");
                if(operation=="missing")Require(service.Reason==RefreshReason.MissingIndex,"Expected missing-index reason");
                var result=model.Scan!;
                row["workload"]=workload;row["operation"]=operation;row["workers"]=workers;row["index_mib"]=cache;
                row["file_limit"]=model.FileLimit;
                row["files"]=result.Root.FileCount;row["folders"]=result.Folders;row["logical_bytes"]=result.Root.LogicalBytes;
                row["allocated_bytes"]=result.Root.AllocatedBytes;row["retained_files"]=result.Files.Count;row["mixed"]=mixed;
                row["refresh_action"]=operation is "scan" or "remeasure"?null:service.Action?.ToString();row["refresh_reason"]=operation is "scan" or "remeasure"?null:service.Reason?.ToString();
                row["baseline_index_bytes"]=baselineIndex;row["retained_index_bytes"]=result.RefreshIndex?.MetadataBytes??0;row["index_state"]=result.RefreshIndex?.State.ToString()??"Off";
                row["native_initial_ms"]=result.Timings?.InitialScan.TotalMilliseconds;row["native_verification_ms"]=result.Timings?.Verification.TotalMilliseconds;
                row["native_repair_ms"]=result.Timings?.Refresh.TotalMilliseconds;
                using(var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    foreach(var file in result.Files.OrderBy(f=>f.Path,StringComparer.Ordinal))
                        digest.AppendData(Encoding.UTF8.GetBytes($"{file.Path}|{file.Size}|{file.Allocated}|{file.HardLinked}|{file.Unverified}|{file.Hint}\n"));
                    row["path_digest"]=Convert.ToHexString(digest.GetHashAndReset());
                    foreach(var size in result.Files.Select(f=>f.Size).OrderDescending())digest.AppendData(BitConverter.GetBytes(size));
                    row["size_digest"]=Convert.ToHexString(digest.GetHashAndReset());
                }
                // Allow pending Progress callbacks to drain before assessing retained generations.
                await Task.Delay(150);Metrics.Collect();
                row["retained_managed_bytes"]=GC.GetTotalMemory(true);row["old_result_alive_after_gc"]=old?.IsAlive;
                row["correctness"]=true;Save(args[7],row);GC.KeepAlive(model);
                Console.WriteLine($"PASS {workload}/{operation} workers={workers} cache={cache}");return 0;
            }
            finally{if(changed){File.WriteAllBytes(inside,original);File.WriteAllBytes(outside,original);}}
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static void Validate(WorkspaceViewModel model,Dataset data,string workload,long delta,bool mixed)
    {
        var result=model.Scan;Require(!model.HasError&&result!=null,"Application measurement failed");
        Require(!result!.Cancelled&&!result.Failed&&!result.ScopeChanged&&!result.NeedsRetry&&!result.HasUnverified&&result.IssueCount==0,"Measurement was incomplete");
        Require(result.Root.FileCount==(workload=="many"?data.Files:data.SparseFiles)&&result.Root.LogicalBytes==(workload=="many"?data.Bytes:data.SparseBytes)+delta,"Declared count or byte total differs");
        Require(result.Files.Count==Math.Min(model.FileLimit,result.Root.FileCount),"Retained list count differs");
        Require(result.HasMixedMeasurementTimes==mixed&&result.FileCandidates==null&&result.PhysicalLinks.Count==0,"Provenance/temporary-state release differs");
    }
    static void Generate(string root,string token,int perDirectory,string[] workloads)
    {
        Require(!Directory.Exists(root)&&perDirectory is >=1 and <=100,"Fresh bounded fixture required");
        Require(workloads.Length is >=1 and <=2&&workloads.Distinct(StringComparer.Ordinal).Count()==workloads.Length&&workloads.All(x=>x is "many" or "sparse"),"Only declared workloads may be generated");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root,"warmup"));for(int i=0;i<100;i++)File.WriteAllBytes(Path.Combine(root,"warmup",i+".bin"),[1]);
        long bytes=0;int files=0;
        if(workloads.Contains("many"))for(int part=0;part<10;part++)for(int d=0;d<100;d++)
        {
            string dir=Path.Combine(root,"many","half"+(part/5),"part"+part.ToString("D2"),"d"+d.ToString("D4"));Directory.CreateDirectory(dir);
            for(int f=0;f<perDirectory;f++){int size=f%31+1;File.WriteAllBytes(Path.Combine(dir,"asset-"+f.ToString("D4")+".bin"),new byte[size]);files++;bytes+=size;}
        }
        int sparseFiles=workloads.Contains("sparse")?32:0;const long sizePerFile=1L<<30;
        string sparse=Path.Combine(root,"sparse");if(sparseFiles>0)Directory.CreateDirectory(sparse);
        for(int i=0;i<sparseFiles;i++)
        {
            using var stream=new FileStream(Path.Combine(sparse,i+".bin"),FileMode.CreateNew,FileAccess.Write,FileShare.None);
            if(!DeviceIoControl(stream.SafeFileHandle,0x000900C4,0,0,0,0,out _,0))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            stream.SetLength(sizePerFile);
        }
        Save(Path.Combine(root,"dataset.json"),new Dataset(token,files,bytes,files>0?1013:0,sparseFiles,sparseFiles*sizePerFile));
    }
    sealed class EmptyInventory:IInventoryProvider
    {public Task<InventorySnapshot> ReadAsync(CancellationToken token)=>throw new InvalidOperationException("Inventory must not be read by this benchmark");}
    sealed class TrackingRefresh:IRefreshService
    {
        readonly NativeRefreshService native=new();internal RefreshAction? Action;internal RefreshReason? Reason;
        public async Task<IPreparedRefresh> PrepareAsync(ScanResult old,FolderEntry selected,ScanRequest request,IProgress<ScanProgress>? progress,CancellationToken token)
        {var lease=await native.PrepareAsync(old,selected,request,progress,token);Action=lease.Action;Reason=lease.Reason;return lease;}
    }
}
