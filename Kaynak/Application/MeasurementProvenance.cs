namespace YerelDisk.Presentation;

internal static class MeasurementProvenance
{
    internal static string Summary(ScanResult scan, DateTimeOffset baseline)
        => !scan.HasMixedMeasurementTimes ? "Tam kapsam ölçümü · " + Local(baseline)
        : "Karma ölçüm · Son klasör yenilemesi " + Local(scan.RefreshedAreas.Max(area => area.MeasuredAt))
            + ". Diğer alanlar önceki ölçümlerini korur. Referans / karşılaştırma için tüm kapsamı yeniden ölçün.";

    internal static string ForRow(ScanResult scan, DateTimeOffset baseline, object? row, bool shortText = false)
    {
        if (!scan.HasMixedMeasurementTimes) return shortText ? "Tam ölçüm" : Summary(scan, baseline);
        string? path = row == null ? null : RowValues.PathOf(row);
        bool folder = row is FolderEntry or ReviewCandidate;
        if (path == null || path == scan.Root.Path || (folder && scan.RefreshedAreas.Any(area => area.Path != path && RefreshPlanner.Below(area.Path, path))))
            return shortText ? "Karma zaman" : "Bu toplam farklı zamanlarda ölçülen alanlar içerir. " + Summary(scan, baseline);
        var area = scan.RefreshedAreas.Where(area => RefreshPlanner.Below(path, area.Path)).MaxBy(area => area.Path.Length);
        return area != null ? shortText ? area.MeasuredAt == scan.RefreshedAreas.Max(item => item.MeasuredAt) ? "Yenilenen alan" : "Önceki yenileme" : "Alan ölçümü · " + Local(area.MeasuredAt)
            : shortText ? "Önceki ölçüm" : "Önceki tam ölçüm · " + Local(baseline) + ". Bu alan seçili yenilemede yeniden taranmadı.";
    }

    internal static SelectionDetails Enrich(SelectionDetails details, ScanResult? scan, object? row, DateTimeOffset baseline)
    {
        if (scan?.HasMixedMeasurementTimes != true) return details;
        string text = ForRow(scan, baseline, row);
        return details with { Sections = new[] { new DetailSection("Ölçüm zamanı", text, true) }.Concat(details.Sections).ToArray(),
            FullText = "Ölçüm zamanı\n" + text + "\n\n" + details.FullText };
    }
    static string Local(DateTimeOffset time) => time == default ? "zaman bilinmiyor" : time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss.fff");
}
