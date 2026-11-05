using System.Globalization;

namespace YerelDisk;

internal sealed record ReviewExplanation(string Purpose, string Evidence, string Consequence, string Unknown, string FileClues = "",
    string Association = "", string Usage = "", string Coverage = "");

// These are explicit explanations of expected purpose, not content or usage diagnoses.
internal static class ReviewText
{
    internal const string Limits = "Taranan boyut, dosya veri akışlarının mantıksal toplamıdır. Ölçülen ayrılan alan diskteki veri alanı ölçümüdür; ölçülemeyen öğeler bulunabilir. Hard linklerle paylaşılan veri, atlanan alanlar ve değişen dosyalar nedeniyle bu değerler silmeyle kazanılacak alan garantisi değildir. Sonuçlar tarama anındaki seçili kapsamı anlatır.";
    internal const string FeedbackHelp = "Faydalı aday: incelemene yardımcı oldu. Yanlış eşleşme: tanınan amaç / konumun yanlış olduğunu düşünüyorsun; bu teknik doğrulama değildir. Tutacağım: bu veriyi kullanıyorsun veya korumak istiyorsun; eşleşmenin yanlış olduğu anlamına gelmez. İşaretler oturumluk kişisel notlardır; kuralları eğitmez ve silme izni vermez.";

    internal static string Kind(ReviewKind kind) => kind switch
    {
        ReviewKind.Cache => "Önbellek",
        ReviewKind.Temporary => "Geçici alan · kontrol et",
        ReviewKind.Protected => "Elle silmeyin",
        _ => "Kullanımına bağlı"
    };

