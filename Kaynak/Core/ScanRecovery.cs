namespace YerelDisk;

internal sealed partial class ScanRun
{
    void RequestRetry(FolderEntry folder)
    {
        lock (issueLock)
        {
            if (nodeMap.TryGetValue(folder, out var node) && node.Invalid && node.Parent != null) folder = node.Parent.Folder;
            retryFolders.Add(folder.Path); result.NeedsRetry = true;
        }
    }
    List<ScanNode> ActiveNodes()
    {
        var active = new List<ScanNode>(); var pending = new Stack<ScanNode>(); pending.Push(nodes[0]);
        while (pending.TryPop(out var node)) { active.Add(node); foreach (var child in node.Children) pending.Push(child); }
        return active;
    }
    void PruneNodes()
    {
        if (nodes.Count == 0) return;
        var active = ActiveNodes(); nodes.Clear(); nodes.AddRange(active); nodeMap.Clear();
        foreach (var node in active) nodeMap[node.Folder] = node;
    }
    void VerifyDirectories()
    {
        HashSet<string>? recheckPaths = null;
        for (validationPass = 1; validationPass <= VerifiedScan.MaxAttempts; validationPass++)
        {
            if (stop.IsCancellationRequested) break;
            BeginPhase(ScanPhase.Verification);
            result.VerificationRounds = validationPass;
            var active = ActiveNodes();
            validationSelection = recheckPaths == null ? null : active.Where(node => !node.ValidationVisited || recheckPaths.Contains(node.Folder.Path)).ToHashSet();
            validationTotal = validationSelection?.Count ?? active.Count; validationDone = 0;
            foreach (var node in validationSelection ?? active.AsEnumerable())
            {
                node.ValidationIssues.Clear(); node.ValidationIssueCount = 0; node.ValidationUnsafe.Clear(); node.Folder.Measurement = node.ScanMeasurement;
            }
            storedIssues = active.Sum(node => node.ScanIssues.Count + node.ValidationIssues.Count);
            validating = true;
            VerifyAnchor(); Report("Doğrulama: " + result.Root.Path, force: true);
            RunDirectoryPass(ValidateDirectory);
            validating = false;
            if (stop.IsCancellationRequested) break;
            VerifyAnchor();
            string[] changed; lock (issueLock) changed = retryFolders.ToArray();
            result.NeedsRetry = changed.Length != 0;
            result.VerificationHistory.Add($"Doğrulama {validationPass}: {validationDone:N0}/{validationTotal:N0} klasör; {changed.Length:N0} değişen alan.");
            if (changed.Length == 0 || validationPass == VerifiedScan.MaxAttempts) break;
            BeginPhase(ScanPhase.Refresh);
            result.VerificationHistory.Add($"Hedefli yenileme {validationPass}: " + string.Join(" | ", changed.Take(12)));
            Report($"Değişen alanlar yeniden ölçülecek · {changed.Length:N0} klasör · {validationPass + 1}/{VerifiedScan.MaxAttempts}", force: true);
            if (stop.Token.WaitHandle.WaitOne(validationPass * 250)) break;
            var dirty = active.Where(n => changed.Contains(n.Folder.Path, StringComparer.OrdinalIgnoreCase)).ToHashSet();
            var navigation = new HashSet<ScanNode>();
            foreach (var node in dirty) for (ScanNode? current = node; current != null; current = current.Parent) navigation.Add(current);
            lock (issueLock) { retryFolders.Clear(); result.NeedsRetry = false; }
            result.RetryCount++;
            refreshedPaths.Clear();
            VerifyAnchor();
            RunDirectoryPass((node, directory) =>
            {
                if (dirty.Contains(node) || !node.Enumerated)
                {
                    var fresh = RefreshDirectory(node, directory);
                    return node.Children.Where(child => fresh.Contains(child) || navigation.Contains(child));
                }
                return node.Children.Where(navigation.Contains);
            });
            PruneNodes();
            recheckPaths = navigation.Select(node => node.Folder.Path).Concat(refreshedPaths.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Claude 1.9.1: an item moved out of an already checked folder is seen only at its
            // destination. Re-check the source too, so it is refreshed instead of counted twice.
            foreach (var source in MovedAwaySources())
                for (ScanNode? current = source; current != null; current = current.Parent) recheckPaths.Add(current.Folder.Path);
        }
        validating = false;
        if (token.IsCancellationRequested) result.Cancelled = true;
    }
    // Claude 1.9.1: folders that still hold a directory or a single-link file now observed in a
    // freshly refreshed folder. Hard links (Links > 1 when observed) are legitimate and skipped.
    List<ScanNode> MovedAwaySources()
    {
        var refreshed = nodes.Where(node => refreshedPaths.ContainsKey(node.Folder.Path)).ToHashSet();
        var directories = new HashSet<ulong>(); var files = new HashSet<(uint Volume, ulong Id)>();
        foreach (var node in refreshed)
        {
            if (node.Folder.ObservedId != 0) directories.Add(node.Folder.ObservedId);
            foreach (var file in node.Files) if (file.Id != 0 && !file.HardLinked) files.Add((file.Volume, file.Id));
        }
        var sources = new List<ScanNode>();
        if (refreshed.Count == 0) return sources;
        foreach (var node in nodes)
        {
            if (refreshed.Contains(node)) continue;
            if (directories.Contains(node.Folder.ObservedId)) sources.Add(node.Parent ?? node);
            else if (files.Count > 0 && node.Files.Any(file => file.Id != 0 && !file.HardLinked && files.Contains((file.Volume, file.Id)))) sources.Add(node);
        }
        return sources;
    }
    void RunDirectoryPass(Func<ScanNode, NativeDirectory, IEnumerable<ScanNode>> visit)
    {
        // Re-open enumeration rights for every pass. Reusing the original open
        // handle would keep its old ACL grant after the user revoked listing.
        using var directory = NativeDirectory.OpenRoot(result.Root.Path);
        if (NativeMetadata.Identity(directory.Handle) != NativeMetadata.Identity(anchor.Handle))
            throw new RootScopeChangedException("Seçilen kökün kimliği değişti; yeni hedef izlenmedi.");
        new DirectoryPass(directory, nodes[0], workerCount, stop.Token, visit, UnavailableDirectory, Fail).Execute();
    }
    IEnumerable<ScanNode> ValidateDirectory(ScanNode node, NativeDirectory directory)
    {
        Interlocked.Increment(ref validationDone); Interlocked.Increment(ref result.VerifiedFolders);
        node.ValidationVisited = true;
        if (!node.Enumerated || InvalidAncestor(node)) return [];
        try
        {
            ValidationTick(node, directory);
            var own = NativeMetadata.Read(directory.Handle, namedStreams);
            if (own.Size != node.OwnStreams.Size || own.Allocated != node.OwnStreams.Allocated
                || own.ExtraStreams != node.OwnStreams.ExtraStreams || own.StreamsKnown != node.OwnStreams.StreamsKnown)
            { Uncertain(node.Folder, node.Folder.Path, "Klasörün kendi veri akışları değişti.", true); return []; }
            DirectoryFingerprint observed = default; int count = 0;
            foreach (var entry in directory.Entries())
            {
                if ((++count & 255) == 0) { stop.Token.ThrowIfCancellationRequested(); ValidationTick(node, directory); }
                if (!excluded.SkipsEntry(node.Folder.Path, entry.Name)) observed.Add(entry);
            }
            // Constructor + worker ticks check location; the lease verifies the
            // parent-relative binding after the whole subtree, before closing.
            if (observed != node.Fingerprint)
            { Uncertain(node.Folder, node.Folder.Path, "Klasörün dosya listesi / üstverisi ilk ölçümden sonra değişti.", true); return []; }
            return validationSelection == null ? node.Children : node.Children.Where(validationSelection.Contains);
        }
        catch (RootScopeChangedException) { throw; }
        catch (ScopeChangedException) when (node.Parent == null) { throw; }
        catch (Exception ex) when (ex is ScopeChangedException || DiskScanner.IsFileError(ex))
        { UnavailableDirectory(node, ex); return []; }
    }
    void ValidationTick(ScanNode node, NativeDirectory directory)
    {
        if (watch.ElapsedMilliseconds - Interlocked.Read(ref lastReport) < 200) return;
        if (Report($"Doğrulama {validationPass}/{VerifiedScan.MaxAttempts} · {Interlocked.Read(ref validationDone):N0}/{validationTotal:N0} klasör · {node.Folder.Path}"))
        {
            VerifyAnchor();
            if (node.Parent == null) directory.EnsureRootLocation(); else directory.EnsureLocation();
        }
    }
    void UnavailableDirectory(ScanNode node, Exception error)
    {
        node.ValidationVisited = true;
        bool retry = error is ScopeChangedException || ShouldRetry(error);
        if (node.Parent == null && !retry) result.Failed = true;
        // Binding loss requires re-reading its parent's name/identity list. Access
        // denial remains a visible previous observation without a pointless retry.
        if (retry) { node.Invalid = true; RequestRetry(node.Parent?.Folder ?? node.Folder); }
        Uncertain(node.Folder, node.Folder.Path, error.Message, false);
        if (node.Files.Count == 0 && node.Children.Count == 0 && node.OwnStreams.Size == 0)
            node.Folder.Measurement = MeasurementState.Unknown;
        if (!validating) node.ScanMeasurement = node.Folder.Measurement;
    }
    HashSet<ScanNode> RefreshDirectory(ScanNode node, NativeDirectory directory)
    {
        var temporaryFolder = new FolderEntry(node.Folder.Path, node.Folder.Parent) { ObservedId = node.Folder.ObservedId };
        var temporary = new ScanNode(temporaryFolder, node.Parent, directory, node.Name) { IsRefresh = true };
        nodeMap[temporaryFolder] = temporary;
        var previous = node.Children.ToDictionary(child => child.Name, StringComparer.Ordinal);
        var fresh = new HashSet<ScanNode>();
        try
        {
            VerifyAnchor();
            temporary.OwnStreams = NativeMetadata.Read(directory.Handle, namedStreams);
            temporaryFolder.OwnStreams = temporary.OwnStreams.ExtraStreams;
            temporaryFolder.OwnStreamBytes = temporary.OwnStreams.ExtraStreamBytes;
            temporaryFolder.LogicalBytes = temporary.OwnStreams.Size;
            temporaryFolder.AllocatedBytes = temporary.OwnStreams.Allocated;
            if (!temporary.OwnStreams.StreamsKnown)
            { temporaryFolder.Measurement = MeasurementState.Partial; Issue(temporaryFolder, temporaryFolder.Path, "Klasörün kendi veri akışları ölçülemedi: " + temporary.OwnStreams.StreamError); }
            var frame = new Frame(temporary); long localTick = -1000;
            try
            {
                foreach (var entry in directory.Entries())
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (excluded.SkipsEntry(node.Folder.Path, entry.Name)) continue;
                    temporary.Fingerprint.Add(entry);
                    if (Tick(ref localTick))
                    {
                        if (Report($"Değişen klasör yeniden ölçülüyor · {temporaryFolder.Path}")) VerifyAnchor();
                    }
                    if ((entry.Attributes & FileAttributes.Directory) == 0) { Entry(null!, frame, entry, ref localTick); continue; }
                    string path = Path.Combine(node.Folder.Path, entry.Name);
                    if ((entry.Attributes & (FileAttributes.ReparsePoint | CloudFlags)) != 0)
                    {
                        UnknownChild(temporaryFolder, path); Issue(temporaryFolder, path, "Klasör bağlantısı / bulut yer tutucusu; izlenmedi.");
                        if (previous.TryGetValue(entry.Name, out var old)) temporaryFolder.Children[^1].LastObservedBytes = ObservedSize(old.Folder);
                        continue;
                    }
                    if (previous.TryGetValue(entry.Name, out var child) && !child.Invalid && child.Folder.ObservedId == entry.Id)
                    { temporary.Children.Add(child); temporaryFolder.Children.Add(child.Folder); }
                    else
                    {
                        var folder = new FolderEntry(path, node.Folder) { ObservedId = entry.Id, Measurement = MeasurementState.Unknown };
                        child = new(folder, node, null, entry.Name) { ScanMeasurement = MeasurementState.Unknown };
                        temporary.Children.Add(child); temporaryFolder.Children.Add(folder); fresh.Add(child);
                        nodeMap[folder] = child;
                    }
                }
            }
            finally { frame.Entries.Dispose(); }
            if (node.Parent == null) directory.EnsureRootLocation(); else directory.EnsureLocation();
            Interlocked.Add(ref files, temporary.Files.Count - node.Files.Count);
            Interlocked.Add(ref bytes, temporaryFolder.LogicalBytes - node.Folder.LogicalBytes);
            node.Files = temporary.Files; node.Children = temporary.Children; node.OwnStreams = temporary.OwnStreams;
            node.Fingerprint = temporary.Fingerprint; node.Enumerated = true; node.Invalid = false;
            node.ScanIssues = temporary.ScanIssues; node.ScanUnsafe = temporary.ScanUnsafe;
            node.ScanIssueCount = temporary.ScanIssueCount;
            node.ScanMeasurement = temporaryFolder.Measurement; node.Folder.Measurement = temporaryFolder.Measurement;
            node.Folder.LogicalBytes = temporaryFolder.LogicalBytes; node.Folder.AllocatedBytes = temporaryFolder.AllocatedBytes;
            node.Folder.FileCount = temporaryFolder.FileCount; node.Folder.OwnStreams = temporaryFolder.OwnStreams;
            node.Folder.OwnStreamBytes = temporaryFolder.OwnStreamBytes;
            node.Folder.Children.Clear(); node.Folder.Children.AddRange(temporaryFolder.Children);
            foreach (var child in node.Folder.Children) child.Parent = node.Folder;
            node.ValidationVisited = false; refreshedPaths[node.Folder.Path] = 0;
            Interlocked.Increment(ref result.RefreshedFolders);
            return fresh;
        }
        catch (RootScopeChangedException) { throw; }
        catch (ScopeChangedException) when (node.Parent == null) { throw; }
        catch (Exception ex) when (ex is ScopeChangedException || DiskScanner.IsFileError(ex))
        { UnavailableDirectory(node, ex); return []; }
        finally { nodeMap.TryRemove(temporaryFolder, out _); }
    }
    static long? ObservedSize(FolderEntry folder)
    {
        if (folder.Measurement == MeasurementState.Unknown) return folder.LastObservedBytes;
        long sum = 0; var pending = new Stack<FolderEntry>(); pending.Push(folder);
        while (pending.TryPop(out var child)) { sum += child.LogicalBytes; foreach (var next in child.Children) pending.Push(next); }
        return sum;
    }
    void RebuildDiagnostics()
    {
        if (nodes.Count == 0) return;
        result.Issues.Clear(); result.UnverifiedPaths.Clear(); result.IssueCount = 0; result.UnknownAllocation = 0;
        foreach (var node in nodes)
        {
            node.Folder.IssueCount = node.ScanIssueCount + node.ValidationIssueCount;
            result.IssueCount += node.Folder.IssueCount;
            result.Issues.AddRange(node.ScanIssues.Concat(node.ValidationIssues).Take(ScanResult.IssueLimit - result.Issues.Count));
            result.UnverifiedPaths.UnionWith(node.ScanUnsafe); result.UnverifiedPaths.UnionWith(node.ValidationUnsafe);
            if (!node.OwnStreams.StreamsKnown) result.UnknownAllocation++;
            result.UnknownAllocation += node.Files.Count(file => file.Unverified || !file.Allocated.HasValue);
        }
    }
    void StaleDuplicate(FolderEntry owner, string? name)
    {
        string path = name == null ? owner.Path : Path.Combine(owner.Path, name);
        if (result.UnverifiedPaths.Contains(path)) return;
        if (owner.Measurement == MeasurementState.Measured) owner.Measurement = MeasurementState.Partial;
        RememberUnsafe(owner, path);
        Issue(owner, path, "Ölçüm doğrulanamadı: aynı dosya başka bir konumda da görüldü; doğrulama sırasında taşınmış olabilir.");
    }
    void AggregateObservations()
    {
        result.PhysicalLinks.Clear(); result.HardLinkAliases = 0;
        // Stale single-link observations can repeat an ID too. Bound the initial
        // reservation instead of trusting observation count as unique ID count.
        result.PhysicalLinks.EnsureCapacity(IdentityCapacity.ForObservations(nodes));
        var aliases = new HashSet<(uint Volume, ulong Id)>();
        var retained = new PriorityQueue<(ScanNode Node, FileObservation File), long>();
        foreach (var node in nodes)
        {
            var folder = node.Folder;
            folder.LogicalBytes = node.OwnStreams.Size; folder.AllocatedBytes = node.OwnStreams.Allocated;
            folder.OwnStreams = node.OwnStreams.ExtraStreams; folder.OwnStreamBytes = node.OwnStreams.ExtraStreamBytes;
            folder.FileCount = node.Files.Count;
            foreach (var file in node.Files)
            {
                folder.LogicalBytes += file.Size;
                if (file.Allocated is { } allocation)
                {
                    if (file.Id != 0 && !result.PhysicalLinks.TryAdd((file.Volume, file.Id), new(folder, allocation, file.Name, file.Size, file.HardLinked)))
                    {
                        var first = result.PhysicalLinks[(file.Volume, file.Id)];
                        // Claude 1.9.1: one link at both observations is not a hard link but a move
                        // that the bounded rounds could not settle: show both paths as unverified.
                        if (!file.HardLinked && !first.Linked) { StaleDuplicate(first.Owner, first.Name); StaleDuplicate(folder, file.Name); }
                        else { result.HardLinkAliases++; aliases.Add((file.Volume, file.Id)); }
                    }
                    else folder.AllocatedBytes += allocation;
                }
                if (retained.Count < fileLimit) retained.Enqueue((node, file), file.Size);
                else if (retained.TryPeek(out _, out long floor) && file.Size > floor)
                { retained.Dequeue(); retained.Enqueue((node, file), file.Size); }
            }
        }
        // Keep the hint-free snapshot until all selected roots are merged. Removing
        // a moved/shrunken winner can require a source candidate outside its old K.
        result.FileCandidates = nodes.SelectMany(node => node.Files.Select(file =>
            (node.Folder, file with { HardLinked = file.HardLinked || aliases.Contains((file.Volume, file.Id)) })));
        result.Files = retained.UnorderedItems.Select(item =>
        {
            var file = item.Element.File;
            return new FileEntry(Path.Combine(item.Element.Node.Folder.Path, file.Name), file.Size, file.Allocated, file.Modified,
                file.HardLinked || aliases.Contains((file.Volume, file.Id)), file.Note, file.ExtraStreams, file.ExtraStreamBytes, file.Unverified);
        }).OrderByDescending(file => file.Size).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
