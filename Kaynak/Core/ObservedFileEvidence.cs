namespace YerelDisk;

// Sparse, session-only evidence. No per-file wrapper/full path is retained.
// Names are existing metadata strings; at most one sample per type and owner.
internal sealed class FolderRiskSummary
{
    long virtualDisks, mail, models, dumps;
    string? virtualSample, mailSample, modelSample, dumpSample;
    internal FileDataRisk Risks { get; private set; }
    internal long Count(FileDataRisk type) => type switch
    {
        FileDataRisk.VirtualDisk => virtualDisks, FileDataRisk.Mail => mail,
        FileDataRisk.Model => models, FileDataRisk.Diagnostics => dumps, _ => 0
    };
    internal string? Sample(FileDataRisk type) => type switch
    {
        FileDataRisk.VirtualDisk => virtualSample, FileDataRisk.Mail => mailSample,
        FileDataRisk.Model => modelSample, FileDataRisk.Diagnostics => dumpSample, _ => null
    };
    internal void Add(FileDataRisk type, string name)
    {
        switch (type)
        {
            case FileDataRisk.VirtualDisk: virtualDisks++; virtualSample ??= name; break;
            case FileDataRisk.Mail: mail++; mailSample ??= name; break;
            case FileDataRisk.Model: models++; modelSample ??= name; break;
            case FileDataRisk.Diagnostics: dumps++; dumpSample ??= name; break;
        }
        Risks |= type;
    }
}

internal sealed class ObservedFileEvidence
{
    readonly Dictionary<FolderEntry, FolderRiskSummary> owners = [];
    internal IReadOnlyDictionary<FolderEntry, FolderRiskSummary> Owners => owners;
    internal long VisitedNames { get; private set; }

    // Called after all root/binding and cross-root move checks, BEFORE releasing
    // the snapshots that already exist for repair/top-K refill. No extra I/O.
    // Do not throw on user cancellation: the engine returns a qualified partial
    // result. This finite metadata-only finalization is the same on that result.
    internal static ObservedFileEvidence Capture(IEnumerable<ScanResult> sources, ScanExclusions? exclusions = null)
    {
        var evidence = new ObservedFileEvidence();
        foreach (var source in sources)
        {
            if (source.FileCandidates == null) continue;
            FolderEntry? lastOwner = null;
            bool ownerUsable = false;
            foreach (var (owner, file) in source.FileCandidates)
            {
                evidence.VisitedNames++;
                if (file.Unverified) continue;
                var risk = FileHints.RiskFor(file.Name);
                if (risk == FileDataRisk.None || !CanonicalName(file.Name)) continue;
                if (!ReferenceEquals(lastOwner, owner))
                {
                    lastOwner = owner;
                    ownerUsable = owner.Measurement is not (MeasurementState.Unknown or MeasurementState.Previous)
                        && ReviewRules.CanonicalLocal(owner.Path) != null
                        && WithinRoots(source.RootPaths, owner.Path)
                        && source.CanUsePath(owner.Path);
                }
                if (!ownerUsable || exclusions?.SkipsEntry(owner.Path, file.Name) == true) continue;
                // Owner safety already covers every ancestor. Only an exact
                // file-level uncertainty needs a full path here.
                if (source.UnverifiedPaths.Count != 0 && source.UnverifiedPaths.Contains(Path.Combine(owner.Path, file.Name))) continue;
                if (!evidence.owners.TryGetValue(owner, out var summary)) evidence.owners[owner] = summary = new();
                summary.Add(risk, file.Name);
            }
        }
        return evidence;
    }
    static bool CanonicalName(string name) => name.Length > 0 && !name.EndsWith('.') && !name.EndsWith(' ')
        && name.AsSpan().IndexOfAny('\\', '/', ':') < 0 && !name.Contains('\0');
    static bool WithinRoots(IReadOnlyList<string> roots, string path)
    {
        for (int i = 0; i < roots.Count; i++) if (ScanExclusions.SameOrBelow(path, roots[i])) return true;
        return false;
    }
}