    internal static ReviewExplanation Explain(ReviewCandidate candidate)
    {
        (string purpose, string consequence) = candidate.RuleId switch
        {
            "GRADLE-CACHES" => ("Gradle projelerinin indirdiği paketler ve oluşturduğu yardımcı derleme verileri için bilinen konum.", "Bir sonraki derleme yeniden indirme / hazırlık isteyebilir. Kaynaklara erişilemiyorsa derleme çalışmayabilir."),
            "GRADLE-DISTS" => ("Projelerin belirli Gradle sürümlerini çalıştırmak için indirdiği dağıtımların bilinen konumu.", "İlgili Gradle sürümünün yeniden indirilmesi gerekebilir; çevrimdışı projeler çalışmayabilir."),
            "NPM-CACHE" => ("JavaScript araçlarının indirdiği npm paketlerinin önbelleği için bilinen konum.", "Sonraki paket kurulumu yeniden indirme isteyebilir; paket kaynağına erişim yoksa tamamlanamayabilir."),
            "NUGET-PACKAGES" => (".NET projelerinin doğrudan kullanabildiği ortak NuGet paket deposunun bilinen konumu.", "Projeler ihtiyaç duydukları paketleri kaybedebilir; yeniden indirme mümkün değilse derleme durabilir."),
            "NUGET-HTTP" or "NUGET-PLUGINS" => ("NuGet paket erişimi / eklentileri için yardımcı önbelleklerin bilinen konumu.", "Paket erişimi veya eklentiler yeniden hazırlık isteyebilir; çevrimdışı kullanım etkilenebilir."),
            "CARGO-ARCHIVES" => ("Rust projeleri için indirilen Cargo paket arşivlerinin bilinen konumu.", "Paketler yeniden indirilebilir; kaynağa erişilemiyorsa sonraki derlemeler engellenebilir."),
            "CARGO-SOURCES" => ("Rust derlemelerinde kullanılan açılmış paket kaynaklarının bilinen konumu; yerel değişiklikler bulunabilir.", "Derleme kaynakları ve varsa yerel değişiklikler kaybolabilir. Yeniden indirmek yerel değişiklikleri geri getirmez."),
            "HF-HUB" => ("Yerel yapay zekâ modelleri, veri setleri ve bunların sürümleri için Hugging Face Hub'ın bilinen konumu.", "Kullanılan model / veri sürümleri kaybolabilir. Büyük indirme gerekebilir; özel veya artık erişilemeyen veriler geri gelmeyebilir."),
            "OLLAMA-MODELS" => ("Ollama ile kullanılan yerel modellerin kendileri için bilinen konum; sıradan geçici veri değildir.", "Yerel modeller çalışmayabilir. Özel modeller geri getirilemeyebilir; elle parça silmek depoyu bozabilir."),
            "ANDROID-AVD" => ("Android sanal cihazlarının uygulama / kullanıcı verileri ve anlık görüntüleri için bilinen konum.", "Sanal cihaz içindeki çalışmalar ve test verileri kaybolabilir; yalnız sistem imajını indirmek bunları geri getirmez."),
            "WINDOWS-OLD" => ("Önceki Windows yüklemesi ve eski kullanıcı dosyalarının bulunabildiği bilinen konum.", "Önceki Windows'a dönüş olanağı ve henüz taşınmamış kişisel dosyalar kaybolabilir."),
            "PIP-CACHE" => ("Python paketlerinin indirme / derleme önbelleği için bilinen konum; Python ortamının kendisi değildir.", "Paketlerin yeniden indirilmesi veya derlenmesi gerekebilir; kaynak erişimi yoksa bu mümkün olmayabilir."),
            "CRASH-DUMPS" => ("Uygulama arızalarını incelemek için oluşturulan çökme dökümlerinin bilinen konumu.", "Destek veya hata incelemesi için gerekli kanıt kaybolabilir; aynı arıza yeniden üretilemeyebilir."),
            "NVIDIA-DX" or "NVIDIA-GL" => ("Oyun / grafik uygulamalarının tekrar kullanmak için hazırladığı grafik verilerinin bilinen önbellek konumu.", "Grafik verilerinin yeniden hazırlanması ilk çalıştırmalarda takılma ve daha uzun yükleme yaratabilir."),
            "USER-TEMP" or "WINDOWS-TEMP" => ("Uygulamaların veya Windows'un geçici çalışma alanı; etkin işlem ve kurtarma verileri de bulunabilir.", "Açık işlemler kesintiye uğrayabilir veya kurtarılabilecek çalışmalar kaybolabilir. Geçici adı, kullanılmadığı anlamına gelmez."),
            "PACKAGE-CACHE" => ("Uygulamaların kurulum, onarım, güncelleme veya kaldırma sırasında kullanabildiği ortak paket konumu.", "Kurulu uygulamaların onarım / güncelleme / kaldırma işlemleri bozulabilir. Elle topluca silmeyin."),
            "WINDOWS-INSTALLER" => ("Windows Installer ile kurulan ürünlerin bakım paketlerinin bilinen konumu.", "Uygulamaların onarılması, güncellenmesi veya kaldırılması bozulabilir. Elle silmeyin."),
            "WINDOWS-COMPONENTS" => ("Windows özellikleri ve güncellemeleri için kullanılan bileşen deposunun bilinen konumu.", "Windows bakımı ve özellikleri bozulabilir. Elle silmeyin; paylaşılan dosyalar görünen boyutu da etkiler."),
            "GAME-Naughty Dog" or "GAME-Hogwarts Legacy" => ("Oyunla ilişkili verilerin bulunabildiği bilinen konum; kayıt, ayar ve önbellek ayrımı yapılmadı.", "Oyun kayıtları / ayarları veya tekrar hazırlanması gereken veriler kaybolabilir. Oyunun kaldırıldığı doğrulanmadı."),
            "BROWSER-CACHE" => ("Chrome / Edge profilinin belirli önbellek alt klasörü için bilinen konum; profilin tamamı değildir.", "Sayfalar / grafik verileri yeniden indirilebilir veya hazırlanabilir. Tarayıcı açıkken elle temizlik çalışmasını etkileyebilir."),
            "ANDROID-SDK-STRUCTURE" => ("Klasör adı ve doğrudan alt klasörleri Android geliştirme araçlarına benziyor; kimlik yalnız yapıdan tahmin edildi.", "Android Studio dışında Flutter, Unity, ADB veya komut satırı derlemeleri de çalışmayabilir; araçları yeniden kurmak gerekebilir."),
            _ => ("Bu kural için doğrulanmış amaç açıklaması bulunmuyor.", "Silmenin etkisi bu kural tarafından belirlenemiyor; kaldırma kararı vermeyin.")
        };
        string unknown = "Dosya içerikleri okunmadı. Bu konumun beklenen amacı anlatılıyor; gerçek içerik, etkin kullanım ve senin ihtiyaç duyup duymadığın doğrulanmadı. Büyük boyut veya eşleşme, gereksizlik kanıtı değildir.";
        if (candidate.RuleId == "ANDROID-SDK-STRUCTURE") unknown += " Klasör yapısı tek başına gerçek bir SDK olduğunu da kanıtlamaz.";
        if (candidate.Kind == ReviewKind.Conditional) unknown += " Yerel değişiklikler, özel veri ve çevrimdışı gereklilik ayırt edilmedi.";
        if (candidate.Evidence != null) unknown += candidate.Evidence.ClueSource == FileClueSource.FinalScanObservations
            ? " Ek dosya adı ipuçları son taramanın geçerli metadata gözlemlerinden gelir; en büyük dosyalar listesinin sınırından bağımsızdır. Ulaşılamayan, hariç tutulan ve doğrulanamayan alanlar kapsanmış sayılmaz. İpucu görülmemesi kişisel veri bulunmadığını kanıtlamaz."
            : " Ek dosya adı ipuçları yalnız listede tutulan dosyalardan gelir; liste sınırı dışındaki dosyalar bu kontrolde değerlendirilmedi. İpucu görülmemesi kişisel veri bulunmadığını kanıtlamaz.";
        string clues = FileClues(candidate);
        if (clues.Length > 0) consequence += (candidate.Evidence!.ClueSource == FileClueSource.FinalScanObservations ? " Son tarama adlarında " : " Listede ")
            + RiskNames(candidate.Evidence.DataRisks) + " işaretleri de görüldü; kişisel/özel veri veya gerekli çalışmalar kaybolabilir. Dosya adı gerçek içeriği kanıtlamaz.";
        return new(purpose, Evidence(candidate), consequence, unknown, clues, Association(candidate), Usage(candidate), ClueCoverage(candidate));
    }

