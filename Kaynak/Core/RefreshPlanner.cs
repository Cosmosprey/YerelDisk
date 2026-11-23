namespace YerelDisk;

internal enum RefreshAction { KeepPrevious, MeasureScope, MeasureSelected, MergeSelected }
internal enum RefreshReason
{
    Cancelled, InvalidScope, OutsideScope, Excluded, RootIdentityUnavailable, RootIdentityChanged,
    WholeScope, MissingIndex, IndexMismatch, SelectedUncertain, UnsafeEvidence, SharedOutside,
    MetadataBudget, IdentityBudget, FreshUnavailable, FreshScopeMismatch, SelectedIdentityChanged,
    FreshUncertain, SelectedScanRequired, ReadyToMerge, AllocationFailed, MeasurementHistoryLimit
}
internal sealed class RefreshMemoryLimits
{
    internal static RefreshMemoryLimits Default { get; } = new(128L << 20, 512L << 20, 16L << 20);
    internal long IndexBytes { get; }
    internal long OperationBytes { get; }
    internal long IdentityBytes { get; }
    internal RefreshMemoryLimits(long indexBytes, long operationBytes, long identityBytes)
    {
        RefreshIndex.ValidateBudget(indexBytes);
        if (operationBytes is < 0 or > (2L << 30)) throw new ArgumentOutOfRangeException(nameof(operationBytes));
        if (identityBytes is < 0 or > (64L << 20)) throw new ArgumentOutOfRangeException(nameof(identityBytes));
        IndexBytes=indexBytes;OperationBytes=operationBytes;IdentityBytes=identityBytes;
    }
}
internal sealed class RefreshPlan
{
    internal RefreshAction Action { get; }
    internal RefreshReason Reason { get; }
    internal IReadOnlyList<string> ScopeRoots { get; }
    internal IReadOnlyList<string> Exclusions { get; }
    internal IReadOnlyList<string> SelectedExclusions { get; }
    internal string SelectedPath { get; }
    internal long SelectedIndexBudgetBytes { get; }
    internal long ReplacementIndexBudgetBytes { get; }
    internal long ReservedMetadataBytes { get; }
    internal bool KeepsOutsideMeasurements => Action is RefreshAction.MeasureSelected or RefreshAction.MergeSelected;
    internal string Explanation => Action switch
    {
        RefreshAction.MeasureSelected => "Seçili alan ölçülecek; yeni kimlik ve bütçe bilgileriyle karar tekrar kontrol edilecek.",
        RefreshAction.MergeSelected => "Seçili alanın yeni ölçümü birleştirme için uygun; diğer alanların önceki ölçümü korunacak. Kökler yayımdan önce tekrar doğrulanmalı.",
        RefreshAction.MeasureScope => "Hızlı yenileme için yeterli güvenilir bilgi veya bütçe yok; mevcut tarama kapsamı yeniden ölçülecek.",
        _ => "Yenileme güvenle sürdürülemiyor; önceki sonuç korunacak."
    };
    internal RefreshPlan(RefreshAction action,RefreshReason reason,string[] roots,string[] excluded,string path,string[] selectedExcluded,
        long freshBudget=0,long replacementBudget=0,long reservation=0)
    {
        Action=action;Reason=reason;ScopeRoots=Array.AsReadOnly(roots);Exclusions=Array.AsReadOnly(excluded);
        SelectedPath=path;SelectedExclusions=Array.AsReadOnly(selectedExcluded);
        SelectedIndexBudgetBytes=freshBudget;ReplacementIndexBudgetBytes=replacementBudget;ReservedMetadataBytes=reservation;
    }
}

// Pure metadata policy: never opens paths, scans, changes results or publishes
// rows. The executor must supply identities verified under held root handles
// for EACH phase and revalidate again before commit. A plan is not an I/O pin.
internal static class RefreshPlanner
{
    const long MinimumFreshBudget=64*1024, SortReserve=4096;
    readonly record struct FileId(uint Volume,ulong Id):IComparable<FileId>
    { public int CompareTo(FileId other){int c=Volume.CompareTo(other.Volume);return c!=0?c:Id.CompareTo(other.Id);} }

