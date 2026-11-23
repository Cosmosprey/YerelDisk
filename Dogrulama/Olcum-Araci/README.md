# Controlled resource measurement

Developer tool: PowerShell 7, SDK, and a framework-dependent developer build
(`Kaynak/Derleme`). It does not add continuous monitoring to the app.

```powershell
.\Kaynak\Derle.ps1
& .\Dogrulama\Olcum-Araci\Run.ps1
& .\Dogrulama\Olcum-Araci\Run.ps1 -CompareProductDirectory 'candidate-build' -Workers 4 -Workloads many -Operations scan,selected-small,selected-half,remeasure
```

Default fixtures: 50,000 small real files / 1,013 folders and 32 sparse files
with 1 GiB logical size each. Sparse ranges are unallocated; this is not a real
32 GiB payload I/O test. Small files contain 1–31 bytes; filesystem metadata and
builds consume additional space. Only a newly generated own-fixtures tree is
created, and only workloads used by the selected cases are generated.
For example, `-Workloads many` creates no sparse files; sparse-only creates no
many-file tree. A shared warmup uses 100 tiny files. Reused fixtures must contain
the requested workloads. Summary counts come from the generated dataset; failed
generation does not claim a complete dataset. Recycle Bin and Explorer can show
the sparse files' logical 32 GiB even though their data ranges are unallocated.
The fixture tree lives under `Dogrulama/Olcum-Kosular`.
Personal drives, registry inventories and global
cache flushing are outside the tool. Generation/controlled edits occur outside
the timed operation and fixture sizes are restored afterward.

Cases compare 1/4/8 workers and index off/128 MiB, plus selected 10%/50% refresh,
root refresh, missing-index fallback and full replacement. Each sample uses a
fresh process and small code-path warmup. Filesystem cache is not flushed or
controlled; system load/JIT/cache effects can remain. Three rounds reverse order;
paired products also alternate order on the same dataset. Count/size/scope/time
provenance must match. Complete retained lists also match path/field/hint hashes;
truncated lists compare sizes because equal-size membership can depend on parallel
arrival order. `-FileLimit 10000` checks a smaller retained list.

Counters distinguish wall time, total process CPU, managed allocation/GC, and
50 ms sampled heap/private/working-set peaks. Sampling can miss brief peaks and
adds its own CPU/allocation cost. Forced GC occurs outside timed operations.
Retained heap is measured with the model alive. Weak-reference checks after a
brief delay and GC are not a general leak proof. Native phase timings describe
the subscan and do not partition all replay/service/model work. WPF rendering,
physical DPI, cold-cache disks and hardware variety are not measured.

Process I/O accounting is not physical SSD/NAND traffic; read/other counters are
separate and physical_ssd_bytes_read is null. Index MetadataBytes is conservative
payload accounting, not a process-RAM cap or exact live heap measurement.

- [Windows I/O counters](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprocessiocounters)
- [Managed allocation counter](https://learn.microsoft.com/en-us/dotnet/api/system.gc.gettotalallocatedbytes)
- [Sparse file control](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_set_sparse)

Products and probe sources are hashed. JSON/CSV/logs remain local. Successful
fixtures are recycled unless KeepFixtures is enabled; failures remain for diagnosis.
ExistingFixtureRun accepts only this tool's owned, marker/size-checked run IDs.
A fixture lock prevents concurrent mutation. A reused fixture is also recycled
after success unless kept. Filter combinations producing no cases are rejected.
Run directories and raw local paths must not enter source commits or Releases.
