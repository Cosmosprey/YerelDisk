namespace YerelDisk.Presentation;

// Resolves only objects belonging to the current in-memory scan. No path I/O,
// spelling normalization or Explorer action is involved in this navigation.
internal static class ComparisonInspection
{
    internal static FolderEntry? Resolve(ScanResult scan, FolderChange? change)
        => change?.After != null ? FindMeasuredFolder(scan, change.Path) : null;

    internal static FolderEntry? FindMeasuredFolder(ScanResult scan, string path)
    {
        if (scan.Cancelled || !scan.CanUsePath(path)) return null;
        FolderEntry? current = Contains(scan.Root.Path, path) ? scan.Root : ChildContaining(scan.Root, path);
        while (current != null)
        {
            if (current.Measurement is MeasurementState.Unknown or MeasurementState.Previous) return null;
            if (string.Equals(current.Path, path, StringComparison.Ordinal))
                return current.Measurement == MeasurementState.Measured && current.IssueCount == 0 ? current : null;
            current = ChildContaining(current, path);
        }
        return null;
    }

    static FolderEntry? ChildContaining(FolderEntry parent, string path)
    {
        foreach (var child in parent.Children) if (Contains(child.Path, path)) return child;
        return null;
    }

    static bool Contains(string parent, string path)
        => path.StartsWith(parent, StringComparison.Ordinal)
            && (path.Length == parent.Length || parent.EndsWith('\\') || path[parent.Length] == '\\');

    internal static string Hint(ScanResult scan, FolderChange? change)
        => change == null ? "Ana listede incelemek için bir fark satırı seçin."
            : change.After == null ? "Son ölçüm doğrulanamadı; bu satırdan ana listeye geçiş kapalı."
            : Resolve(scan, change) == null ? "Klasör son ölçümde yok veya doğrulanamadı; ana listede açılamaz."
            : "Ana listedeki tarama sonucuna geçer; yeniden tarama yapmaz.";
}