    internal static string Association(ReviewCandidate c) => c.Evidence?.Match switch
    {
        ReviewMatchKind.KnownLocation => "Bilinen klasör konumundan amaç ilişkisi. Uygulamanın kurulu olduğu veya bir projenin bu veriye başvurduğu doğrulanmadı.",
        ReviewMatchKind.FolderStructure => "Ad ve gözlenen klasör yapısından olası araç ilişkisi. Gerçek araç kimliği ve proje bağımlılığı doğrulanmadı.",
        _ => "Uygulama / proje ilişkisi belirlenmedi; dayanak eksik."
    };
    internal static string Usage(ReviewCandidate c) => c.Evidence?.Usage switch
    {
        ReviewUsageState.NotExamined => "İncelenmedi · Güncel kullanım ve bu veriye ihtiyaç duyulup duyulmadığı bilinmiyor. Uygulama envanteri, çalışan süreçler ve proje içerikleri incelenmedi.",
        _ => "Bilinmiyor · Bu kayıt için kullanım kanıtı bulunmuyor."
    };
    internal static string UsageLabel(ReviewCandidate c) => c.Evidence?.Usage == ReviewUsageState.NotExamined ? "İncelenmedi" : "Bilinmiyor";
    internal static string ClueCoverage(ReviewCandidate c)
    {
        string source = c.Evidence == null ? "Dosya adı ipucu kaynağı belirtilmedi. "
            : c.Evidence.ClueSource == FileClueSource.FinalScanObservations
                ? "Son taramanın geçerli dosya adı gözlemleri; en büyük dosyalar listesi sınırından bağımsız. İçerik veya gerçek dosya biçimi okunmadı. "
                : "Yalnız sonuç listesinde tutulan dosya adları; liste dışında kalan dosyalar incelenmiş sayılmaz. İçerik veya gerçek dosya biçimi okunmadı. ";
        return source + (c.Incomplete ? "Sonuç kısmi / uyarılı; ulaşılamayan veya doğrulanamayan alanlar var. " : "Ulaşılamayan veya doğrulanamayan alanlar için eksiksizlik garantisi yok. ")
            + (c.ExcludedSubtrees > 0 ? $"Bu aday altında {c.ExcludedSubtrees} yol bilerek hariç tutuldu. " : "")
            + "İpucu görülmemesi kişisel veri bulunmadığını kanıtlamaz.";
    }

