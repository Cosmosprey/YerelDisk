namespace YerelDisk;

internal readonly record struct ObservationOwner(FolderEntry Folder, FileMetadata OwnStreams,
    IEnumerable<FileObservation> Files, int Count);

// One accounting path for native scans and immutable-index replay. Inputs must
// remain repeatable until FileCandidates has been consumed by finalization.
internal static class ObservationAggregate
{
    internal static void Apply(ScanResult result, IEnumerable<ObservationOwner> owners, int limit,
        int initialCapacity, Action<FolderEntry, string?> staleDuplicate, CancellationToken token = default)
    {
        result.PhysicalLinks.Clear(); result.HardLinkAliases = 0;
        result.PhysicalLinks.EnsureCapacity(initialCapacity);
        var aliases = new HashSet<(uint Volume, ulong Id)>();
        HashSet<(FolderEntry Owner, string Name)>? discarded = null;
        HashSet<(uint Volume, ulong Id)>? uncertain = null;
        // Reserve only known names, bounded independently of a large user limit.
        // Owners are repeatable; this stops as soon as the small reserve is known.
        int capacity = 0, capacityLimit = Math.Min(limit, 32_768);
        foreach (var owner in owners)
        {
            token.ThrowIfCancellationRequested();
            capacity += Math.Min(Math.Max(0, owner.Count), capacityLimit - capacity);
            if (capacity == capacityLimit) break;
        }
        var retained = new PriorityQueue<(FolderEntry Folder, FileObservation File), long>(capacity);
        foreach (var owner in owners)
        {
            token.ThrowIfCancellationRequested();
            var folder = owner.Folder;
            folder.LogicalBytes = owner.OwnStreams.Size; folder.AllocatedBytes = owner.OwnStreams.Allocated;
            folder.OwnStreams = owner.OwnStreams.ExtraStreams; folder.OwnStreamBytes = owner.OwnStreams.ExtraStreamBytes;
            folder.FileCount = owner.Count;
            foreach (var file in owner.Files)
            {
                token.ThrowIfCancellationRequested();
                folder.LogicalBytes += file.Size;
                long allocation = file.Allocated ?? 0;
                if (file.Id != 0 && (file.Allocated.HasValue || !file.Unverified))
                {
                    var key = (file.Volume, file.Id);
                    if (!result.PhysicalLinks.TryAdd(key, new(folder, allocation, file.Name, file.Size, file.HardLinked, file.Allocated.HasValue)))
                    {
                        var first = result.PhysicalLinks[key];
                        if (!file.HardLinked && !first.Linked)
                        {
                            staleDuplicate(first.Owner, first.Name); staleDuplicate(folder, file.Name);
                            (uncertain ??= []).Add(key);
                            // No chronological claim: retain the largest observed version once,
                            // explicitly uncertain, so a smaller duplicate cannot hide a large file.
                            if (file.Size > first.Size)
                            {
                                (discarded ??= []).Add((first.Owner, first.Name!));
                                first.Owner.LogicalBytes -= first.Size; first.Owner.FileCount--; first.Owner.AllocatedBytes -= first.Bytes;
                                folder.AllocatedBytes += allocation;
                                result.PhysicalLinks[key] = new(folder, allocation, file.Name, file.Size, false, file.Allocated.HasValue);
                            }
                            else
                            {
                                (discarded ??= []).Add((folder, file.Name));
                                folder.LogicalBytes -= file.Size; folder.FileCount--;
                            }
                        }
                        else
                        {
                            if (first.AllocationKnown && file.Allocated.HasValue) result.HardLinkAliases++;
                            aliases.Add(key);
                            if (!first.AllocationKnown && file.Allocated.HasValue)
                            {
                                folder.AllocatedBytes += allocation;
                                result.PhysicalLinks[key] = new(folder, allocation, file.Name, file.Size, true);
                            }
                        }
                    }
                    else folder.AllocatedBytes += allocation;
                }
                else folder.AllocatedBytes += allocation;
                if (retained.Count < limit) retained.Enqueue((folder, file), file.Size);
                else if (retained.TryPeek(out _, out long floor) && file.Size > floor)
                { retained.Dequeue(); retained.Enqueue((folder, file), file.Size); }
            }
        }
        result.FileCandidates = owners.SelectMany(owner => owner.Files
            .Where(file => discarded?.Contains((owner.Folder, file.Name)) != true)
            .Select(file => (owner.Folder, file with { HardLinked = file.HardLinked || aliases.Contains((file.Volume, file.Id)),
                Unverified = file.Unverified || uncertain?.Contains((file.Volume, file.Id)) == true })));
        if (discarded != null)
        {
            // Refill from the complete pool only on the rare uncertain-identity path.
            retained.Clear();
            foreach (var item in result.FileCandidates)
            {
                token.ThrowIfCancellationRequested();
                if (retained.Count < limit) retained.Enqueue(item, item.File.Size);
                else if (retained.TryPeek(out _, out long floor) && item.File.Size > floor)
                { retained.Dequeue(); retained.Enqueue(item, item.File.Size); }
            }
        }
        result.Files = retained.UnorderedItems.Select(item =>
        {
            var file = item.Element.File;
            return new FileEntry(Path.Combine(item.Element.Folder.Path, file.Name), file.Size, file.Allocated, file.Modified,
                file.HardLinked || aliases.Contains((file.Volume, file.Id)), file.Note, file.ExtraStreams, file.ExtraStreamBytes, file.Unverified);
        }).OrderByDescending(file => file.Size).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
