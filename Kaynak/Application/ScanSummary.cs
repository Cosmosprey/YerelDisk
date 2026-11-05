namespace YerelDisk.Presentation;

// Formats completed observations only; never enumerates paths or infers reclaimable space.
internal sealed record ScanSummary(string Heading, string Totals, string Timing, string Coverage, string Limits)
{
    internal static ScanSummary Create(ScanResult scan)
    {
        string values = scan.Root.Measurement == MeasurementState.Unknown
            ? "Boyut, ayrılan alan ve dosya sayısı bilinmiyor; sıfır olarak değerlendirmeyin."
            : $"{RowValues.Text(scan.Root, "Size")} veri · {RowValues.Text(scan.Root, "Allocated")} ölçülen ayrılan alan · {scan.Root.FileCount:N0} dosya"
                + (scan.Root.Measurement == MeasurementState.Measured ? "" : " · dosya sayısı da son/kısmi gözlemdir");
        string timing = $"Toplam {Duration(scan.Elapsed)}";
        if (scan.Timings is { } phases)
        {
            var remainder = scan.Elapsed - phases.InitialScan - phases.Verification - phases.Refresh;
            if (remainder < TimeSpan.Zero) remainder = TimeSpan.Zero;
            timing += $" · İlk tarama {Duration(phases.InitialScan)} · Doğrulama {Duration(phases.Verification)}"
                + $" · Hedefli yenileme / bekleme {Duration(phases.Refresh)} · Diğer hazırlık {Duration(remainder)}";
        }
        else timing += " · Aşama süreleri mevcut değil";
        return new($"Tarama özeti · {WorkspaceViewModel.ResultStatus(scan)} · {Duration(scan.Elapsed)}", values, timing,
            $"{scan.IssueCount:N0} uyarı · {scan.UnverifiedPaths.Count:N0} doğrulanamayan konum · {scan.UnknownAllocation:N0} eksik alan / akış ölçümü\n"
                + $"{scan.RootPaths.Count:N0} seçili kök · {scan.Folders:N0} gözlenen klasör · {scan.VerifiedFolders:N0} klasör kontrolü · {scan.RefreshedFolders:N0} klasör yenilemesi (turlar toplamı)"
                + (scan.ExcludedPaths.Count > 0 ? $"\n{scan.ExcludedPaths.Count:N0} yol bilerek hariç tutuldu; varlıkları / boyutları ölçülmedi, toplamlar bunları içermez. Yol listesi F6 ve CSV'dedir." : ""),
            "Boyutlar silerek kazanılacak alanı göstermez. Bağlantılar ve bulut yer tutucuları izlenmez; canlı tarama bütün değişiklikleri yakalama garantisi vermez. Uyarı varsa ayrıntılarını inceleyin.");
    }

    internal static string Duration(TimeSpan value) => $"{value.TotalSeconds:N2} sn";
    internal static string PhaseName(ScanPhase phase) => phase switch
    {
        ScanPhase.Verification => "Doğrulama",
        ScanPhase.Refresh => "Hedefli yenileme / bekleme",
        ScanPhase.Finalizing => "Sonuç hazırlama",
        _ => "İlk tarama"
    };
}
