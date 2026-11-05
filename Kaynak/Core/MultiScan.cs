using System.Diagnostics;

namespace YerelDisk;

internal static class MultiScan
{
    public static string[] NormalizeRoots(IEnumerable<string> paths)
        => Normalize(paths, DiskScanner.ValidateRoot);
    internal static string[] NormalizeRootPaths(IEnumerable<string> paths)
        => Normalize(paths, DiskScanner.NormalizeRootPath);
    static string[] Normalize(IEnumerable<string> paths, Func<string, string> normalize)
    {
        string[] input = paths.ToArray();
        string[] roots = input.Select(path =>
        {
            try { return normalize(path); }
            // Claude 1.2.1: with several roots, name the one that was rejected.
            catch (Exception ex) when (input.Length > 1 && ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            { throw new ArgumentException($"{path}: {ex.Message}", ex); }
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0) throw new ArgumentException("En az bir klasör seçin.");
        // A selected parent already includes its descendants. Respect component boundaries.
        return roots.Where(path => !roots.Any(parent => !path.Equals(parent, StringComparison.OrdinalIgnoreCase)
            && path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public static ScanResult Scan(IEnumerable<string> inputs, IProgress<ScanProgress>? progress, CancellationToken token,
        int fileLimit = ScanResult.FileLimit)
        => ScanConfigured(inputs, progress, token, fileLimit, 0);

    internal static ScanResult ScanConfigured(IEnumerable<string> inputs, IProgress<ScanProgress>? progress, CancellationToken token,
        int fileLimit, int workers)
    {
        if (fileLimit < 1) throw new ArgumentOutOfRangeException(nameof(fileLimit));
        if (workers < 0 || workers > 16) throw new ArgumentOutOfRangeException(nameof(workers));
        string[] roots = NormalizeRoots(inputs);
        if (roots.Length == 1) return DiskScanner.ScanCore(roots[0], progress, token, fileLimit, [], workers);
        ScanResult combined = new(new FolderEntry($"Seçilen klasörler ({roots.Length})", null));
        combined.RetainedFileLimit = fileLimit;
        combined.RootPaths.Clear(); combined.RootPaths.AddRange(roots);
        HashSet<(uint Volume, ulong Id)> seenLinks = [];
        Stopwatch watch = Stopwatch.StartNew();
        // Claude 1.2.1: roots left unscanned after a stop or scope change are listed as warnings,
        // so the result (and the CSV, which lists every selected root) does not look complete.
        void NotScanned(int from, string reason)
        {
            for (int i = from; i < roots.Length; i++)
            {
                combined.IssueCount++; combined.Root.IssueCount++;
                if (combined.Issues.Count < ScanResult.IssueLimit) combined.Issues.Add(new(roots[i], "Taranmadı: " + reason));
            }
        }
        for (int index = 0; index < roots.Length; index++)
        {
            if (token.IsCancellationRequested) { combined.Cancelled = true; NotScanned(index, "tarama durduruldu."); break; }
            int number = index + 1;
            var localProgress = new ForwardProgress(p => progress?.Report(new ScanProgress(
                combined.Root.FileCount + p.Files, combined.Folders + p.Folders,
                combined.Root.LogicalBytes + p.Bytes, combined.IssueCount + p.Issues,
                $"[{number}/{roots.Length}] {p.Current}", watch.Elapsed)));
            ScanResult part;
            try { part = DiskScanner.ScanForMerge(roots[index], localProgress, token, fileLimit, seenLinks, workers); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                combined.IssueCount++; combined.Root.IssueCount++;
                if (combined.Issues.Count < ScanResult.IssueLimit) combined.Issues.Add(new(roots[index], "Seçilen klasör taranamadı: " + ex.Message));
                combined.Failed = true; // Claude 1.3.1: a selected root is missing from the result
                continue;
            }
            // Only link completed in-memory results. During the actual scan every real root
            // still has Parent=null, preserving the strict root-binding check unchanged.
            part.Root.Parent = combined.Root;
            combined.Root.Children.Add(part.Root);
            combined.Root.LogicalBytes += part.Root.LogicalBytes;
            combined.Root.AllocatedBytes += part.Root.AllocatedBytes;
            combined.Root.FileCount += part.Root.FileCount;
            combined.Root.IssueCount += part.Root.IssueCount;
            combined.Folders += part.Folders;
            combined.IssueCount += part.IssueCount;
            combined.UnknownAllocation += part.UnknownAllocation;
            combined.HardLinkAliases += part.HardLinkAliases;
            combined.Workers = Math.Max(combined.Workers, part.Workers); // Claude 1.3.0
            combined.Failed |= part.Failed; // Claude 1.3.1: other roots are independent and still scanned
            combined.Issues.AddRange(part.Issues.Take(ScanResult.IssueLimit - combined.Issues.Count));
            combined.Files = combined.Files.Concat(part.Files).OrderByDescending(f => f.Size)
                .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(fileLimit).ToList();
            if (part.ScopeChanged) { combined.ScopeChanged = true; NotScanned(index + 1, "önceki kökte tarama kapsamı değişti."); break; }
            if (part.Cancelled) { combined.Cancelled = true; NotScanned(index + 1, "tarama durduruldu."); break; }
        }
        FileHints.Apply(combined);
        combined.Elapsed = watch.Elapsed;
        progress?.Report(new(combined.Root.FileCount, combined.Folders, combined.Root.LogicalBytes,
            combined.IssueCount, combined.Root.Path, combined.Elapsed));
        return combined;
    }
    sealed class ForwardProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    { public void Report(ScanProgress value) => report(value); }
}
