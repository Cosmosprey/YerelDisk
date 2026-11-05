namespace YerelDisk.Presentation;

// Compact presentation of the existing rules/evidence. No new classification,
// machine inspection, content reading or path-keyed cache.
internal static class ReviewTableText
{
    static readonly string[] riskLabels = Enumerable.Range(0, 16).Select(index =>
    {
        var risk = (FileDataRisk)index;
        var names = new List<string>(4);
        if ((risk & FileDataRisk.VirtualDisk) != 0) names.Add("Sanal disk");
        if ((risk & FileDataRisk.Mail) != 0) names.Add("Posta");
        if ((risk & FileDataRisk.Model) != 0) names.Add("Model");
        if ((risk & FileDataRisk.Diagnostics) != 0) names.Add("Döküm");
        return string.Join(" / ", names);
    }).ToArray();

    internal static string Purpose(ReviewCandidate c) => c.RuleId switch
    {
        "GRADLE-CACHES" => "Gradle paket / derleme önbelleği",
        "GRADLE-DISTS" => "Gradle sürüm indirmeleri",
        "NPM-CACHE" => "npm paket önbelleği",
        "NUGET-PACKAGES" => "NuGet ortak paket deposu",
        "NUGET-HTTP" or "NUGET-PLUGINS" => "NuGet yardımcı önbelleği",
        "CARGO-ARCHIVES" => "Cargo paket arşivleri",
        "CARGO-SOURCES" => "Cargo paket kaynakları",
        "HF-HUB" => "Hugging Face model / veri deposu",
        "OLLAMA-MODELS" => "Ollama yerel modelleri",
        "ANDROID-AVD" => "Android sanal cihaz verileri",
        "ANDROID-SDK-STRUCTURE" => "Olası Android SDK yapısı",
        "WINDOWS-OLD" => "Önceki Windows / eski dosyalar",
        "PIP-CACHE" => "Python paket önbelleği",
        "CRASH-DUMPS" => "Uygulama hata dökümleri",
        "NVIDIA-DX" or "NVIDIA-GL" => "Grafik önbelleği",
        "USER-TEMP" or "WINDOWS-TEMP" => "Geçici çalışma / kurtarma alanı",
        "PACKAGE-CACHE" => "Uygulama kurulum paketleri",
        "WINDOWS-INSTALLER" => "Windows Installer bakım paketleri",
        "WINDOWS-COMPONENTS" => "Windows bileşen deposu",
        "GAME-Naughty Dog" or "GAME-Hogwarts Legacy" => "Oyunla ilişkili veriler",
        "BROWSER-CACHE" => "Tarayıcı profil önbelleği",
        _ => "Amaç belirlenemedi"
    };

    internal static bool Attention(ReviewCandidate c) => c.Kind == ReviewKind.Protected || c.Incomplete || c.ExcludedSubtrees > 0
        || c.Evidence is { DataRisks: not FileDataRisk.None }
        || c.Evidence?.Match is not (ReviewMatchKind.KnownLocation or ReviewMatchKind.FolderStructure);
    internal static string Signal(ReviewCandidate c)
    {
        var risk = c.Evidence?.DataRisks ?? FileDataRisk.None;
        int known = (int)(risk & ReviewRiskText.Known);
        string label = ReviewRiskText.HasUnknown(risk) ? (known == 0 ? "Ek tür" : riskLabels[known] + " / Ek tür") : riskLabels[known];
        return ReviewText.Kind(c.Kind) + (risk == FileDataRisk.None ? ""
            : " · " + label + " ipucu") + (c.Incomplete ? " · Kısmi" : "")
            + (c.Evidence?.Match is not (ReviewMatchKind.KnownLocation or ReviewMatchKind.FolderStructure) ? " · Dayanak belirsiz" : "")
            + (c.Evidence?.Usage != ReviewUsageState.NotExamined ? " · Kullanım bilinmiyor" : "");
    }
    internal static string Context(ReviewCandidate c)
    {
        string relation = c.Evidence?.Match switch
        {
            ReviewMatchKind.KnownLocation => "Konum ipucu",
            ReviewMatchKind.FolderStructure => "Yapı ipucu",
            _ => "Dayanak belirsiz"
        };
        return relation + " · Kullanım: " + ReviewText.UsageLabel(c);
    }
    internal static string Effect(ReviewCandidate c)
    {
        if (c.Evidence is { DataRisks: not FileDataRisk.None })
            return c.Kind == ReviewKind.Protected ? "Özel / gerekli veri riski; elle silmeyin."
                : "Özel veya gerekli veri kaybolabilir; F6 ile inceleyin.";
        if (c.Kind == ReviewKind.Protected && c.RuleId is not ("WINDOWS-OLD" or "PACKAGE-CACHE" or "WINDOWS-INSTALLER" or "WINDOWS-COMPONENTS"))
            return "Elle silmeyin; kaldırma etkisi belirsiz.";
        return c.RuleId switch
        {
            "GRADLE-CACHES" or "GRADLE-DISTS" or "NPM-CACHE" or "NUGET-HTTP" or "NUGET-PLUGINS"
                or "CARGO-ARCHIVES" or "PIP-CACHE" => "Yeniden indirme / hazırlık gerekebilir.",
            "NUGET-PACKAGES" => "Projelerin paket erişimi etkilenebilir.",
            "CARGO-SOURCES" => "Derleme kaynakları / yerel değişiklikler kaybolabilir.",
            "HF-HUB" or "OLLAMA-MODELS" => "Gerekli model veya veri sürümleri kaybolabilir.",
            "ANDROID-AVD" => "Sanal cihazın kullanıcı / test verileri kaybolabilir.",
            "ANDROID-SDK-STRUCTURE" => "Android araçları ve derlemeler etkilenebilir.",
            "WINDOWS-OLD" => "Eski dosyalar / Windows'a dönüş olanağı kaybolabilir.",
            "CRASH-DUMPS" => "Hata incelemesi için gerekli kanıt kaybolabilir.",
            "NVIDIA-DX" or "NVIDIA-GL" => "İlk açılışlarda hazırlık ve takılma olabilir.",
            "USER-TEMP" or "WINDOWS-TEMP" => "Etkin işlem veya kurtarma verisi etkilenebilir.",
            "PACKAGE-CACHE" or "WINDOWS-INSTALLER" => "Onarım, güncelleme veya kaldırma bozulabilir.",
            "WINDOWS-COMPONENTS" => "Windows bakımı ve özellikleri bozulabilir.",
            "GAME-Naughty Dog" or "GAME-Hogwarts Legacy" => "Kayıt, ayar veya önbellek kaybolabilir.",
            "BROWSER-CACHE" => "Sayfa / grafik verileri tekrar indirilebilir.",
            _ => "Kaldırma etkisi belirsiz; F6 ile inceleyin."
        };
    }
    // Header sorting only. This is warning order, never a safe-deletion score.
    internal static int WarningOrder(ReviewCandidate c) => c.Kind == ReviewKind.Protected ? 0
        : c.Evidence is { DataRisks: not FileDataRisk.None } ? 1 : c.Incomplete ? 2 : c.Kind == ReviewKind.Conditional ? 3 : c.Kind == ReviewKind.Temporary ? 4 : 5;
    internal static string Tooltip(ReviewCandidate c) => c.Path + "\n" + Purpose(c) + "\n" + Signal(c)
        + "\n" + Context(c) + "\n" + Effect(c) + "\nİlk kontrol: " + ReviewGuidance.FirstStep(c) + "\n" + RowValues.MeasurementOf(c)
        + "\n" + ReviewText.ClueCoverage(c) + "\nTam açıklama: F6.";
}
