namespace YerelDisk;

internal enum ReviewMatchKind { Unspecified, KnownLocation, FolderStructure }
internal enum FileClueSource { RetainedList, FinalScanObservations }
internal enum ReviewUsageState { NotExamined }
[Flags] internal enum FileDataRisk { None = 0, VirtualDisk = 1, Mail = 2, Model = 4, Diagnostics = 8 }
internal sealed record ReviewFileClue(FileDataRisk Risk, string Path);
internal sealed record ReviewEvidence(ReviewMatchKind Match, string MatchedPath,
    FileDataRisk DataRisks = FileDataRisk.None, long ClueCount = 0, IReadOnlyList<ReviewFileClue>? Examples = null,
    FileClueSource ClueSource = FileClueSource.RetainedList, ReviewUsageState Usage = ReviewUsageState.NotExamined);

// Positive clues from finalized metadata summaries, with an explicit retained-list fallback.
// No I/O; absence of clues never proves that a folder contains no personal data.
internal static class ReviewFileEvidence
{
    sealed class Clues
    {
        internal FileDataRisk Risks;
        internal long Count;
        internal readonly List<ReviewFileClue> Examples = [];
        internal void Add(FileDataRisk risk, string path)
        {
            // One example for each of the four finite risk types, never one per file.
            if ((Risks & risk) != risk) Examples.Add(new(risk, path));
            Risks |= risk; Count++;
        }
        internal void AddSummary(FolderEntry owner, FolderRiskSummary summary)
        {
            foreach (var type in types)
            {
                long count = summary.Count(type);
                if (count == 0) continue;
                if ((Risks & type) == 0 && summary.Sample(type) is { } name) Examples.Add(new(type, Path.Combine(owner.Path, name)));
                Risks |= type; Count += count;
            }
        }
    }
    static readonly FileDataRisk[] types = [FileDataRisk.VirtualDisk, FileDataRisk.Mail, FileDataRisk.Model, FileDataRisk.Diagnostics];
    internal static void Apply(ScanResult scan, List<ReviewCandidate> candidates, CancellationToken token)
    {
        if (candidates.Count == 0) return;
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < candidates.Count; i++) byPath.TryAdd(candidates[i].Path.TrimEnd('\\'), i);
        var clues = new Clues?[candidates.Count];
        if (scan.ObservedEvidence is { } metadata)
        {
            int visitedOwners = 0;
            foreach (var (owner, summary) in metadata.Owners)
            {
                if ((visitedOwners++ & 255) == 0) token.ThrowIfCancellationRequested();
                if (owner.Measurement is MeasurementState.Unknown or MeasurementState.Previous || !scan.CanUsePath(owner.Path)) continue;
                for (FolderEntry? current = owner; current != null; current = current.Parent)
                    if (byPath.TryGetValue(current.Path.TrimEnd('\\'), out int index))
                    {
                        (clues[index] ??= new()).AddSummary(owner, summary); break;
                    }
            }
        }
        else
        {
            bool checkBindings = scan.ScopeChanged || scan.UnverifiedPaths.Count != 0 || scan.ExcludedPaths.Count != 0;
            int visited = 0;
            foreach (var file in scan.Files)
            {
                if ((visited++ & 255) == 0) token.ThrowIfCancellationRequested();
                if (file.Unverified) continue;
                var risk = FileHints.RiskFor(file.Path);
                if (risk == FileDataRisk.None || ReviewRules.CanonicalLocal(file.Path) == null || (checkBindings && !scan.CanUsePath(file.Path))) continue;
                for (string? parent = Path.GetDirectoryName(file.Path); parent != null; parent = Path.GetDirectoryName(parent))
                    if (byPath.TryGetValue(parent.TrimEnd('\\'), out int index))
                    {
                        (clues[index] ??= new()).Add(risk, file.Path); break;
                    }
            }
        }
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Evidence is not { } evidence) continue;
            var candidate = candidates[i];
            evidence = evidence with { ClueSource = scan.ObservedEvidence != null ? FileClueSource.FinalScanObservations : FileClueSource.RetainedList };
            if (clues[i] is not { } observed)
            {
                candidates[i] = candidate with { Evidence = evidence }; continue;
            }
            candidates[i] = candidate with
            {
                Kind = candidate.Kind is ReviewKind.Cache or ReviewKind.Temporary ? ReviewKind.Conditional : candidate.Kind,
                Advice = "Önce ek dosya adı ipuçlarını ilgili uygulamada inceleyin ve veri/yedek ihtiyacını doğrulayın. Konum adına dayanarak toplu temizlik yapmayın. " + candidate.Advice,
                Evidence = evidence with { DataRisks = observed.Risks, ClueCount = observed.Count, Examples = observed.Examples.ToArray() }
            };
        }
    }
}
