using System.Globalization;

namespace YerelDisk.Presentation;

internal sealed record InventoryApplication(string Name, string Publisher, string Version, string InstallPath, string Sources);
internal enum InventoryReadState { Complete, Missing, Partial, Denied, Link, Error, Limited }
internal sealed record InventorySourceStatus(string Source, InventoryReadState State, int Visited, int Unusable, int Errors);
internal sealed class InventorySnapshot(DateTimeOffset started, DateTimeOffset finished,
    IEnumerable<InventoryApplication> applications, IEnumerable<InventorySourceStatus> sources)
{
    internal DateTimeOffset Started { get; } = started;
    internal DateTimeOffset Finished { get; } = finished;
    internal IReadOnlyList<InventoryApplication> Applications { get; } = Array.AsReadOnly(applications.ToArray());
    internal IReadOnlyList<InventorySourceStatus> Sources { get; } = Array.AsReadOnly(sources.ToArray());
    internal bool Partial => Sources.Any(s => s.State is not (InventoryReadState.Complete or InventoryReadState.Missing));
    internal bool Readable => Sources.Any(s => s.State is InventoryReadState.Complete or InventoryReadState.Missing or InventoryReadState.Partial);
    internal string Coverage => $"İnceleme: {Started.ToLocalTime():g} – {Finished.ToLocalTime():T}. "
        + string.Join(" · ", Sources.Select(s => $"{s.Source}: {StateText(s.State)}, {s.Visited:N0} anahtar, {s.Unusable:N0} kullanılamayan yol/kayıt, {s.Errors:N0} okuma/biçim uyarısı"))
        + ". Yalnız HKLM/HKCU Uninstall 32/64 görünümleri; tüm yazılımların eksiksiz listesi değildir. Kayıtların güncelliği ve uygulamaların kullanım durumu doğrulanmadı.";
    static string StateText(InventoryReadState state) => state switch
    { InventoryReadState.Complete => "okundu", InventoryReadState.Missing => "kaynak yok", InventoryReadState.Denied => "erişilemedi", InventoryReadState.Link => "yönlendirme atlandı", InventoryReadState.Limited => "bütçe doldu", InventoryReadState.Partial => "kısmi", _ => "okuma hatası" };
}
internal interface IInventoryProvider { Task<InventorySnapshot> ReadAsync(CancellationToken token); }

internal static class InventoryPaths
{
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) return null;
        string path = value.Trim();
        if (path.Length > 1 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
        path = path.Replace('/', '\\').TrimEnd('\\');
        if (path.Length <= 2 || path[1] != ':' || path[2] != '\\' || !char.IsAsciiLetter(path[0])
            || path.AsSpan(2).IndexOfAny(':', '%', '"') >= 0 || path.IndexOfAny(['*', '?', '<', '>', '|']) >= 0 || path.Any(char.IsControl)) return null;
        if (path[3..].Split('\\').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' '))) return null;
        try { return Path.GetFullPath(path).Equals(path, StringComparison.OrdinalIgnoreCase) ? path : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }
    internal static string? Text(string? text, int maximum)
    { if (text == null || text.Length > maximum || text.Any(char.IsControl)) return null; return text.Trim(); }
}