    static string Evidence(ReviewCandidate c)
    {
        if (c.Evidence is not { } evidence) return c.Reason;
        string text = c.Reason + (evidence.Match switch
        {
            ReviewMatchKind.FolderStructure => "\nTanıma yöntemi: klasör adı ve gözlenen doğrudan yapı işaretleri; gerçek araç kimliği doğrulanmadı.",
            ReviewMatchKind.KnownLocation => "\nTanıma yöntemi: bilinen konum eşleşmesi; içerik veya etkin kullanım doğrulaması değil.",
            _ => "\nTanıma yöntemi belirtilmedi; bu kayıt doğrulanmış bir konum eşleşmesi olarak yorumlanmamalı."
        });
        if (evidence.Match == ReviewMatchKind.KnownLocation && !c.Path.TrimEnd('\\').Equals(evidence.MatchedPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            text += "\nYalnız bu alt klasörün ölçümü gösteriliyor; eşleşen kural konumunun tamamı taranmış sayılmaz.";
        return text;
    }
    static string RiskNames(FileDataRisk risks)
    {
        List<string> names = [];
        if (risks.HasFlag(FileDataRisk.VirtualDisk)) names.Add("sanal disk");
        if (risks.HasFlag(FileDataRisk.Mail)) names.Add("posta verisi");
        if (risks.HasFlag(FileDataRisk.Model)) names.Add("AI modeli");
        if (risks.HasFlag(FileDataRisk.Diagnostics)) names.Add("hata/bellek dökümü");
        if (ReviewRiskText.HasUnknown(risks)) names.Add("tanımlanamayan ek dosya türü");
        return string.Join(", ", names);
    }
    static string FileClues(ReviewCandidate c)
    {
        if (c.Evidence is not { DataRisks: not FileDataRisk.None } evidence) return "";
        string prefix = c.Path.TrimEnd('\\') + "\\";
        string examples = string.Join("\n", (evidence.Examples ?? []).OrderBy(e => e.Risk)
            .Select(e => "• " + (e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? e.Path[prefix.Length..] : e.Path)));
        string origin = evidence.ClueSource == FileClueSource.FinalScanObservations ? "Son tarama gözlemlerindeki" : "Listede tutulan";
        return $"{origin} {evidence.ClueCount:N0} dosya adı {RiskNames(evidence.DataRisks)} türüyle eşleşiyor. Biçim ve içerik doğrulanmadı."
            + (examples.Length == 0 ? "" : "\nTür başına bir örnek (bu klasöre göre):\n" + examples)
            + (evidence.ClueSource == FileClueSource.FinalScanObservations
                ? "\nBu ipucu en büyük dosyalar listesinin sınırından bağımsızdır. Ulaşılamayan, hariç tutulan ve doğrulanamayan gözlemler için güvence vermez."
                : "\nBu ipucu yalnız tutulan dosya listesindendir; diğer dosyaları kapsamaz.")
            + " Konumun adı bu türlerin veri kaybı riskini kaldırmaz.";
    }

    internal static string Measurement(ReviewCandidate c) =>
        $"Taranan mantıksal boyut: {PresentationText.Size(c.LogicalBytes)} · Ölçülen ayrılan alan: {PresentationText.Size(c.AllocatedBytes)} · Dosya: {c.FileCount:N0}. "
        + (c.Incomplete ? "Kısmi / uyarılı ölçüm; boyut eksik olabilir. " : "Yalnız taranan kapsam; eksiksizlik garantisi değildir. ")
        + (c.ExcludedSubtrees > 0 ? $"Bu aday altında {c.ExcludedSubtrees} yol bilerek hariç tutuldu; boyuta ve dosya ipuçlarına dahil değildir. " : "") + Limits;

    internal static string Preview(ReviewCandidate c)
    {
        var e=Explain(c);
        return $"{Kind(c.Kind)} · {PresentationText.Size(c.LogicalBytes)}\r\n\r\nBU NEDİR?\r\n{e.Purpose}\r\n\r\nİLİŞKİ\r\n{e.Association}\r\n\r\nKULLANIM BİLGİSİ\r\n{e.Usage}\r\n\r\nNEYİ KAYBEDEBİLİRİM?\r\n{e.Consequence}\r\n\r\nNASIL YÖNETMELİ?\r\n{ReviewGuidance.Inspection(c)}\r\n\r\nDAYANAK\r\n{c.Confidence} · {c.RuleId}\r\n\r\nTam dayanak, bilinmeyenler ve ölçüm sınırları: Ayrıntı (F6).";
    }

    internal static string Details(ReviewCandidate c, string feedback)
    {
        var e = Explain(c);
        return $"{c.Path}\r\n{Kind(c.Kind)} · {c.Category}\r\n\r\nAmaç\r\n{e.Purpose}\r\n\r\nİlişki\r\n{e.Association}\r\n\r\nKullanım bilgisi\r\n{e.Usage}\r\n\r\nNeye dayanıyor?\r\n{e.Evidence}\r\n\r\nKaldırma etkisi\r\n{e.Consequence}\r\n"
            + (e.FileClues.Length == 0 ? "" : $"\r\nEk dosya adı ipuçları\r\n{e.FileClues}\r\n")
            + $"\r\nKanıt kapsamı\r\n{e.Coverage}\r\n\r\nNeyi bilmiyoruz?\r\n{e.Unknown}\r\n\r\nNasıl incelemeli?\r\n{ReviewGuidance.Inspection(c)}\r\n\r\nÖlçümün sınırı\r\n{Measurement(c)}\r\n\r\nKişisel notun: {feedback}\r\n{FeedbackHelp}\r\n\r\nKural: {c.RuleId} · {c.Confidence}";
    }

    internal static string[] CsvHeader => ["Konum", "Tür", "Taranan mantıksal boyut (bayt)", "Ölçülen ayrılan alan (bayt)", "Dosya sayısı", "Kural", "Tanıma sınırı", "Bu nedir?", "Neye dayanıyor?", "Silinirse ne olabilir?", "Neyi bilmiyoruz?", "Nasıl incelemeli?", "Ölçümün sınırı", "Kısmi / uyarılı", "Kişisel notum", "Dosya adı ipuçları", "İlişki", "Kullanım bilgisi", "Kanıt kapsamı"];
    internal static string[] CsvFields(ReviewCandidate c, string feedback)
    {
        var e = Explain(c);
        return [c.Path, Kind(c.Kind) + " · " + c.Category, c.LogicalBytes.ToString(CultureInfo.InvariantCulture), c.AllocatedBytes.ToString(CultureInfo.InvariantCulture), c.FileCount.ToString(CultureInfo.InvariantCulture), c.RuleId, c.Confidence, e.Purpose, e.Evidence, e.Consequence, e.Unknown, ReviewGuidance.Inspection(c), Measurement(c), c.Incomplete ? "Evet" : "Hayır", feedback, e.FileClues, e.Association, e.Usage, e.Coverage];
    }
}
