using System.Diagnostics;
using System.Runtime.InteropServices;

// Developer-only measurements. No system-wide counters, cache flush or tracing.
sealed class Metrics : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] public struct Io { public ulong Reads,Writes,Other,ReadBytes,WriteBytes,OtherBytes; }
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessIoCounters(nint process,out Io value);
    readonly Process process=Process.GetCurrentProcess();
    readonly object gate=new();
    readonly Stopwatch clock=new();
    Timer? timer;
    readonly long allocatedBefore,heapBefore,privateBefore,workingBefore;
    readonly TimeSpan cpuBefore,pauseBefore;
    readonly int[] gcBefore;
    readonly Io ioBefore;
    readonly bool ioAvailable;
    long heapPeak,privatePeak,workingPeak;
    int samples; bool finished;
    internal Metrics()
    {
        Sample();heapBefore=heapPeak;privateBefore=privatePeak;workingBefore=workingPeak;
        gcBefore=Enumerable.Range(0,3).Select(GC.CollectionCount).ToArray();pauseBefore=GC.GetTotalPauseDuration();
        allocatedBefore=GC.GetTotalAllocatedBytes(true);
        cpuBefore=process.TotalProcessorTime;ioAvailable=GetProcessIoCounters(process.Handle,out ioBefore);
        clock.Start();timer=new Timer(_=>Sample(),null,50,50);
    }
    void Sample()
    {
        lock(gate)
        {
            process.Refresh();heapPeak=Math.Max(heapPeak,GC.GetTotalMemory(false));
            privatePeak=Math.Max(privatePeak,process.PrivateMemorySize64);workingPeak=Math.Max(workingPeak,process.WorkingSet64);samples++;
        }
    }
    internal Dictionary<string,object?> Finish()
    {
        clock.Stop();
        var cpu=process.TotalProcessorTime-cpuBefore;
        bool available=GetProcessIoCounters(process.Handle,out var ioAfter)&&ioAvailable;
        timer!.DisposeAsync().AsTask().GetAwaiter().GetResult();timer=null;Sample();
        long allocated=GC.GetTotalAllocatedBytes(true)-allocatedBefore;
        finished=true;
        return new()
        {
            ["wall_ms"]=clock.Elapsed.TotalMilliseconds,["cpu_ms"]=cpu.TotalMilliseconds,
            ["average_cores"]=cpu.TotalSeconds/Math.Max(clock.Elapsed.TotalSeconds,.000001),
            ["allocated_managed_bytes"]=allocated,["managed_start_bytes"]=heapBefore,["managed_sample_peak_bytes"]=heapPeak,
            ["private_start_bytes"]=privateBefore,["private_sample_peak_bytes"]=privatePeak,
            ["working_start_bytes"]=workingBefore,["working_sample_peak_bytes"]=workingPeak,["sample_count"]=samples,["sample_interval_ms"]=50,
            ["gc_counts"]=Enumerable.Range(0,3).Select(i=>GC.CollectionCount(i)-gcBefore[i]).ToArray(),
            ["gc_pause_ms"]=(GC.GetTotalPauseDuration()-pauseBefore).TotalMilliseconds,
            ["process_io_available"]=available,["process_read_bytes"]=available?ioAfter.ReadBytes-ioBefore.ReadBytes:null,
            ["process_write_bytes"]=available?ioAfter.WriteBytes-ioBefore.WriteBytes:null,
            ["process_other_bytes"]=available?ioAfter.OtherBytes-ioBefore.OtherBytes:null,
            ["process_read_operations"]=available?ioAfter.Reads-ioBefore.Reads:null,
            ["process_other_operations"]=available?ioAfter.Other-ioBefore.Other:null,
            ["physical_ssd_bytes_read"]=null
        };
    }
    internal static void Collect(){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
    public void Dispose(){if(!finished)timer?.DisposeAsync().AsTask().GetAwaiter().GetResult();process.Dispose();}
}
