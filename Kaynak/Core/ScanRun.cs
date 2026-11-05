using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;

namespace YerelDisk;

// Claude 1.3.0: one scan of one root, run by one or more worker threads.
//
// The safety model of 1.2.1 is kept, per worker:
// - Every lookup below the root is relative to an already open directory handle, never follows a
//   reparse point and is checked against the enumerated FileId (NativeDirectory.OpenDirectory/OpenFile).
// - A folder stays open until it and every folder below it are finished (ScanNode.Pending). Its
//   close-out check then runs with its parent still open, after its whole subtree: the same order
//   as the single-threaded depth-first scan, whichever worker finishes last.
// - The root is re-resolved strictly at the end and on a 200 ms tick shared by all workers; each
//   worker re-checks the folder it is in on its own 200 ms tick.
// - Strict callers stop on scope loss. Recovery callers retain directory snapshots;
//   descendants can be refreshed, while root identity loss and unexpected errors stop the run.
//
// A folder is enumerated by exactly one worker, which alone writes its FolderEntry until the folder
// is finished; subtree totals are folded after all workers have stopped. Shared state: the work
// queue, the counters (Interlocked), the largest-files heap, the issue list and the hard link set (locks).
internal sealed partial class ScanRun(string rootPath, IProgress<ScanProgress>? progress, CancellationToken token,
    int fileLimit, HashSet<(uint Volume, ulong Id)> seenLinks, ScanPolicy? policy, ScanExclusions? exclusions = null)
{
    public ScanRun(string rootPath, IProgress<ScanProgress>? progress, CancellationToken token,
        int fileLimit, HashSet<(uint Volume, ulong Id)> seenLinks) : this(rootPath, progress, token, fileLimit, seenLinks, null) { }
    // OFFLINE / RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS: do not hydrate cloud files.
    const FileAttributes CloudFlags = NativeDirectory.CloudFlags;
    readonly ScanExclusions excluded = exclusions ?? ScanExclusions.None;
    readonly ScanResult result = new(new FolderEntry(rootPath, null));
    readonly Stopwatch watch = Stopwatch.StartNew();
    volatile ScanPhase phase;
    long phaseStarted = Stopwatch.GetTimestamp();
    TimeSpan initialElapsed, verificationElapsed, refreshElapsed;
    readonly PriorityQueue<FileEntry, long> largest = new();
    readonly List<ScanNode> nodes = [];
    readonly ConcurrentDictionary<FolderEntry, ScanNode> nodeMap = new();
    readonly HashSet<string> retryFolders = new(StringComparer.OrdinalIgnoreCase);
    bool validating;
    int validationPass = 1;
    long validationDone, validationTotal;
    int storedIssues;
    HashSet<ScanNode>? validationSelection;
    readonly ConcurrentDictionary<string, byte> refreshedPaths = new(StringComparer.OrdinalIgnoreCase);
    readonly object issueLock = new(), fatalLock = new();
    long heapFloor = -1, files, bytes, folders = 1, lastReport = -1000;
    int workerCount = 1;
    bool namedStreams, rootCompleted;
    // Claude 1.3.1: every error that stopped the scan, from every worker (guarded by fatalLock).
    // Keeping only the first one let an I/O error hide a later scope change (Codex 1.3.0 review).
    readonly List<Exception> fatalErrors = [];
    NativeDirectory anchor = null!;
    BlockingCollection<ScanNode> queue = null!;
    CancellationTokenSource stop = null!;

    public ScanResult Execute(int workers)
    {
        try
        {
            // Anchor BEFORE the first callback. Later lookups use only this object and its descendants.
            anchor = NativeDirectory.OpenRoot(rootPath);
            if (policy != null && NativeMetadata.Identity(anchor.Handle) != policy.RootIdentity)
                throw new ScopeChangedException("Seçilen kökün kimliği değişti; yeni kök izlenmedi.");
            result.Root.ObservedId = NativeMetadata.Identity(anchor.Handle).Id;
            ScanNode root = new(result.Root, null, anchor, "");
            nodes.Add(root);
            nodeMap[result.Root] = root;
            namedStreams = anchor.SupportsNamedStreams;
            workerCount = workers > 0 ? workers : VolumeInfo.ScanWorkers(rootPath);
            result.Workers = workerCount;
            using (stop = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (queue = new BlockingCollection<ScanNode>())
            {
                queue.Add(root);
                List<Thread> threads = [];
                try
                {
                    for (int i = 1; i < workerCount; i++)
                    {
                        Thread thread = new(Worker) { IsBackground = true, Name = "YerelDisk tarama " + i };
                        thread.Start();
                        threads.Add(thread);
                    }
                    Worker();
                }
                catch (Exception ex) { Fail(ex); } // a worker thread could not start: stop the others
                finally { foreach (Thread thread in threads) thread.Join(); }
                if (policy != null)
                {
                    foreach (var node in nodes) node.ScanMeasurement = node.Folder.Measurement;
                    if (!token.IsCancellationRequested && rootCompleted && fatalErrors.Count == 0) VerifyDirectories();
                }
            }
        }
        catch (Exception ex) when (ex is ScopeChangedException || DiskScanner.IsFileError(ex)) { lock (fatalLock) fatalErrors.Add(ex); }
        finally
        {
            BeginPhase(ScanPhase.Finalizing);
            foreach (ScanNode node in nodes) node.Directory?.Dispose();
            anchor?.Dispose(); // Also closes a root rejected before its ScanNode was registered.
        }

        // Claude 1.3.1: classify all stop errors, not just the first. A programming error is still
        // rethrown (as in 1.2.1, so no result with stale paths reaches the UI). Otherwise any scope
        // change marks the result, which keeps stale-path actions disabled, and every root-level
        // I/O error is listed and marks the result as stopped by an error.
        Exception? unexpected = fatalErrors.Find(ex => ex is not ScopeChangedException && !DiskScanner.IsFileError(ex));
        if (unexpected != null) ExceptionDispatchInfo.Throw(unexpected);
        foreach (Exception ex in fatalErrors.DistinctBy(ex => (ex.GetType(), ex.Message)))
        {
            if (ex is ScopeChangedException) { result.ScopeChanged = true; Issue(result.Root, rootPath, ex.Message); }
            else { result.Failed = true; Issue(result.Root, rootPath, DiskScanner.FriendlyError(ex)); }
        }
        if (fatalErrors.Count == 0 && !rootCompleted) result.Cancelled = true; // only cancellation stops workers without an error

        // A queued folder may never have started when cancellation stops the workers.
        // Its default counters cannot prove that it is empty. Preserve measured children
        // of partially enumerated parents as usable observations, but block unseen folders.
        if (policy != null)
        {
            PruneNodes();
            RebuildDiagnostics();
            MarkUnfinished();
            AggregateObservations();
        }

        // Fold subtree totals bottom-up: reversed preorder lists every folder before its parent.
        List<FolderEntry> order = [];
        Stack<FolderEntry> walk = new();
        walk.Push(result.Root);
        while (walk.TryPop(out FolderEntry? folder))
        {
            order.Add(folder);
            foreach (FolderEntry child in folder.Children) walk.Push(child);
        }
        for (int i = order.Count - 1; i > 0; i--)
        {
            FolderEntry f = order[i], p = f.Parent!;
            p.LogicalBytes += f.LogicalBytes;
            p.AllocatedBytes += f.AllocatedBytes;
            p.FileCount += f.FileCount;
            p.IssueCount += f.IssueCount;
            if (f.Measurement != MeasurementState.Measured && p.Measurement == MeasurementState.Measured)
                p.Measurement = MeasurementState.Partial;
        }
        // Context/type text is added by the caller after the FINAL retention limit,
        // including the global multi-root merge. Measurement notes remain on each record.
        if (policy == null) result.Files = largest.UnorderedItems.Select(x => x.Element)
            .OrderByDescending(x => x.Size).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        result.Folders = order.Count;
        result.Elapsed = watch.Elapsed;
        result.Timings = new(initialElapsed, verificationElapsed, refreshElapsed);
        Report(rootPath, force: true);
        return result;
    }

    void Worker()
    {
        Stack<Frame> stack = new();
        long localTick = -1000;
        try
        {
            while (queue.TryTake(out ScanNode? start, Timeout.Infinite, stop.Token))
            {
                stack.Push(new Frame(start));
                while (stack.Count > 0) Step(stack, ref localTick);
            }
        }
        catch (OperationCanceledException) { Cancel(); }
        catch (Exception ex) { Fail(ex); }
        finally { while (stack.TryPop(out Frame? frame)) frame.Entries.Dispose(); }
    }

    // One unit of work on the folder at the top of this worker's stack (the 1.2.1 loop body).
    void Step(Stack<Frame> stack, ref long localTick)
    {
        stop.Token.ThrowIfCancellationRequested();
        Frame frame = stack.Peek();
        ScanNode node = frame.Node;
        FolderEntry folder = node.Folder;
        NativeDirectory directory = node.Directory!;
        if (policy != null && InvalidAncestor(node))
        {
            node.Invalid = true; node.SkipCloseCheck = true;
            if (!node.Enumerated && folder.FileCount == 0 && folder.Children.Count == 0) folder.Measurement = MeasurementState.Unknown;
            stack.Pop(); frame.Entries.Dispose(); Release(node); return;
        }
        // Location checks are costly. Each worker re-checks its own folder every 200 ms; the root
        // checks and the progress report run on a 200 ms tick shared by all workers. With a single
        // worker both ticks coincide, which is the 1.2.1 order.
        bool tick = Tick(ref localTick);
        if (tick && Report(folder.Path)) VerifyAnchor();
        bool finished = false;
        try
        {
            if (tick)
            {
                if (node.Parent is null) directory.EnsureRootLocation(); else directory.EnsureLocation();
            }
            if (!frame.StreamsMeasured)
            {
                frame.StreamsMeasured = true;
                var ownStreams = NativeMetadata.Read(directory.Handle, namedStreams);
                node.OwnStreams = ownStreams;
                folder.OwnStreams = ownStreams.ExtraStreams;
                folder.OwnStreamBytes = ownStreams.ExtraStreamBytes;
                folder.LogicalBytes += ownStreams.Size;
                folder.AllocatedBytes += ownStreams.Allocated;
                Interlocked.Add(ref bytes, ownStreams.Size);
                if (!ownStreams.StreamsKnown)
                {
                    Interlocked.Increment(ref result.UnknownAllocation);
                    Issue(folder, folder.Path, "Klasörün kendi veri akışları ölçülemedi: " + ownStreams.StreamError);
                    if (policy != null) folder.Measurement = MeasurementState.Partial;
                }
            }
            if (!frame.Entries.MoveNext())
            {
                finished = true; node.Enumerated = true; node.Fingerprint = frame.Fingerprint;
            }
            else
            {
                var entry = frame.Entries.Current;
                if (!excluded.SkipsEntry(folder.Path, entry.Name))
                {
                    if (policy != null) frame.Fingerprint.Add(entry);
                    Entry(stack, frame, entry, ref localTick);
                }
            }
        }
        catch (ScopeChangedException ex) when (policy != null && node.Parent != null && ex is not RootScopeChangedException)
        { node.Invalid = true; node.SkipCloseCheck = true; Uncertain(folder, folder.Path, ex.Message, true); finished = true; }
        catch (ScopeChangedException) { throw; }
        catch (Exception ex) when (DiskScanner.IsFileError(ex))
        {
            // As before, a folder that fails is closed without its close-out check.
            Issue(folder, folder.Path, DiskScanner.FriendlyError(ex));
            if (policy != null)
            {
                folder.Measurement = folder.FileCount == 0 && folder.Children.Count == 0 ? MeasurementState.Unknown : MeasurementState.Partial;
                RememberUnsafe(folder, folder.Path);
                if (ShouldRetry(ex)) RequestRetry(folder);
                if (node.Parent == null) result.Failed = true;
            }
            node.SkipCloseCheck = true;
            finished = true;
        }
        if (finished)
        {
            stack.Pop();
            frame.Entries.Dispose();
            Release(node);
        }
    }

    void Entry(Stack<Frame> stack, Frame frame, NativeEntry entry, ref long localTick)
    {
        ScanNode node = frame.Node;
        FolderEntry folder = node.Folder;
        NativeDirectory directory = node.Directory!;
        string entryPath = Path.Combine(folder.Path, entry.Name);
        stop.Token.ThrowIfCancellationRequested();
        if (Tick(ref localTick))
        {
            if (Report(folder.Path)) VerifyAnchor();
            if (node.Parent is null) directory.EnsureRootLocation(); else directory.EnsureLocation();
        }
        try
        {
            var attributes = entry.Attributes;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & (FileAttributes.ReparsePoint | CloudFlags)) != 0)
                {
                    Issue(folder, entryPath, "Klasör bağlantısı / bulut yer tutucusu; izlenmedi.");
                    if (policy != null) UnknownChild(folder, entryPath);
                    return;
                }
                NativeDirectory childDirectory = directory.OpenDirectory(entry.Name, entry.Id);
                FolderEntry child = new(entryPath, folder);
                try { if (policy != null) child.ObservedId = NativeMetadata.Identity(childDirectory.Handle).Id; }
                catch { childDirectory.Dispose(); throw; }
                folder.Children.Add(child);
                ScanNode childNode = new(child, node, childDirectory, entry.Name);
                node.Children.Add(childNode); nodeMap[child] = childNode;
                Interlocked.Increment(ref node.Pending);
                lock (nodes) nodes.Add(childNode);
                Interlocked.Increment(ref folders);
                // Hand the folder to an idle worker while the shared queue is short; otherwise this
                // worker continues depth-first, as before.
                if (workerCount > 1 && queue.Count < workerCount) queue.Add(childNode);
                else stack.Push(new Frame(childNode));
                return;
            }
            long size = entry.Size;
            long? allocated = null;
            bool hardLinked = false, counted = true;
            int extraStreams = 0;
            long extraStreamBytes = 0;
            string hint = ""; // Measurement notes only; context/type is filled for final retained files.
            bool unverified = false;
            uint volume = 0; ulong id = 0;
            if ((attributes & (FileAttributes.ReparsePoint | CloudFlags)) != 0)
            {
                const string warning = "Bağlantı / bulut dosyası: içerik açılmadı, akışlar ve ayrılan alan ölçülmedi.";
                hint = " " + warning;
                Issue(folder, entryPath, warning);
                Interlocked.Increment(ref result.UnknownAllocation);
                if (policy != null) { unverified = true; folder.Measurement = MeasurementState.Partial; RememberUnsafe(folder, entryPath); }
            }
            else
            {
                try
                {
                    using SafeFileHandle handle = directory.OpenFile(entry.Name);
                    var metadata = NativeMetadata.Read(handle, namedStreams);
                    if ((metadata.Attributes & (FileAttributes.ReparsePoint | CloudFlags)) != 0)
                        throw new ScopeChangedException("Dosya açılış sırasında bağlantı / bulut yer tutucusuna dönüştü.");
                    if (entry.Id != 0 && metadata.Id != entry.Id)
                        throw new ScopeChangedException("Dosya kimliği tarama sırasında değişti; yeni dosya izlenmedi.");
                    size = metadata.Size;
                    volume = metadata.Volume; id = metadata.Id;
                    allocated = metadata.Allocated;
                    extraStreams = metadata.ExtraStreams;
                    extraStreamBytes = metadata.ExtraStreamBytes;
                    if (!metadata.StreamsKnown)
                    {
                        Interlocked.Increment(ref result.UnknownAllocation);
                        hint += " Ek veri akışları ölçülemedi; boyut kısmi olabilir.";
                        Issue(folder, entryPath, "Veri akışları ölçülemedi: " + metadata.StreamError);
                        if (policy != null) { unverified = true; folder.Measurement = MeasurementState.Partial; RememberUnsafe(folder, entryPath); }
                    }
                    hardLinked = metadata.Links > 1;
                    if (hardLinked && policy == null)
                    {
                        // Physical space of a hard-linked file is counted once, at the first name any
                        // worker reaches; with several workers that folder can differ between scans.
                        bool first;
                        lock (seenLinks)
                        {
                            first = seenLinks.Add((metadata.Volume, metadata.Id));
                        }
                        if (!first)
                        {
                            counted = false;
                            Interlocked.Increment(ref result.HardLinkAliases);
                        }
                    }
                }
                catch (ScopeChangedException ex) when (policy != null)
                {
                    allocated = null; unverified = true; id = 0;
                    Uncertain(folder, entryPath, ex.Message, true);
                    hint += " Listeleme sırasında gözlenen boyut; dosya kimliği / ölçümü doğrulanamadı.";
                }
                catch (ScopeChangedException) { throw; }
                catch (Exception ex) when (DiskScanner.IsFileError(ex))
                {
                    Interlocked.Increment(ref result.UnknownAllocation);
                    Issue(folder, entryPath, "Ayrılan alan / akışlar ölçülemedi: " + ex.Message);
                    if (policy != null)
                    {
                        unverified = true; folder.Measurement = MeasurementState.Partial; RememberUnsafe(folder, entryPath);
                        if (ShouldRetry(ex)) RequestRetry(folder);
                        hint += " Listeleme boyutu; ek veri akışları ve güncellik doğrulanamadı.";
                    }
                }
            }
            folder.LogicalBytes += size;
            folder.FileCount++;
            if (counted && allocated.HasValue) folder.AllocatedBytes += allocated.Value;
            if (!node.IsRefresh)
            {
                Interlocked.Increment(ref files);
                Interlocked.Add(ref bytes, size);
            }
            if (policy != null)
            {
                if (extraStreams > 0) hint += $" {extraStreams:N0} ek veri akışı ({extraStreamBytes:N0} bayt) boyuta dahil.";
                node.Files.Add(new(entry.Name, size, allocated, entry.Modified, volume, id, hardLinked,
                    extraStreams, extraStreamBytes, unverified, hint));
                return;
            }
            // Once the heap is full, files no larger than its smallest entry skip the lock.
            if (size > Interlocked.Read(ref heapFloor))
            {
                if (extraStreams > 0) hint += $" {extraStreams:N0} ek veri akışı ({extraStreamBytes:N0} bayt) boyuta dahil.";
                FileEntry item = new(entryPath, size, allocated, entry.Modified, hardLinked, hint, extraStreams, extraStreamBytes, unverified);
                lock (largest)
                {
                    if (largest.Count < fileLimit) largest.Enqueue(item, size);
                    else if (largest.TryPeek(out _, out long smallest) && size > smallest) { largest.Dequeue(); largest.Enqueue(item, size); }
                    if (largest.Count == fileLimit && largest.TryPeek(out _, out long floor)) Interlocked.Exchange(ref heapFloor, floor);
                }
            }
        }
        catch (ScopeChangedException ex) when (policy != null && ex is not RootScopeChangedException)
        {
            UnknownChild(folder, entryPath); Uncertain(folder, entryPath, ex.Message, true);
        }
        catch (ScopeChangedException) { throw; }
        catch (Exception ex) when (DiskScanner.IsFileError(ex))
        {
            Issue(folder, entryPath, DiskScanner.FriendlyError(ex));
            if (policy != null)
            {
                if ((entry.Attributes & FileAttributes.Directory) != 0) UnknownChild(folder, entryPath);
                folder.Measurement = MeasurementState.Partial; RememberUnsafe(folder, entryPath);
                if (ShouldRetry(ex)) RequestRetry(folder);
            }
        }
    }

    // Drops one pending unit; whoever finishes a folder runs its close-out check and then
    // releases its parent, so ancestors finish after their descendants.
    void Release(ScanNode? node)
    {
        while (node != null && Interlocked.Decrement(ref node.Pending) == 0)
        {
            Complete(node);
            node = node.Parent;
        }
    }

    void Complete(ScanNode node)
    {
        try
        {
            if (!node.SkipCloseCheck && !(policy != null && InvalidAncestor(node)))
            {
                // Root: strict full-path re-resolution. Children: one-component link check (1.2.1).
                if (node.Parent is null) node.Directory!.VerifyBinding(node.Folder.Path);
                else node.Directory!.VerifyLink(node.Parent.Directory!, node.Name);
            }
        }
        catch (PathUnavailableException ex)
        {
            Issue(node.Folder, node.Folder.Path, ex.Message);
            if (policy != null) { node.Invalid = true; RequestRetry(node.Parent?.Folder ?? node.Folder); node.Folder.Measurement = MeasurementState.Partial; RememberUnsafe(node.Folder, node.Folder.Path); }
        }
        catch (ScopeChangedException ex) when (policy != null && node.Parent != null)
        { node.Invalid = true; Uncertain(node.Folder, node.Folder.Path, ex.Message, true); }
        finally
        {
            // The root handle is the shared anchor; Execute closes it after all workers stop.
            if (node.Parent is not null) { node.Directory!.Dispose(); node.Directory = null; }
        }
        if (node.Parent is null) { rootCompleted = true; queue.CompleteAdding(); }
    }

    // Missing paths can be reported by either a relative open or a location check.
    // Permanent access failures do not justify repeating an entire selected root.
    internal static bool ShouldRetry(Exception error) => error is PathUnavailableException
        or NativePathException { MayBeTransient: true };

    void MarkUnfinished()
    {
        foreach (var node in nodes)
        {
            if (node.Enumerated) continue;
            var folder = node.Folder;
            bool unseen = folder.FileCount == 0 && folder.Children.Count == 0
                && folder.LogicalBytes == 0 && folder.AllocatedBytes == 0;
            folder.Measurement = unseen ? MeasurementState.Unknown : MeasurementState.Partial;
            if (unseen) RememberUnsafe(folder, folder.Path);
        }
    }

    bool Tick(ref long last)
    {
        long now = watch.ElapsedMilliseconds;
        if (now - last < 200) return false;
        last = now;
        return true;
    }

    static bool InvalidAncestor(ScanNode node)
    {
        for (ScanNode? current = node; current != null; current = current.Parent) if (current.Invalid) return true;
        return false;
    }
    void RememberUnsafe(FolderEntry owner, string path)
    {
        lock (issueLock)
        {
            result.UnverifiedPaths.Add(path);
            if (nodeMap.TryGetValue(owner, out var node))
                (validating ? node.ValidationUnsafe : node.ScanUnsafe).Add(path);
        }
    }
    void Uncertain(FolderEntry folder, string path, string reason, bool retry)
    {
        if (retry) RequestRetry(folder);
        folder.Measurement = MeasurementState.Partial;
        RememberUnsafe(folder, path); Issue(folder, path, "Ölçüm doğrulanamadı: " + reason);
    }
    void UnknownChild(FolderEntry parent, string path)
    {
        if (!parent.Children.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            parent.Children.Add(new(path, parent) { Measurement = MeasurementState.Unknown });
        parent.Measurement = MeasurementState.Partial; RememberUnsafe(parent, path);
    }

    void VerifyAnchor()
    {
        try { anchor.VerifyBinding(rootPath); anchor.EnsureRootLocation(); }
        catch (Exception ex) when (ex is ScopeChangedException || DiskScanner.IsFileError(ex))
        { throw new RootScopeChangedException(ex.Message); }
    }

    // Coordinator only: transitions happen after all workers of the previous pass joined.
    // Durations are wall time, never a sum of worker CPU times.
    void BeginPhase(ScanPhase next)
    {
        long now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(phaseStarted, now);
        switch (phase)
        {
            case ScanPhase.InitialScan: initialElapsed += elapsed; break;
            case ScanPhase.Verification: verificationElapsed += elapsed; break;
            case ScanPhase.Refresh: refreshElapsed += elapsed; break;
        }
        phaseStarted = now; phase = next;
    }

    bool Report(string current, bool force = false)
    {
        long now = watch.ElapsedMilliseconds, last = Interlocked.Read(ref lastReport);
        if (!force && (now - last < 200 || Interlocked.CompareExchange(ref lastReport, now, last) != last)) return false;
        if (force) Interlocked.Exchange(ref lastReport, now);
        progress?.Report(new(Interlocked.Read(ref files), Interlocked.Read(ref folders), Interlocked.Read(ref bytes),
            Interlocked.Read(ref result.IssueCount), current, watch.Elapsed, phase));
        return true;
    }

    // folder is written only by the worker enumerating it, or by the single worker finishing it.
    void Issue(FolderEntry folder, string path, string reason)
    {
        folder.IssueCount++;
        Interlocked.Increment(ref result.IssueCount);
        lock (issueLock)
        {
            var issue = new ScanIssue(path, reason);
            if (result.Issues.Count < ScanResult.IssueLimit) result.Issues.Add(issue);
            if (policy != null && nodeMap.TryGetValue(folder, out var node))
            {
                if (validating) node.ValidationIssueCount++; else node.ScanIssueCount++;
                if (storedIssues < ScanResult.IssueLimit)
                { (validating ? node.ValidationIssues : node.ScanIssues).Add(issue); storedIssues++; }
            }
        }
    }

    void Fail(Exception ex)
    {
        lock (fatalLock) fatalErrors.Add(ex);
        Cancel();
    }

    void Cancel()
    {
        try { stop.Cancel(); } catch (ObjectDisposedException) { }
    }

    sealed class Frame(ScanNode node)
    {
        public ScanNode Node { get; } = node;
        public IEnumerator<NativeEntry> Entries { get; } = node.Directory!.Entries().GetEnumerator();
        public bool StreamsMeasured;
        public DirectoryFingerprint Fingerprint;
    }
}

// Claude 1.3.0: a folder being scanned. Pending = 1 for its own enumeration plus 1 for each child
// folder not yet finished; the worker that brings it to zero checks and closes the folder.
internal sealed class ScanNode(FolderEntry folder, ScanNode? parent, NativeDirectory? directory, string name)
{
    public FolderEntry Folder { get; } = folder;
    public ScanNode? Parent { get; } = parent;
    public NativeDirectory? Directory = directory;
    public string Name { get; } = name;
    public int Pending = 1;
    public bool SkipCloseCheck;
    public volatile bool Invalid;
    public bool Enumerated;
    public DirectoryFingerprint Fingerprint;
    public FileMetadata OwnStreams;
    public bool IsRefresh;
    public bool ValidationVisited;
    public int ScanIssueCount, ValidationIssueCount;
    public List<ScanNode> Children = [];
    public List<FileObservation> Files = [];
    public MeasurementState ScanMeasurement;
    public List<ScanIssue> ScanIssues = [], ValidationIssues = [];
    public HashSet<string> ScanUnsafe = new(StringComparer.OrdinalIgnoreCase), ValidationUnsafe = new(StringComparer.OrdinalIgnoreCase);
}
