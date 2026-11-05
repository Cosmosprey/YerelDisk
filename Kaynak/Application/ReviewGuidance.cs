namespace YerelDisk;

// Presentation guidance from existing evidence only. Never inspects a machine,
// executes a cleanup command, or interprets absence of clues as safe deletion.
internal static class ReviewGuidance
{
    internal static string FirstStep(ReviewCandidate c)
    {
        if (c.Kind == ReviewKind.Protected) return "Elle silme; desteklenen yönetimi incele.";
        if (c.Incomplete || c.ExcludedSubtrees > 0) return "Önce ölçüm eksiklerini kontrol et.";
        if (c.Evidence is { DataRisks: not FileDataRisk.None }) return "Önce işaretli dosyaları incele.";
        if (c.Evidence?.Match is not (ReviewMatchKind.KnownLocation or ReviewMatchKind.FolderStructure)) return "Önce klasörün amacını doğrula.";
        return c.RuleId switch
        {
            "GRADLE-CACHES" or "GRADLE-DISTS" or "NUGET-PACKAGES" or "NUGET-HTTP" or "NUGET-PLUGINS"
                or "NPM-CACHE" or "PIP-CACHE" => "Projelerini ve çevrimdışı ihtiyacını kontrol et.",
            "CARGO-ARCHIVES" or "CARGO-SOURCES" => "Projelerini ve yerel değişiklikleri kontrol et.",
            "HF-HUB" or "OLLAMA-MODELS" => "Gerekli / özel modellerini kontrol et.",
            "ANDROID-AVD" => "Sanal cihazdaki çalışmalarını kontrol et.",
            "ANDROID-SDK-STRUCTURE" => "SDK kullanan diğer araçlarını kontrol et.",
            "USER-TEMP" or "WINDOWS-TEMP" => "Açık işleri ve kurtarma verilerini kontrol et.",
            "CRASH-DUMPS" => "Süren hata / destek incelemesini kontrol et.",
            "NVIDIA-DX" or "NVIDIA-GL" => "İlk açılışta yeniden hazırlık etkisini değerlendir.",
            "GAME-Naughty Dog" or "GAME-Hogwarts Legacy" => "Oyun kayıtlarını ve ayarlarını kontrol et.",
            "BROWSER-CACHE" => "Tarayıcıdaki önbellek temizliğini incele.",
            _ => "Önce klasörün amacını doğrula."
        };
    }

