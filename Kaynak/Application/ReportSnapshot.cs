using System.Globalization;
using System.Text;

namespace YerelDisk.Presentation;

// Only explicit export writes to the selected destination. The immutable snapshot
// prevents later UI activity from mixing filters, roots, or rows in one report.
internal sealed record ReportSnapshot(string[][] Metadata, object[] Items, WorkspaceView View, Dictionary<string, string> Feedback, bool Unrecognized = false, InventoryExport? Inventory = null)
{
    internal static ReportSnapshot Capture(WorkspaceViewModel model)
    {
        if (!model.CanExport || model.Scan is not { } scan || model.AppliedQuery is not { } query) throw new InvalidOperationException("Görünüm henüz dışa aktarmaya hazır değil.");
        List<string[]> metadata =
        [
            ["Görünüm", model.Title, "Uygulanan arama", model.AppliedSearch],
            ["Gösterilen kayıt", model.RowCount.ToString(CultureInfo.InvariantCulture), "Sıralama", model.SortDescription],
            ["Tarama durumu", WorkspaceViewModel.ResultStatus(scan)],
            ["Tarama süresi (saniye)", scan.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture), "İşçi", scan.Workers.ToString(CultureInfo.InvariantCulture), "Dosya listesi sınırı", scan.RetainedFileLimit.ToString(CultureInfo.InvariantCulture)],
            ["Tarama kapsamı", model.ScopeText],
            ["Ölçüm sınırı", ReviewText.Limits]
        ];
        if (scan.Timings is { } phases)
            metadata.Add(["İlk tarama (saniye)", phases.InitialScan.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture),
                "Doğrulama (saniye)", phases.Verification.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture),
                "Hedefli yenileme / bekleme (saniye)", phases.Refresh.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)]);
        foreach (string root in scan.RootPaths) metadata.Add(["Tarama kökü", root]);
        if (model.IsDistribution) metadata.Add(["Görsel sunum", "Alan dağılımı", "CSV kapsamı", "Filtrelenmiş klasör tablosunun tüm satırları; çubuk grupları ayrı kayıt değildir."]);
        metadata.Add(["Hariç tutulan yol sayısı", scan.ExcludedPaths.Count.ToString(CultureInfo.InvariantCulture), "Anlamı", "Kullanıcının kapsam tercihi; erişim hatası veya sıfır boyut değildir. Bu yolların varlığı / boyutu ölçülmedi; toplamlar içermez."]);
        foreach (string path in scan.ExcludedPaths) metadata.Add(["Hariç tutulan yol", path, "Durum", "Kapsam dışında; ölçülmedi"]);
        metadata.Add(["Yeniden ölçüm sayısı", scan.RetryCount.ToString(CultureInfo.InvariantCulture), "Doğrulanamayan konum sayısı", scan.UnverifiedPaths.Count.ToString(CultureInfo.InvariantCulture)]);
        metadata.Add(["Yenilenen klasör sayısı", scan.RefreshedFolders.ToString(CultureInfo.InvariantCulture), "Kontrol edilen klasör (turlar toplamı)", scan.VerifiedFolders.ToString(CultureInfo.InvariantCulture), "Doğrulama turu", scan.VerificationRounds.ToString(CultureInfo.InvariantCulture)]);
        foreach (string path in scan.UnverifiedPaths.Order(StringComparer.OrdinalIgnoreCase)) metadata.Add(["Doğrulanamayan konum", path, "Not", "Ölçüm eksik veya güncelliği doğrulanamadı; sıfır boyut değildir."]);
        foreach (string item in scan.VerificationHistory) metadata.Add(["Doğrulama geçmişi", item]);
        if (query.View == WorkspaceView.Folders && query.Folder is { } folder)
        {
            metadata.Add(["Görüntülenen klasör", folder.Path, "Klasör arama kapsamı", query.FullFolderPath ? "Tam yol" : "Klasör adı"]);
            metadata.Add(["Görüntülenen klasörün kendi ek akışları", folder.Measurement == MeasurementState.Unknown ? "" : folder.OwnStreams.ToString(CultureInfo.InvariantCulture), "Ek akış boyutu (bayt)", folder.Measurement == MeasurementState.Unknown ? "" : folder.OwnStreamBytes.ToString(CultureInfo.InvariantCulture)]);
        }
        if (model.Search.Trim() != model.AppliedSearch) metadata.Add(["Bekleyen arama", model.Search.Trim(), "Not", "Rapor uygulanan filtredeki görünür listeyi içerir."]);
        var feedback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (model.IsReview)
        {
            if (model.AppliedCounts is { } counts)
            {
                metadata.Add(["Filtre öncesi uygun liste kaydı", counts.Available.ToString(CultureInfo.InvariantCulture), "Filtrelenen kayıt", counts.Hidden.ToString(CultureInfo.InvariantCulture)]);
                metadata.Add(["Filtre nedenleri (sıralı, her kayıt bir kez)", "Boyut", counts.BySize.ToString(CultureInfo.InvariantCulture), "Tür", counts.ByKind.ToString(CultureInfo.InvariantCulture), "Arama", counts.BySearch.ToString(CultureInfo.InvariantCulture)]);
                metadata.Add(["Sayaç kapsamı", "Sayılar uygulanmış listedeki kayıtlardır; tüm diskin dosya/klasör toplamı veya geri kazanılabilir alan değildir."]);
            }
            metadata.Add(["DENEYSEL", "Amaç tanıma, gereksizlik veya silme güvenliği kanıtı değildir."]);
            metadata.Add(["İnceleme listesi", query.Unrecognized ? "Diğer büyük alanlar" : "Kural eşleşmeleri"]);
            metadata.Add(["Aday boyut eşiği (bayt)", query.ReviewMinimum.ToString(CultureInfo.InvariantCulture)]);
            if (query.Unrecognized)
            {
                metadata.Add(["Anlamı", UnrecognizedAreas.Notice]);
                metadata.Add(["Listenin kapsamı", UnrecognizedAreas.Coverage]);
                metadata.Add(["İlk kontrol adımı", UnrecognizedAreas.FirstStep]);
            }
            else
            {
                metadata.Add(["Tür filtresi", model.KindChoices.Single(c => c.Value == query.ReviewKind).Label]);
                metadata.Add(["Kişisel notların anlamı", ReviewText.FeedbackHelp]);
                foreach (var candidate in model.Rows.OfType<ReviewCandidate>()) feedback[candidate.Path] = model.FeedbackFor(candidate);
            }
        }
        var inventory = model.Inventory.CaptureExport(model.IsReview);
        if (model.IsReview) metadata.Add(["Uygulama kaydı ipuçları", inventory == null ? "Bu rapora dahil edilmedi; registry ad/yol bilgisi yazılmaz." : "Kullanıcının açık seçimiyle yalnız dışa aktarılan satır ilişkileri dahil edildi."]);
        return new(metadata.ToArray(), model.Rows.ToArray(), model.View, feedback, query.Unrecognized, inventory);
    }

    internal void Write(string destination)
    {
        using var writer = new StreamWriter(destination, false, new UTF8Encoding(true));
        Write(writer);
    }
    internal void Write(TextWriter writer)
    {
        foreach (string[] line in Metadata) writer.WriteLine(PresentationText.Csv(line));
        Inventory?.Write(writer, Items);
        writer.WriteLine(PresentationText.Csv(View switch
        {
            WorkspaceView.Review when !Unrecognized => ReviewText.CsvHeader,
            WorkspaceView.Files => ["Dosya", "Tüm veri akışları (bayt)", "Ayrılan alan (bayt)", "Ek akış sayısı", "Ek akış boyutu (bayt)", "Değiştirilme", "Konum", "İpucu", "Ölçüm durumu"],
            WorkspaceView.Issues => ["Konum", "Açıklama"],
            _ => ["Klasör", "Veri akışları (bayt)", "Ölçülen ayrılan alan (bayt)", "Dosya sayısı", "Uyarı", "Kendi ek akış sayısı", "Kendi ek akış boyutu (bayt)", "Konum", "Ölçüm durumu", "Önceki denemede gözlenen boyut (bayt; güncel değildir)"]
        }));
        foreach (var item in Items)
        {
            string N(long n) => n.ToString(CultureInfo.InvariantCulture);
            string[] cells = item switch
            {
                ReviewCandidate c => ReviewText.CsvFields(c, Feedback.GetValueOrDefault(c.Path, "Değerlendirilmedi")),
                FileEntry f => [f.Name, N(f.Size), f.Allocated.HasValue ? N(f.Allocated.Value) : "", N(f.ExtraStreams), N(f.ExtraStreamBytes), f.Modified == DateTime.MinValue ? "" : f.Modified.ToString("O"), f.Path, f.Hint, RowValues.MeasurementOf(f)],
                FolderEntry f => [f.Name, f.Measurement == MeasurementState.Unknown ? "" : N(f.LogicalBytes), f.Measurement == MeasurementState.Unknown ? "" : N(f.AllocatedBytes),
                    f.Measurement == MeasurementState.Unknown ? "" : N(f.FileCount), N(f.IssueCount), f.Measurement == MeasurementState.Unknown ? "" : N(f.OwnStreams),
                    f.Measurement == MeasurementState.Unknown ? "" : N(f.OwnStreamBytes), f.Path, RowValues.MeasurementOf(f), f.LastObservedBytes is { } previous ? N(previous) : ""],
                ScanIssue i => [i.Path, i.Reason], _ => throw new InvalidOperationException("Bilinmeyen rapor kaydı.")
            };
            writer.WriteLine(PresentationText.Csv(cells));
        }
    }
}
