using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using YerelDisk;
using YerelDisk.Presentation;

internal static class CorpusTests
{
    const string Home = @"C:\Corpus-Fixture\User", Local = Home + @"\AppData\Local", Data = @"C:\ProgramData", Windows = @"C:\Windows";
    static readonly ReviewRules rules = new(Home, Local, Data, Windows);
    static string Expand(string path) => path.Replace("$HOME", Home, StringComparison.Ordinal).Replace("$LOCAL", Local, StringComparison.Ordinal).Replace("$DATA", Data, StringComparison.Ordinal).Replace("$WINDOWS", Windows, StringComparison.Ordinal);
    sealed record FileCase(string Path, bool Unverified = false);
    sealed record Case(string Id, string Theme, string Facts, string Path, string? Rule = null, string? Kind = null,
        string Risks = "None", string Source = "final", FileCase[]? Files = null, string[]? Retained = null,
        string[]? Excluded = null, string[]? Unverified = null, string[]? Markers = null,
        string[]? UnusableMarkers = null, bool Partial = false, bool ScopeChanged = false, bool Cancelled = false,
        int? FutureRisk = null, string[]? Consequence = null, string[]? Guidance = null, string[]? Signal = null);
    static ReviewCandidate? Execute(Case item)
    {
        string path = Expand(item.Path); var root = new FolderEntry(path, null) { LogicalBytes = 10000, AllocatedBytes = 12288, FileCount = 2, Measurement = item.Partial ? MeasurementState.Partial : MeasurementState.Measured };
        var scan = new ScanResult(root) { ScopeChanged = item.ScopeChanged, Cancelled = item.Cancelled };
        var folders = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase) { [path] = root };
        FolderEntry Owner(string ownerPath)
        {
            if (folders.TryGetValue(ownerPath, out var existing)) return existing;
            var parent = Owner(System.IO.Path.GetDirectoryName(ownerPath)!); var node = new FolderEntry(ownerPath, parent); parent.Children.Add(node); folders[ownerPath] = node; return node;
        }
        foreach (string marker in item.Markers ?? [])
        { var folder = Owner(System.IO.Path.Combine(path, marker)); if ((item.UnusableMarkers ?? []).Contains(marker, StringComparer.Ordinal)) folder.Measurement = MeasurementState.Unknown; }
        scan.ExcludedPaths = (item.Excluded ?? []).Select(p => System.IO.Path.Combine(path, p)).ToArray(); root.ExcludedSubtrees = scan.ExcludedPaths.Count;
        foreach (string uncertain in item.Unverified ?? []) scan.UnverifiedPaths.Add(System.IO.Path.Combine(path, uncertain));
        string Full(string p) => p.StartsWith('$') || System.IO.Path.IsPathFullyQualified(p) ? Expand(p) : System.IO.Path.Combine(path, p);
        var observations = new List<(FolderEntry, FileObservation)>();
        foreach (var file in item.Files ?? [])
        {
            string full = Full(file.Path); scan.Files.Add(new(full, 1, 4096, DateTime.MinValue, false, "", Unverified: file.Unverified));
            // Outside-scope retained files are intentionally not observations of this root.
            if (ScanExclusions.SameOrBelow(full, path))
                observations.Add((Owner(System.IO.Path.GetDirectoryName(full)!), new(System.IO.Path.GetFileName(full), 1, 4096, DateTime.MinValue, 1, 0, false, 0, 0, file.Unverified, "")));
        }
        if (item.Source != "retained")
        {
            scan.FileCandidates = item.Source == "empty-final" ? [] : observations;
            scan.ObservedEvidence = ObservedFileEvidence.Capture([scan], ScanExclusions.Create(scan.RootPaths, scan.ExcludedPaths)); scan.FileCandidates = null;
            scan.Files.Clear(); foreach (string name in item.Retained ?? ["big.bin"]) scan.Files.Add(new(Full(name), 9000, 12288, DateTime.MinValue, false, ""));
        }
        var candidates = rules.Analyze(scan);
        if (item.Rule == null)
        { if (candidates.Count != 0) throw new Exception("Expected no recognized purpose; got " + string.Join(",", candidates.Select(c => c.RuleId))); return null; }
        if (candidates.Count != 1) throw new Exception("Expected one candidate; got " + candidates.Count);
        var candidate = candidates[0];
        if (item.FutureRisk is { } future) candidate = candidate with { Evidence = candidate.Evidence! with { DataRisks = (FileDataRisk)future, ClueCount = 1 } };
        if (candidate.RuleId != item.Rule || candidate.Kind.ToString() != item.Kind) throw new Exception($"Purpose/kind expected {item.Rule}/{item.Kind}, got {candidate.RuleId}/{candidate.Kind}");
        if (candidate.Evidence?.DataRisks != Enum.Parse<FileDataRisk>(item.Risks)) throw new Exception($"Risk expected {item.Risks}, got {candidate.Evidence?.DataRisks}");
        if (candidate.Incomplete != (item.Partial || item.Cancelled)) throw new Exception("Partial measurement flag differs");
        if (candidate.ExcludedSubtrees != (item.Excluded?.Length ?? 0)) throw new Exception("Excluded count differs");
        var e = ReviewText.Explain(candidate); string guidance = ReviewGuidance.Details(candidate), signal = ReviewTableText.Signal(candidate);
        if (candidate.Evidence?.Usage != ReviewUsageState.NotExamined || !e.Usage.StartsWith("İncelenmedi", StringComparison.Ordinal)) throw new Exception("Scenario facts were incorrectly turned into observed usage");
        if (!e.Unknown.Contains("gereksizlik kanıtı değildir", StringComparison.Ordinal)) throw new Exception("Absence of a deletion guarantee was lost");
        if (!e.Coverage.Contains("İpucu görülmemesi kişisel veri bulunmadığını kanıtlamaz", StringComparison.Ordinal)) throw new Exception("No-clue outcome was turned into no-personal-data evidence");
        foreach (string word in item.Consequence ?? []) if (!e.Consequence.Contains(word, StringComparison.OrdinalIgnoreCase)) throw new Exception("Missing consequence: " + word);
        foreach (string word in item.Guidance ?? []) if (!guidance.Contains(word, StringComparison.OrdinalIgnoreCase)) throw new Exception("Missing first check: " + word);
        foreach (string word in item.Signal ?? []) if (!signal.Contains(word, StringComparison.OrdinalIgnoreCase)) throw new Exception("Missing table signal: " + word);
        if (item.Excluded?.Length > 0 && !e.Coverage.Contains("hariç tutuldu", StringComparison.Ordinal)) throw new Exception("Excluded observation scope was hidden");
        if (item.FutureRisk != null)
        {
            var fields = ReviewText.CsvFields(candidate, "Değerlendirilmedi");
            if (!e.FileClues.Contains("tanımlanamayan", StringComparison.Ordinal) || !fields[15].Contains("tanımlanamayan", StringComparison.Ordinal)
                || !guidance.Contains("çözümlenemedi", StringComparison.Ordinal)) throw new Exception("Unknown risk type lacks explicit F6/CSV/check wording");
        }
        if (candidate.Kind == ReviewKind.Protected && (!signal.Contains("Elle silmeyin", StringComparison.Ordinal) || !guidance.Contains("Elle silme", StringComparison.Ordinal))) throw new Exception("Protected location lost prohibition");
        return candidate;
    }
    static int Main(string[] args)
    {
        string output = System.IO.Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        string corpus = System.IO.Path.Combine(AppContext.BaseDirectory, "Cases.json");
        var cases = JsonSerializer.Deserialize<Case[]>(File.ReadAllText(corpus), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var log = new List<string>(); var rows = new List<string> { PresentationText.Csv(["Örnek", "Tema", "Senaryoda bilinen (uygulamaya verilmez)", "Beklenen kural", "Beklenen tür", "Sonuç", "Açıklama"]) };
        int failures = 0;
        foreach (var item in cases)
        {
            try { Execute(item); log.Add("PASS " + item.Id + " · " + item.Theme); rows.Add(PresentationText.Csv([item.Id, item.Theme, item.Facts, item.Rule ?? "Eşleşme beklenmiyor", item.Kind ?? "Belirlenmez", "PASS", "Senaryo beklentileri karşılandı; kullanılmama kanıtı değildir."])); }
            catch (Exception ex) { failures++; log.Add("FAIL " + item.Id + ": " + ex.Message); rows.Add(PresentationText.Csv([item.Id, item.Theme, item.Facts, item.Rule ?? "Eşleşme beklenmiyor", item.Kind ?? "Belirlenmez", "FAIL", ex.Message])); }
        }
        string[] themes = cases.Select(c => c.Theme).Distinct(StringComparer.Ordinal).Order().ToArray();
        File.WriteAllLines(System.IO.Path.Combine(output, "matrix.csv"), rows);
        File.WriteAllText(System.IO.Path.Combine(output, "corpus.txt"), $"Cases={cases.Length}; distinct themes={themes.Length}\nSHA256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corpus)))}\nSynthetic metadata scenarios, not real user validation or a measured safe-deletion accuracy rate. Facts field is never passed to product methods.\n" + string.Join("\n", themes));
        if (cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cases.Length || cases.Any(c => string.IsNullOrWhiteSpace(c.Facts))) { failures++; log.Add("FAIL duplicate ID or missing scenario provenance"); }
        string final = failures == 0 ? $"ALL CHECKS PASSED ({cases.Length})" : $"FAILED SCENARIOS={failures}/{cases.Length}";
        log.Add(final); File.WriteAllLines(System.IO.Path.Combine(output, "results.txt"), log); foreach (string line in log.Where(s => s.StartsWith("FAIL", StringComparison.Ordinal))) Console.WriteLine(line); Console.WriteLine(final); return failures == 0 ? 0 : 1;
    }
}
