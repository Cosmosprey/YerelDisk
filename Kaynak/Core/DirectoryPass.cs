using System.Collections.Concurrent;
using System.Diagnostics;

namespace YerelDisk;

// A bounded breadth queue plus depth-first local stacks. A parent handle remains
// alive until all children opened relative to it finish, including cancelled jobs.
internal sealed class DirectoryPass(NativeDirectory anchor, ScanNode root, int workers, CancellationToken token,
    Func<ScanNode, NativeDirectory, IEnumerable<ScanNode>> visit, Action<ScanNode, Exception> unavailable,
    Action<Exception> fail)
{
    readonly BlockingCollection<Work> queue = new();
    int pending = 1;
    internal void Execute()
    {
        using (queue)
        {
            queue.Add(new(root, new Lease(anchor, false, null, null, fail)));
            var threads = new List<Thread>();
            try
            {
                for (int i = 1; i < workers; i++)
                {
                    var thread = new Thread(Worker) { IsBackground = true, Name = "YerelDisk doğrulama " + i };
                    thread.Start(); threads.Add(thread);
                }
                Worker();
            }
            catch (Exception ex) { fail(ex); }
            finally
            {
                foreach (var thread in threads) thread.Join();
                while (queue.TryTake(out var work)) Finish(work);
            }
        }
    }
    void Worker()
    {
        var stack = new Stack<Frame>();
        long lastLocation = 0;
        try
        {
            while (queue.TryTake(out var start, Timeout.Infinite, token))
            {
                stack.Push(new(start));
                while (stack.TryPeek(out var frame))
                {
                    token.ThrowIfCancellationRequested();
                    if (frame.Children == null && Stopwatch.GetTimestamp() - lastLocation >= Stopwatch.Frequency / 5)
                    {
                        lastLocation = Stopwatch.GetTimestamp();
                        try
                        {
                            if (frame.Work.Node.Parent == null) frame.Work.Lease.Directory.EnsureRootLocation();
                            else frame.Work.Lease.Directory.EnsureLocation();
                        }
                        catch (Exception ex) when (frame.Work.Node.Parent != null && (ex is ScopeChangedException || DiskScanner.IsFileError(ex)))
                        { unavailable(frame.Work.Node, ex); frame.Children = Enumerable.Empty<ScanNode>().GetEnumerator(); }
                    }
                    frame.Children ??= visit(frame.Work.Node, frame.Work.Lease.Directory).GetEnumerator();
                    if (!frame.Children.MoveNext()) { stack.Pop(); frame.Dispose(); Finish(frame.Work); continue; }
                    var child = frame.Children.Current;
                    NativeDirectory directory;
                    try { directory = frame.Work.Lease.Directory.OpenDirectory(child.Name, child.Folder.ObservedId); }
                    catch (Exception ex) when (ex is ScopeChangedException || DiskScanner.IsFileError(ex))
                    { unavailable(child, ex); continue; }
                    var parentDirectory = frame.Work.Lease.Directory;
                    var work = new Work(child, new Lease(directory, true, frame.Work.Lease, () =>
                    {
                        if (token.IsCancellationRequested) return;
                        try { directory.VerifyLink(parentDirectory, child.Name); }
                        catch (Exception ex) when (ex is ScopeChangedException || DiskScanner.IsFileError(ex)) { unavailable(child, ex); }
                    }, fail));
                    Interlocked.Increment(ref pending);
                    if (workers > 1 && queue.Count < workers) queue.Add(work);
                    else stack.Push(new(work));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { fail(ex); }
        finally { while (stack.TryPop(out var frame)) { frame.Dispose(); Finish(frame.Work); } }
    }
    void Finish(Work work)
    {
        work.Lease.Release();
        if (Interlocked.Decrement(ref pending) == 0) queue.CompleteAdding();
    }
    sealed record Work(ScanNode Node, Lease Lease);
    sealed class Frame(Work work) : IDisposable
    {
        internal Work Work { get; } = work;
        internal IEnumerator<ScanNode>? Children;
        public void Dispose() => Children?.Dispose();
    }
    sealed class Lease(NativeDirectory directory, bool owns, Lease? parent, Action? close, Action<Exception> failure)
    {
        int references = 1;
        readonly bool ownsHandle = owns;
        readonly Lease? parentLease = parent;
        readonly Action? closeAction = close;
        readonly Action<Exception> reportFailure = failure;
        internal NativeDirectory Directory { get; } = AddParent(directory, parent);
        static NativeDirectory AddParent(NativeDirectory value, Lease? parent)
        { if (parent != null) Interlocked.Increment(ref parent.references); return value; }
        internal void Release()
        {
            // Iterative release keeps very deep trees off the managed call stack.
            for (Lease? current = this; current != null && Interlocked.Decrement(ref current.references) == 0; current = current.parentLease)
            {
                try { current.closeAction?.Invoke(); }
                catch (Exception ex) { current.reportFailure(ex); }
                finally { if (current.ownsHandle) current.Directory.Dispose(); }
            }
        }
    }
}