internal sealed record InventoryHit(InventoryApplication Application, bool Contained);
internal sealed class InventoryPathIndex
{
    readonly ScanResult scan;
    readonly Dictionary<string, InventoryApplication[]> byPath;
    readonly HashSet<string> observed;
    readonly string[] observedInstalls;
    internal InventorySnapshot Snapshot { get; }
    InventoryPathIndex(ScanResult scan, InventorySnapshot snapshot, Dictionary<string, InventoryApplication[]> byPath,
        HashSet<string> observed, string[] installs)
    { this.scan = scan; Snapshot = snapshot; this.byPath = byPath; this.observed = observed; observedInstalls = installs; }
    internal static InventoryPathIndex Create(ScanResult scan, InventorySnapshot snapshot, CancellationToken token)
    {
        var entries = new List<InventoryApplication>();
        foreach (var app in snapshot.Applications)
        {
            token.ThrowIfCancellationRequested();
            if (InventoryPaths.Normalize(app.InstallPath) is not { } path || InventoryPaths.Text(app.Name, 512) is not { Length: > 0 } name) continue;
            entries.Add(app with { Name = name, InstallPath = path, Publisher = InventoryPaths.Text(app.Publisher, 256) ?? "", Version = InventoryPaths.Text(app.Version, 128) ?? "" });
        }
        var byPath = entries.Distinct().GroupBy(a => a.InstallPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = scan.RootPaths.Select(ReviewRules.CanonicalLocal).OfType<string>().ToArray();
        bool Inside(string path) { foreach (string root in roots) if (ScanExclusions.SameOrBelow(path, root)) return true; return false; }
        var pending = new Stack<FolderEntry>();
        if (ReviewRules.CanonicalLocal(scan.Root.Path) is { } rootPath && Inside(rootPath)) pending.Push(scan.Root);
        else if (scan.RootPaths.Count > 1 && !Path.IsPathFullyQualified(scan.Root.Path))
            foreach (var child in scan.Root.Children) if (ReferenceEquals(child.Parent, scan.Root)) pending.Push(child);
        int visited = 0;
        while (pending.TryPop(out var node))
        {
            if ((visited++ & 255) == 0) token.ThrowIfCancellationRequested();
            if (ReviewRules.CanonicalLocal(node.Path) is not { } path || !Inside(path) || !scan.CanUsePath(path)
                || node.Measurement is MeasurementState.Unknown or MeasurementState.Previous || !observed.Add(path.TrimEnd('\\'))) continue;
            int childIndex = 0;
            foreach (var child in node.Children)
            {
                if ((childIndex++ & 255) == 0) token.ThrowIfCancellationRequested();
                if (ReferenceEquals(child.Parent, node) && ReviewRules.CanonicalLocal(child.Path) is { } childPath
                    && string.Equals(Path.GetDirectoryName(childPath.TrimEnd('\\'))?.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) pending.Push(child);
            }
        }
        var installs = byPath.Keys.Where(observed.Contains).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(scan, snapshot, byPath, observed, installs);
    }
    internal IReadOnlyList<InventoryHit> Matches(string path)
    {
        path = path.TrimEnd('\\');
        if (!observed.Contains(path) || !scan.CanUsePath(path)) return [];
        var hits = new List<InventoryHit>(); var seen = new HashSet<InventoryApplication>();
        for (string? parent = path; parent != null; parent = Path.GetDirectoryName(parent.TrimEnd('\\')))
            if (byPath.TryGetValue(parent, out var entries)) foreach (var app in entries) if (seen.Add(app)) hits.Add(new(app, false));
        string prefix = path + "\\";
        int low = 0, high = observedInstalls.Length;
        while (low < high) { int mid = low + (high - low) / 2; if (StringComparer.OrdinalIgnoreCase.Compare(observedInstalls[mid], prefix) < 0) low = mid + 1; else high = mid; }
        for (int i = low; i < observedInstalls.Length && observedInstalls[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase); i++)
            foreach (var app in byPath[observedInstalls[i]]) if (seen.Add(app)) hits.Add(new(app, true));
        return hits.OrderByDescending(h => h.Application.InstallPath.Length).ThenBy(h => h.Application.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal string Summary(string path)
    { var hits = Matches(path); return hits.Count == 0 ? "" : "Yol kaydı: " + hits[0].Application.Name + (hits.Count > 1 ? $" (+{hits.Count - 1})" : ""); }
    internal string Details(string path)
    {
        var hits = Matches(path);
        string result = hits.Count == 0 ? "İncelenen kaynaklarda bu gözlenen yolla eşleşen kurulum kaydı bulunamadı. Bu, uygulamanın kaldırıldığı veya verinin gereksiz olduğu anlamına gelmez."
            : $"{hits.Count:N0} ilgili yol kaydı. Gerçek sahiplik, kurulumun güncelliği, dosya ihtiyacı ve etkin kullanım doğrulanmadı.\n"
                + string.Join("\n\n", hits.Take(4).Select(h => HitText(h))) + (hits.Count > 4 ? $"\n\n{hits.Count - 4:N0} ek kayıt daha var; dört örnek gösterildi." : "");
        return result + "\n\n" + Snapshot.Coverage;
    }
    internal static string HitText(InventoryHit hit) => (hit.Contained ? "Alt ağaçta gözlenen kurulum yolu" : "Kurulum yolu kaydıyla örtüşme")
        + $": {hit.Application.Name}\nYayıncı: {hit.Application.Publisher} · Sürüm: {hit.Application.Version}\nBildirilen yol: {hit.Application.InstallPath}\nKaynak: {hit.Application.Sources}";
}
