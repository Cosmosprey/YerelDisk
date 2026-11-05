using System.Globalization;

namespace YerelDisk.Presentation;

internal static class RowValues
{
    internal static string PathOf(object item) => item switch { FolderEntry f => f.Path, FileEntry f => f.Path, ReviewCandidate c => c.Path, ScanIssue i => i.Path, _ => "" };
    internal static string NameOf(object item) => item switch { FolderEntry f => f.Name, FileEntry f => f.Name, _ => Path.GetFileName(PathOf(item).TrimEnd('\\')) };
    internal static long SizeOf(object item) => item switch { FolderEntry f => f.LogicalBytes, FileEntry f => f.Size, ReviewCandidate c => c.LogicalBytes, _ => 0 };
    internal static long? AllocatedOf(object item) => item switch { FolderEntry { Measurement: MeasurementState.Unknown } => null, FolderEntry f => f.AllocatedBytes, FileEntry f => f.Allocated, ReviewCandidate c => c.AllocatedBytes, _ => null };
    internal static string MeasurementOf(object item) => item switch
    {
        FolderEntry f => (f.Measurement switch { MeasurementState.Unknown => "Boyut bilinmiyor", MeasurementState.Previous => "Son ölçüm · doğrulanamadı", MeasurementState.Partial => "Kısmi ölçüm", _ => "Ölçüldü" })
            + (f.ExcludedSubtrees > 0 ? " · hariç tutma var" : ""),
        FileEntry f => f.Unverified ? "Doğrulanamadı" : "Ölçüldü",
        ReviewCandidate c => (c.Incomplete ? "Kısmi / uyarılı" : "Taranan kapsam") + (c.ExcludedSubtrees > 0 ? " · hariç tutma var" : ""), _ => ""
    };
    internal static string Text(object item, string key) => key switch
    {
        "Name" => NameOf(item), "Path" => PathOf(item),
        "Size" => item is FolderEntry { Measurement: MeasurementState.Unknown } unknown ? "Boyut bilinmiyor" + (unknown.LastObservedBytes is { } old ? " · önceki " + PresentationText.Size(old) : "") : PresentationText.Size(SizeOf(item))
            + (item is FolderEntry { Measurement: MeasurementState.Previous } ? " · son ölçüm" : item is FolderEntry { Measurement: MeasurementState.Partial } ? " · kısmi" : item is FileEntry { Unverified: true } ? " · doğrulanamadı" : ""),
        "Allocated" => AllocatedOf(item) is { } n ? PresentationText.Size(n) : "Ölçülemedi",
        "Count" => item is FolderEntry f ? f.Measurement == MeasurementState.Unknown ? "Bilinmiyor" : f.FileCount.ToString("N0") : "",
        "Issues" => item is FolderEntry f ? f.IssueCount.ToString("N0") : "",
        "Streams" => item switch { FolderEntry { Measurement: MeasurementState.Unknown } => "Bilinmiyor", FileEntry f => $"{f.ExtraStreams} · {PresentationText.Size(f.ExtraStreamBytes)}", FolderEntry f => $"{f.OwnStreams} · {PresentationText.Size(f.OwnStreamBytes)}", _ => "" },
        "Modified" => item is FileEntry f ? f.Modified == DateTime.MinValue ? "Tarih bilinmiyor" : f.Modified.ToString("g") : "",
        "Hint" => item is FileEntry f ? f.Hint : "",
        "Kind" => item is ReviewCandidate c ? ReviewText.Kind(c.Kind) : "",
        "Evidence" => item is ReviewCandidate c ? c.Confidence + (c.Evidence is { DataRisks: not FileDataRisk.None } ? " · dosya türü ipucu" : "") : "",
        "ReviewPurpose" => item is ReviewCandidate c ? ReviewTableText.Purpose(c) : "",
        "ReviewSignal" => item is ReviewCandidate c ? ReviewTableText.Signal(c) : "",
        "ReviewContext" => item is ReviewCandidate c ? ReviewTableText.Context(c) : "",
        "ReviewEffect" => item is ReviewCandidate c ? ReviewTableText.Effect(c) : "",
        "ReviewStep" => item is ReviewCandidate c ? "Kontrol: " + ReviewGuidance.FirstStep(c) : "",
        "ReviewTooltip" => item is ReviewCandidate c ? ReviewTableText.Tooltip(c) : "",
        "ParentPath" => item is FolderEntry folder ? Path.GetDirectoryName(folder.Path.TrimEnd('\\')) ?? folder.Path : "",
        "Measurement" => MeasurementOf(item),
        "Reason" => item is ScanIssue i ? i.Reason : "", _ => ""
    };
    internal static int Compare(object a, object b, string key)
    {
        int order = key switch
        {
            "Size" => (a is FolderEntry { Measurement: MeasurementState.Unknown }, b is FolderEntry { Measurement: MeasurementState.Unknown }) switch
            { (true, false) => 1, (false, true) => -1, _ => SizeOf(a).CompareTo(SizeOf(b)) }, "Allocated" => Nullable.Compare(AllocatedOf(a), AllocatedOf(b)),
            "Count" => (a is FolderEntry f ? f.FileCount : 0).CompareTo(b is FolderEntry g ? g.FileCount : 0),
            "Issues" => (a is FolderEntry f ? f.IssueCount : 0).CompareTo(b is FolderEntry g ? g.IssueCount : 0),
            "Streams" => StreamsOf(a).CompareTo(StreamsOf(b)),
            "Modified" => (a is FileEntry f ? f.Modified : DateTime.MinValue).CompareTo(b is FileEntry g ? g.Modified : DateTime.MinValue),
            "ReviewSignal" => (a is ReviewCandidate ca ? ReviewTableText.WarningOrder(ca) : 5)
                .CompareTo(b is ReviewCandidate cb ? ReviewTableText.WarningOrder(cb) : 5),
            _ => StringComparer.CurrentCultureIgnoreCase.Compare(Text(a, key), Text(b, key))
        };
        return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(PathOf(a), PathOf(b));
    }
    static int StreamsOf(object item) => item switch { FileEntry f => f.ExtraStreams, FolderEntry f => f.OwnStreams, _ => 0 };
}
