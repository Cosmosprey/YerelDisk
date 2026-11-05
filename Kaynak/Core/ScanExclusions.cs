namespace YerelDisk;

// Immutable lexical scope. Never probes or opens an excluded target. The parent
// enumeration still sees its name, but it is omitted before fingerprinting/opening.
internal sealed class ScanExclusions
{
    internal static ScanExclusions None { get; } = new([]);
    readonly Dictionary<string, HashSet<string>> namesByParent = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<string> Paths { get; }
    ScanExclusions(string[] paths)
    {
        Paths = Array.AsReadOnly(paths);
        foreach (string path in paths)
        {
            string parent = Path.GetDirectoryName(path)!;
            if (!namesByParent.TryGetValue(parent, out var names)) namesByParent[parent] = names = new(StringComparer.OrdinalIgnoreCase);
            names.Add(Path.GetFileName(path));
        }
    }
    internal static ScanExclusions Create(IEnumerable<string> roots, IEnumerable<string>? paths)
    {
        string[] rootPaths = roots.ToArray();
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string input in paths ?? [])
        {
            string value = input.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal)
                || value.Length < 3 || value[1] != ':' || value[2] != '\\'
                || value.AsSpan(2).IndexOfAny(':', '*', '?') >= 0)
                throw new ArgumentException("Hariç tutulacak konum için tam bir yerel klasör yolu yazın: " + input);
            if (value.Split('\\').Skip(1).Any(part => part.EndsWith(' ') || part.EndsWith('.')))
                throw new ArgumentException("Sonunda nokta veya boşluk bulunan yol desteklenmiyor: " + input);
            string path = DiskScanner.NormalizeRootPath(value);
            if (rootPaths.Any(root => SameOrBelow(root, path)))
                throw new ArgumentException("Seçili kökün tamamı hariç tutulamaz; kökü listeden çıkarın: " + path);
            if (!rootPaths.Any(root => SameOrBelow(path, root)))
                throw new ArgumentException("Hariç tutulacak yol seçili köklerden birinin altında olmalı: " + path);
            normalized.Add(path);
        }
        string[] minimal = normalized.Where(path => !normalized.Any(parent =>
            !path.Equals(parent, StringComparison.OrdinalIgnoreCase) && SameOrBelow(path, parent)))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return minimal.Length == 0 ? None : new(minimal);
    }
    internal bool SkipsEntry(string parent, string name)
        => namesByParent.Count != 0 && namesByParent.TryGetValue(parent, out var names) && names.Contains(name);
    internal static bool SameOrBelow(string path, string parent)
    {
        int length = parent.Length;
        while (length > 0 && parent[length - 1] == '\\') length--;
        return path.AsSpan().StartsWith(parent.AsSpan(0, length), StringComparison.OrdinalIgnoreCase)
            && (path.Length == length || (path.Length > length && path[length] == '\\'));
    }

    internal void ApplyTo(ScanResult result)
    {
        result.ExcludedPaths = Paths;
        if (Paths.Count == 0) return;
        // Mark only observed ancestors. Excluded folders never become zero-byte rows.
        var ancestors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Paths)
            for (string? parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent))
            {
                string key = parent.TrimEnd('\\');
                ancestors[key] = ancestors.GetValueOrDefault(key) + 1;
            }
        var pending = new Stack<FolderEntry>(); pending.Push(result.Root);
        while (pending.TryPop(out var folder))
        {
            folder.ExcludedSubtrees = ancestors.GetValueOrDefault(folder.Path.TrimEnd('\\'));
            foreach (var child in folder.Children) pending.Push(child);
        }
        if (result.RootPaths.Count > 1) result.Root.ExcludedSubtrees = Paths.Count;
    }
}
