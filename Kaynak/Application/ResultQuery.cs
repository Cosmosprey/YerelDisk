using System.Globalization;

namespace YerelDisk.Presentation;

internal sealed record ViewQuery(WorkspaceView View, FolderEntry? Folder, string Search, bool FullFolderPath,
    int ReviewMinimum, int ReviewKind, string SortKey, bool Descending, bool Unrecognized = false)
{
    internal string SortDescription => (SortKey switch
    {
        "Name" => "Ad", "Size" => "Boyut", "Allocated" => "Ayrılan alan", "Count" => "Dosya sayısı",
        "Issues" => "Uyarı", "Modified" => "Değiştirilme", "Streams" => "Ek akış sayısı",
        "Kind" => "Tür", "Evidence" => "Dayanak", "Measurement" => "Ölçüm", "ReviewSignal" => "Uyarı önceliği", "Reason" => "Açıklama", _ => "Konum"
    }) + (Descending ? " · azalan" : " · artan");
}

internal sealed record QueryFilterCounts(int Available, int BySize, int ByKind, int BySearch)
{
    internal int Hidden => BySize + ByKind + BySearch;
}
internal sealed record QueryResult(List<object> Rows, long Maximum, ViewQuery Query, UnrecognizedAreas? Areas = null, QueryFilterCounts? Counts = null);

internal static class ResultQuery
{
    internal static QueryResult Execute(ScanResult result, IReadOnlyList<ReviewCandidate> candidates, ViewQuery query, CancellationToken token, UnrecognizedAreas? cachedAreas = null)
    {
        token.ThrowIfCancellationRequested();
        var areas = query.View == WorkspaceView.Review && query.Unrecognized ? cachedAreas ?? UnrecognizedAreas.Create(result, candidates, token) : null;
        IEnumerable<object> input = query.View switch
        {
            WorkspaceView.Folders => query.Folder?.Children ?? [],
            WorkspaceView.Files => result.Files,
            WorkspaceView.Review when query.Unrecognized => areas!.Folders,
            WorkspaceView.Review => result.ScopeChanged && result.UnverifiedPaths.Count == 0 ? [] : candidates.Where(c => result.CanUsePath(c.Path)),
            WorkspaceView.Issues => result.Issues,
            _ => []
        };
        var rows = new List<object>(); int index = 0, bySize = 0, byKind = 0, bySearch = 0; long maximum = 0;
        var comparison = CultureInfo.CurrentCulture.CompareInfo;
        foreach (var item in input)
        {
            if ((index++ & 1023) == 0) token.ThrowIfCancellationRequested();
            if (query.View == WorkspaceView.Review && RowValues.SizeOf(item) < query.ReviewMinimum) { bySize++; continue; }
            if (item is ReviewCandidate c && (query.ReviewKind switch
            {
                1 => c.Kind is not (ReviewKind.Cache or ReviewKind.Temporary),
                2 => c.Kind != ReviewKind.Conditional, 3 => c.Kind != ReviewKind.Protected, _ => false
            })) { byKind++; continue; }
            string text = item is FolderEntry f && query.View == WorkspaceView.Folders && !query.FullFolderPath ? f.Name : RowValues.PathOf(item);
            if (query.Search.Length == 0 || comparison.IndexOf(text, query.Search, CompareOptions.IgnoreCase) >= 0)
            {
                rows.Add(item); maximum = Math.Max(maximum, RowValues.SizeOf(item));
            }
            else bySearch++;
        }
        int comparisons = 0;
        rows.Sort((a, b) =>
        {
            if ((++comparisons & 2047) == 0) token.ThrowIfCancellationRequested();
            return query.Descending ? RowValues.Compare(b, a, query.SortKey) : RowValues.Compare(a, b, query.SortKey);
        });
        token.ThrowIfCancellationRequested();
        return new(rows, maximum, query, areas, new(index, bySize, byKind, bySearch));
    }
}