    internal static RefreshPlan Evaluate(ScanResult previous,FolderEntry selected,IReadOnlyList<RefreshRoot> verifiedRoots,
        RefreshMemoryLimits? limits=null,RefreshIndex? fresh=null,CancellationToken token=default)
    {
        limits ??= RefreshMemoryLimits.Default;
        string[] roots=previous.RootPaths.ToArray(), excluded=previous.ExcludedPaths.ToArray();
        string[] selectedExcluded=excluded.Where(path=>ScanExclusions.SameOrBelow(path,selected.Path)).ToArray();
        RefreshPlan Result(RefreshAction action,RefreshReason reason,long grant=0,long reserve=0)=>new(action,reason,roots,excluded,selected.Path,selectedExcluded,grant,limits.IndexBytes,reserve);
        RefreshPlan Stop(RefreshReason reason)=>Result(RefreshAction.KeepPrevious,reason);
        RefreshPlan Full(RefreshReason reason)=>Result(RefreshAction.MeasureScope,reason);
        if(token.IsCancellationRequested)return Stop(RefreshReason.Cancelled);
        if(fresh?.State==RefreshIndexState.AllocationFailed)return Stop(RefreshReason.AllocationFailed);
        if(roots.Length==0 || roots.Distinct(StringComparer.Ordinal).Count()!=roots.Length || verifiedRoots.Count!=roots.Length)return Stop(RefreshReason.InvalidScope);
        var live=verifiedRoots.ToArray();
        foreach(string root in roots)
        {
            if(token.IsCancellationRequested)return Stop(RefreshReason.Cancelled);
            var known=RootFolder(previous,root);var current=live.Where(r=>r.Path==root).ToArray();
            if(current.Length!=1 || known?.ObservedId is not >0 || previous.RootVolumeIds?.TryGetValue(root,out var guid)!=true
                || guid==Guid.Empty || current[0].Id==0 || current[0].VolumeId is null || current[0].VolumeId==Guid.Empty)
                return Stop(RefreshReason.RootIdentityUnavailable);
            if(current[0].Id!=known.ObservedId || current[0].VolumeId!=guid)return Stop(RefreshReason.RootIdentityChanged);
        }
        if(!Member(previous.Root,selected) || !roots.Any(root=>Below(selected.Path,root)))
        {
            if(ReferenceEquals(previous.Root,selected))return Full(RefreshReason.WholeScope);
            return Stop(RefreshReason.OutsideScope);
        }
        if(excluded.Any(path=>ScanExclusions.SameOrBelow(selected.Path,path)))return Stop(RefreshReason.Excluded);
        if(ReferenceEquals(previous.Root,selected))return Full(RefreshReason.WholeScope);
        if(limits.IndexBytes==0 || previous.RefreshIndex is not{Captured:true} index)return Full(RefreshReason.MissingIndex);
        if(previous.Cancelled||previous.Failed||previous.ScopeChanged||previous.NeedsRetry)return Full(RefreshReason.UnsafeEvidence);
        var indexRoots=index.Roots.ToArray();
        if(indexRoots.Length!=roots.Length || !indexRoots.Select(r=>r.Path).SequenceEqual(roots,StringComparer.Ordinal)
            || !index.Exclusions.SequenceEqual(excluded,StringComparer.Ordinal))return Full(RefreshReason.IndexMismatch);
        foreach(var cached in indexRoots)
        {
            if(token.IsCancellationRequested)return Stop(RefreshReason.Cancelled);
            var current=live.Single(r=>r.Path==cached.Path);
            if(cached.Id!=current.Id || cached.VolumeId!=current.VolumeId || cached.Volume!=current.Volume)return Full(RefreshReason.IndexMismatch);
            if(!FindDirectory(index,cached.Path,token,out var data) || !IdentityMatches(data,cached))
                return token.IsCancellationRequested?Stop(RefreshReason.Cancelled):Full(RefreshReason.IndexMismatch);
        }
        if(!FindDirectory(index,selected.Path,token,out var target) || target.Id==0 || target.Id!=selected.ObservedId || !previous.CanUsePath(selected.Path))
            return token.IsCancellationRequested?Stop(RefreshReason.Cancelled):Full(RefreshReason.SelectedUncertain);
        var rootForTarget=indexRoots.Single(r=>Below(selected.Path,r.Path));
        if(target.OwnStreams.Volume!=rootForTarget.Volume)return Full(RefreshReason.IndexMismatch);
        if(!Inspect(index,selected.Path,false,token,out int selectedCount))return token.IsCancellationRequested?Stop(RefreshReason.Cancelled):Full(RefreshReason.UnsafeEvidence);

        // Retained metadata reservation: old snapshot + selected capture +
        // maximum replacement snapshot + identity workspace. Not a process RAM
        // cap; native scan/rebuild scratch and result objects need executor policy.
        long scopeBytes=SortReserve+8L*(roots.Length+excluded.Length+selectedExcluded.Length);
        if(index.MetadataBytes>limits.IndexBytes)return Full(RefreshReason.MetadataBudget);
        long fixedBytes=index.MetadataBytes+limits.IndexBytes+limits.IdentityBytes+scopeBytes;
        long grant=Math.Min(limits.IndexBytes,limits.OperationBytes-fixedBytes);
        if(grant<MinimumFreshBudget)return Full(RefreshReason.MetadataBudget);
        long reserve=fixedBytes+grant;
        int freshCount=0;
        if(fresh!=null)
        {
            if(fresh.State==RefreshIndexState.Cancelled)return Stop(RefreshReason.Cancelled);
            if(!fresh.Captured)return Full(RefreshReason.FreshUnavailable);
            if(fresh.BudgetBytes>grant || fresh.MetadataBytes>grant)return Full(RefreshReason.MetadataBudget);
            var freshRoots=fresh.Roots.ToArray();
            if(freshRoots.Length!=1 || freshRoots[0].Path!=selected.Path
                || !fresh.Exclusions.SequenceEqual(selectedExcluded,StringComparer.Ordinal))return Stop(RefreshReason.FreshScopeMismatch);
            var freshRoot=freshRoots[0];
            if(freshRoot.VolumeId!=rootForTarget.VolumeId || freshRoot.Volume!=rootForTarget.Volume)return Stop(RefreshReason.RootIdentityChanged);
            if(freshRoot.Id!=target.Id)return Full(RefreshReason.SelectedIdentityChanged);
            if(!FindDirectory(fresh,selected.Path,token,out var freshDirectory) || !IdentityMatches(freshDirectory,freshRoot))
                return token.IsCancellationRequested?Stop(RefreshReason.Cancelled):Full(RefreshReason.IndexMismatch);
            if(fresh.HasUncertainty || !Inspect(fresh,selected.Path,true,token,out freshCount))return token.IsCancellationRequested?Stop(RefreshReason.Cancelled):Full(RefreshReason.FreshUncertain);
            // Upper bound: old + fresh already includes the replaced area, so
            // no optimistic subtraction of stale observations is needed.
            if(index.MetadataBytes+fresh.MetadataBytes>limits.IndexBytes)return Full(RefreshReason.MetadataBudget);
        }
        long idCount=(long)selectedCount+freshCount;
        long idBytes=idCount==0?0:32+16*idCount+SortReserve;
        if(idBytes>limits.IdentityBytes || idCount>int.MaxValue)return Full(RefreshReason.IdentityBudget);
        try
        {
            var ids=idCount==0?Array.Empty<FileId>():new FileId[(int)idCount];int pos=0;
            foreach(var item in Entries(index))
            {
                if(token.IsCancellationRequested)return Stop(RefreshReason.Cancelled);
                if(Below(item.Owner.Data.Path,selected.Path))ids[pos++]=new(item.File.Volume,item.File.Id);
            }
            if(fresh!=null)foreach(var file in fresh.Files)
            {if(token.IsCancellationRequested)return Stop(RefreshReason.Cancelled);ids[pos++]=new(file.Volume,file.Id);}
            if(pos!=ids.Length)return Full(RefreshReason.IndexMismatch);
            Array.Sort(ids);
            foreach(var item in Entries(index))
            {
                if(token.IsCancellationRequested)return Stop(RefreshReason.Cancelled);
                if(!Below(item.Owner.Data.Path,selected.Path)&&Array.BinarySearch(ids,new FileId(item.File.Volume,item.File.Id))>=0)return Full(RefreshReason.SharedOutside);
            }
            if(token.IsCancellationRequested)return Stop(RefreshReason.Cancelled);
            return Result(fresh==null?RefreshAction.MeasureSelected:RefreshAction.MergeSelected,
                fresh==null?RefreshReason.SelectedScanRequired:RefreshReason.ReadyToMerge,grant,reserve);
        }
        catch(OutOfMemoryException){return Stop(RefreshReason.AllocationFailed);}
        catch(InvalidDataException){return Full(RefreshReason.IndexMismatch);}
    }