    internal static string Details(ReviewCandidate c)
    {
        string step = c.RuleId switch
        {
            "GRADLE-CACHES" or "GRADLE-DISTS" => "Gradle kullanan projelerin gereken sürüm ve paketlerini kontrol et. Android Studio'yu kaldırmak başka projelerin Gradle ihtiyacını ortadan kaldırmaz. Çevrimdışı çalışma ve yeniden indirme olanağını değerlendir.",
            "NUGET-PACKAGES" or "NUGET-HTTP" or "NUGET-PLUGINS" => ".NET projelerinin paket ihtiyacını, özel paket kaynaklarına erişimini ve çevrimdışı çalışma gereksinimini kontrol et. Bir IDE'nin kaldırılması ortak paketin kullanılmadığını göstermez.",
            "NPM-CACHE" or "PIP-CACHE" => "İlgili projelerin paketlerini yeniden edinebilip edinemeyeceğini ve çevrimdışı ihtiyacını kontrol et. Kurulu ortam / proje verisi ile önbelleği ayır; tüm üst klasörü aynı amaçta sayma.",
            "CARGO-ARCHIVES" or "CARGO-SOURCES" => "Cargo projelerinin paket ihtiyacını ve yeniden indirme olanağını kontrol et. Özellikle kaynak klasöründe yaptığın yerel değişikliklerin ayrı kopyası olup olmadığını incele.",
            "HF-HUB" or "OLLAMA-MODELS" => "Kullandığın model / veri sürümlerini ve özel çalışmalarını ilgili araçta kontrol et. Yeniden erişilemeyen veya özel modellerin kopyasını koru; depo parçalarını rastgele silme.",
            "ANDROID-AVD" => "İlgili sanal cihazdaki uygulama, test, kullanıcı verisi ve anlık görüntülere hâlâ ihtiyacın olup olmadığını kontrol et. Cihazı yeniden oluşturmak bu çalışmaları geri getirmez.",
            "ANDROID-SDK-STRUCTURE" => "Önce gerçekten bir SDK olduğunu doğrula; ardından Flutter, Unity, ADB ve komut satırı projelerinin bu konumu kullanıp kullanmadığını kontrol et. Android Studio'nun olmaması tek başına yeterli değildir.",
            "WINDOWS-OLD" => "Elle silme. Eski kullanıcı dosyalarının taşındığını ve önceki Windows'a dönüş ihtiyacını kontrol et; ardından Windows'un desteklediği depolama temizliğindeki seçeneği incele.",
            "PACKAGE-CACHE" => "Elle silme. Tek bir uygulamaya ait olduğunu varsayma; ilgili ürünün yükleyicisinin desteklediği bakım / önbellek seçeneklerini incele. Bu paketler onarım ve kaldırma için gerekli olabilir.",
            "WINDOWS-INSTALLER" => "Elle silme. İlgili uygulamanın desteklediği bakım veya kaldırma yolunu kullan; burada dosya seçip silmek güvenilir bir temizlik yöntemi değildir.",
            "WINDOWS-COMPONENTS" => "Elle silme. Windows'un desteklediği Depolama / bileşen temizleme seçeneklerini incele; görünen toplamın tamamını geri kazanılabilir alan sayma.",
            "USER-TEMP" or "WINDOWS-TEMP" => "Önce açık çalışmalarını kaydet ve tamamlanmamış kurulum / dışa aktarma işlemlerini kontrol et. Gerekebilecek kurtarma dosyalarını incele; temizlik için uygulamanın veya Windows'un desteklediği yöntemi tercih et.",
            "CRASH-DUMPS" => "Devam eden bir arıza, destek talebi veya hata incelemesi varsa gerekli dökümleri koru. Gerekiyorsa ayrıca bir kopya ayır; aynı arıza yeniden üretilemeyebilir.",
            "NVIDIA-DX" or "NVIDIA-GL" => "Yeniden hazırlığın ilk oyun / uygulama açılışında daha uzun yükleme ve takılma yaratabileceğini değerlendir. Desteklenen önbellek yönetimini incele; bu açıklamayı tüm NVIDIA klasörlerine uygulama.",
            "GAME-Naughty Dog" or "GAME-Hogwarts Legacy" => "Önce oyun kayıtları, ayarlar ve özel dosyaların ayrı kopyasını kontrol et. Oyunun kaldırılmış olması bu verilerin değersiz olduğunu göstermez; bu tarama kayıt ile önbelleği ayırmadı.",
            "BROWSER-CACHE" => "Tarayıcının kendi tarama verisi temizleme ekranında yalnız önbellek seçeneğini incele. Profil, parola, çerez ve geçmişi aynı şey sayma; bu öneri profilin tamamını kaldırmaya yönelik değildir.",
            _ => "Klasörün hangi uygulama veya çalışmaya ait olduğunu önce kendin doğrula. Amaç ve kaldırma etkisi belirlenemediği için bu kayda dayanarak kaldırma kararı verme."
        };
        if (c.Kind == ReviewKind.Protected && !step.StartsWith("Elle silme.", StringComparison.Ordinal))
            step = "Elle silme. İlgili uygulamanın veya Windows'un desteklediği yönetim yolunu incele. " + step;
        var risks = c.Evidence?.DataRisks ?? FileDataRisk.None;
        if (risks != FileDataRisk.None)
        {
            string checks = "Önce F6'daki ek dosya adı ipuçlarını incele. Ad / uzantı gerçek içeriği doğrulamaz. ";
            if ((risks & FileDataRisk.VirtualDisk) != 0) checks += "Sanal diskin bağlı olduğu sistemi ve içindeki kişisel / proje verilerini kontrol et. ";
            if ((risks & FileDataRisk.Mail) != 0) checks += "Posta dosyasının arşiv / yerel kopya olup olmadığını ve başka yerde bulunup bulunmadığını kontrol et. ";
            if ((risks & FileDataRisk.Model) != 0) checks += "Modelin gerekli, özel veya yeniden erişilemeyen bir sürüm olup olmadığını kontrol et. ";
            if ((risks & FileDataRisk.Diagnostics) != 0) checks += "Dökümün süren hata incelemesi için gerekip gerekmediğini ve hassas veri içerebileceğini değerlendir. ";
            if (ReviewRiskText.HasUnknown(risks)) checks += "Ek dosya türü çözümlenemedi; dosyanın amacını ve veri ihtiyacını ayrıca doğrula. ";
            step = (c.Kind == ReviewKind.Protected ? "Elle silme. " : "") + checks + "\n" + step;
        }
        if (c.Evidence?.Match is not (ReviewMatchKind.KnownLocation or ReviewMatchKind.FolderStructure))
            step = "Amaç ilişkisi için dayanak eksik; aşağıdaki yönlendirme bu kuralın genel açıklamasıdır.\n" + step;
        if (c.Incomplete || c.ExcludedSubtrees > 0)
            step = "Önce ölçümün sınırını kontrol et: " + (c.Incomplete ? "kısmi / uyarılı alanlar var. " : "")
                + (c.ExcludedSubtrees > 0 ? "Hariç tutulan alanlar incelenmedi; gerekiyorsa kapsamı kendin değiştirip yeniden tara. " : "Gerekiyorsa ilgili alanı yeniden tara. ")
                + "Eksik gözlemi ipucu yokluğu veya boş alan olarak yorumlama.\n" + step;
        return step;
    }

    internal static string Inspection(ReviewCandidate c) => Details(c) + "\n\nEk inceleme notu:\n" + c.Advice;
}
