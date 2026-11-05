namespace YerelDisk.Presentation;

internal sealed record DetailSection(string Heading, string Text, bool Caution = false);
internal sealed record SelectionDetails(string Title, string Path, string Summary, IReadOnlyList<DetailSection> Sections, string FullText)
{
    internal string PlainText => Summary + "\n\n" + string.Join("\n\n", Sections.Select(s => s.Heading + "\n" + s.Text));

    // One model per selected item, reused by the side pane and full detail window.
    // Text comes from the shared explanations; this layer never invents a rule.
    internal static SelectionDetails Create(ScanResult? scan, object? item, bool review, string feedback, string scope, bool unrecognized = false)
    {
        if (scan?.ScopeChanged == true && !scan.CanUsePath(item == null ? null : RowValues.PathOf(item)))
        {
            const string message = "Tarama sırasında kapsam değişti. Yollar başka öğelere işaret edebileceği için konum açma ve yol kopyalama kapalıdır. Yeniden tarayın.";
            return new("Kapsam değişti", "", "Kısmi sonuç", [new("Yeniden tarayın", message, true), new("Tarama kapsamı", scope)], message + "\n\n" + scope);
        }
        if (item is ReviewCandidate c)
        {
            var e = ReviewText.Explain(c);
            List<DetailSection> reviewSections = [];
            if (e.FileClues.Length > 0) reviewSections.Add(new("Ek dosya adı ipuçları", e.FileClues, true));
            reviewSections.AddRange([
                new("Kaldırma etkisi", e.Consequence, true),
                new("İlk kontrol adımı", ReviewGuidance.Details(c)),
                new("Amaç", e.Purpose),
                new("İlişki", e.Association),
                new("Kullanım bilgisi", e.Usage),
                new("Nasıl yönetmeli?", c.Advice),
                new("Neye dayanıyor?", e.Evidence + $"\n{c.Confidence} · {c.RuleId}"),
                new("Neyi bilmiyoruz?", e.Unknown),
                new("Kanıt kapsamı", e.Coverage),
                new("Ölçümün sınırı", ReviewText.Measurement(c)),
                new("Tarama kapsamı", scope)
            ]);
            return new(RowValues.NameOf(c), c.Path, $"{ReviewText.Kind(c.Kind)} · {PresentationText.Size(c.LogicalBytes)} · Kullanım: {ReviewText.UsageLabel(c)}" + (c.Incomplete ? " · Kısmi / uyarılı" : ""),
                reviewSections, ReviewText.Details(c, feedback) + "\n\nTarama kapsamı\n" + scope);
        }
        List<DetailSection> sections = [];
        if (unrecognized)
        {
            sections.Add(new("Amaç ve kullanım bilinmiyor", UnrecognizedAreas.Notice, true));
            sections.Add(new("İlk kontrol adımı", UnrecognizedAreas.FirstStep));
            sections.Add(new("Bu listenin kapsamı", UnrecognizedAreas.Coverage));
        }
        if (scan?.ExcludedPaths.Count > 0)
        {
            var paths = item is FolderEntry selected && !ReferenceEquals(selected, scan.Root)
                ? scan.ExcludedPaths.Where(path => ScanExclusions.SameOrBelow(path, selected.Path)) : scan.ExcludedPaths;
            string[] skipped = paths.ToArray();
            if (skipped.Length > 0) sections.Add(new("Bilerek hariç tutulan yollar", "Bu alanlar ölçülmedi; sıfır bayt veya erişim hatası değildir. Toplamlar bunları içermez.\n" + string.Join("\n", skipped), true));
        }
        if (item is FolderEntry { Measurement: not MeasurementState.Measured } measuredFolder)
            sections.Add(new("Ölçüm durumu", RowValues.MeasurementOf(measuredFolder) + ". " +
                (measuredFolder.Measurement == MeasurementState.Unknown ? "Bu klasör ölçülemedi; sıfır bayt veya boş klasör olduğu anlamına gelmez."
                    + (measuredFolder.LastObservedBytes is { } previous ? " Bu yolda önceki denemede gözlenen boyut: " + PresentationText.Size(previous) + ". Güncel boyut değildir." : "")
                    : measuredFolder.Measurement == MeasurementState.Previous ? "Görünen değerler son gözlemdir; güncel boyut doğrulanamadı. Yeniden doğrulayın."
                    : "Toplam yalnız ölçülebilen kısmı içerir; ölçülemeyen alanlarda büyük dosyalar bulunabilir."), true));
        if (item is FileEntry f)
        {
            if (f.Unverified) sections.Add(new("Ölçüm durumu", "Görünen boyut son gözlemdir; dosyanın kimliği / güncel ölçümü doğrulanamadı. Konum işlemleri kapalıdır; yeniden doğrulayın.", true));
            if (f.HardLinked) sections.Add(new("Paylaşılan veri", "Hard link: başka adlarla aynı veriyi paylaşır; bir adı silmek alan açmayabilir.", true));
            sections.Add(new("İnceleme notu", f.Hint));
            sections.Add(new("Dosya bilgileri", $"Ayrılan alan: {(f.Allocated.HasValue ? PresentationText.Size(f.Allocated.Value) : "Ölçülemedi")}\nDeğiştirilme: {(f.Modified == DateTime.MinValue ? "Tarih bilinmiyor" : f.Modified.ToString("g"))}\nEk akışlar: {f.ExtraStreams:N0} · {PresentationText.Size(f.ExtraStreamBytes)}"));
        }
        else if (item is FolderEntry folder)
        {
            sections.Add(new("Klasör bilgileri", folder.Measurement == MeasurementState.Unknown ? "Dosya sayısı, boyut ve ayrılan alan bilinmiyor."
                : $"{folder.FileCount:N0} dosya · {folder.IssueCount:N0} uyarı\nÖlçülen ayrılan alan: {PresentationText.Size(folder.AllocatedBytes)}\nKlasörün kendi ek akışları: {folder.OwnStreams:N0} · {PresentationText.Size(folder.OwnStreamBytes)}"));
        }
        else if (item is ScanIssue issue) sections.Add(new("Ölçüm uyarısı", issue.Reason, true));
        else sections.Add(new("Nasıl kullanılır?", review ? "Bir aday seçerek amacını, kaldırmanın olası etkisini ve inceleme koşullarını okuyun. Gereksizlik ve etkin kullanım doğrulanmaz." : "Bir kayıt seçerek konumunu, boyutunu ve açıklamasını inceleyin."));
        sections.Add(new("Ölçümün sınırı", ReviewText.Limits));
        sections.Add(new("Tarama kapsamı", scope));
        string title = item is ScanIssue ? "Tarama uyarısı" : item == null ? "Bir kayıt seçin" : RowValues.NameOf(item);
        string path = item == null ? "" : RowValues.PathOf(item);
        string summary = item is FileEntry or FolderEntry ? RowValues.Text(item, "Size") : "";
        var result = new SelectionDetails(title, path, summary, sections, "");
        return result with { FullText = path + "\n\n" + result.PlainText };
    }
}