    static bool IdentityMatches(RefreshDirectoryData data,RefreshRoot root)=>data.Id==root.Id
        && data.OwnStreams.Id==root.Id && data.OwnStreams.Volume==root.Volume;
    static bool FindDirectory(RefreshIndex index,string path,CancellationToken token,out RefreshDirectoryData found)
    {
        found=default;int count=0;
        foreach(var dir in index.Directories)
        {if(token.IsCancellationRequested)return false;if(dir.Data.Path==path){found=dir.Data;if(++count>1)return false;}}
        return count==1;
    }
    static bool Inspect(RefreshIndex index,string selected,bool onlySelected,CancellationToken token,out int selectedCount)
    {
        selectedCount=0;
        foreach(var dir in index.Directories)
        {
            if(token.IsCancellationRequested)return false;
            bool inside=Below(dir.Data.Path,selected);
            if(onlySelected&&!inside)return false;
            if(inside)
            {
                if(dir.Data.Measurement!=MeasurementState.Measured || !dir.Data.Enumerated || !dir.Data.OwnStreams.StreamsKnown
                    || dir.Data.IssueCount!=0 || !string.IsNullOrEmpty(dir.Data.OwnStreams.StreamError))return false;
                selectedCount+=dir.FileCount;
            }
            else if(dir.Data.Measurement is MeasurementState.Unknown or MeasurementState.Previous || !dir.Data.Enumerated)
            {
                // A known skipped zero-observation row can stay explicitly
                // unknown outside the refreshed area; it is never followed.
                if(dir.FileCount!=0 || dir.Data.FileCount!=0 || dir.Data.LogicalBytes!=0 || dir.Data.AllocatedBytes!=0)return false;
            }
            else if(!dir.Data.OwnStreams.StreamsKnown)return false;
        }
        foreach(var file in index.Files)
        {if(token.IsCancellationRequested || file.Unverified || file.Id==0 || !file.Allocated.HasValue)return false;}
        return true;
    }
    static IEnumerable<(RefreshDirectory Owner,FileObservation File)> Entries(RefreshIndex index)
    {
        using var files=index.Files.GetEnumerator();int position=0;
        foreach(var directory in index.Directories)
        {
            if(directory.FileStart!=position)throw new InvalidDataException();
            for(int i=0;i<directory.FileCount;i++)
            {if(!files.MoveNext())throw new InvalidDataException();position++;yield return(directory,files.Current);}
        }
        if(position!=index.FileCount || files.MoveNext())throw new InvalidDataException();
    }
    static bool Member(FolderEntry root,FolderEntry selected)
    {
        int depth=0;
        for(FolderEntry? p=selected;p!=null;p=p.Parent)
        {
            if(ReferenceEquals(p,root))return true;
            if(p.Parent is{} owner && !owner.Children.Contains(p))return false;
            if(++depth>32768 || (p.Parent is{} parent && !ReferenceEquals(parent,root) && parent.Path.Length>=p.Path.Length))return false;
        }
        return false;
    }
    static FolderEntry? RootFolder(ScanResult scan,string path)=>scan.Root.Path==path?scan.Root:scan.Root.Children.FirstOrDefault(f=>f.Path==path);
    // Selection paths are copied from the observed tree; Ordinal avoids merging
    // distinct names in case-sensitive directories. Exclusions keep their own
    // existing case-insensitive policy separately.
    internal static bool Below(string path,string parent)
    {int n=parent.Length;while(n>0&&parent[n-1]=='\\')n--;return path.AsSpan().StartsWith(parent.AsSpan(0,n),StringComparison.Ordinal)&&(path.Length==n||(path.Length>n&&path[n]=='\\'));}
}
