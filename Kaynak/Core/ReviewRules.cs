namespace YerelDisk;

internal enum ReviewKind { Cache, Temporary, Conditional, Protected }

internal sealed record ReviewCandidate(string Path, long LogicalBytes, long AllocatedBytes, long FileCount,
    string Category, string Confidence, string RuleId, string Reason, string Advice, bool Incomplete,
    ReviewKind Kind = ReviewKind.Conditional, ReviewEvidence? Evidence = null, int ExcludedSubtrees = 0);

// Path recognition is evidence of a folder's purpose, never proof that its data is dispensable.
// This component uses completed scan metadata only: no disk access, registry, processes or network.
internal sealed class ReviewRules
{
    internal sealed record Location(string Path, string Id, string Category, string Reason, string Advice, ReviewKind Kind);
    readonly List<Location> locations = [];
    readonly List<string> browserRoots = [];

    internal ReviewRules(string home, string local, string programData, string windows)
    {
        void Add(string root, string suffix, string id, string category, string reason, string advice, ReviewKind kind)
        {
            if (CanonicalLocal(root) is { } full) locations.Add(new(Path.Combine(full, suffix).TrimEnd('\\'), id, category, reason, advice, kind));
        }
        const string cacheAdvice = "İlgili uygulama ve derlemeleri kapatın. Önce uygulamanın kendi temizleme yöntemini kullanın. Yeniden kullanım indirme veya yeniden oluşturma gerektirebilir; kaynaklar erişilemiyorsa geri yükleme mümkün olmayabilir. Çevrimdışı çalışmayı etkileyebilir. Kullanılmadığı doğrulanmadı.";
        Add(home, @".gradle\caches", "GRADLE-CACHES", "Önbellek adayı", "Gradle kullanıcı önbelleğinin bilinen konumuyla eşleşiyor. Android dışında Java/Gradle projeleri de kullanabilir.", cacheAdvice, ReviewKind.Cache);
        Add(home, @".gradle\wrapper\dists", "GRADLE-DISTS", "Önbellek adayı", "Gradle Wrapper dağıtımlarının bilinen indirme konumuyla eşleşiyor. .gradle ayarları bu öneriye dahil değildir.", cacheAdvice, ReviewKind.Cache);
        Add(local, @"npm-cache\_cacache", "NPM-CACHE", "Önbellek adayı", "npm paket önbelleğinin varsayılan alt konumuyla eşleşiyor; klasörün bütünü yerine yalnız _cacache ele alındı.", cacheAdvice, ReviewKind.Cache);
        Add(home, @".nuget\packages", "NUGET-PACKAGES", "Paket deposu · kullanım bilinmiyor", "NuGet'in varsayılan ortak paket deposu. Projeler paketleri doğrudan buradan kullanabilir.", "Etkin derlemeleri kapatın. Önce Visual Studio / NuGet'in desteklenen temizleme seçeneklerini değerlendirin. Paket kaynakları hâlâ erişilebilir değilse veya çevrimdışı çalışıyorsanız silinen paketler geri getirilemeyebilir. Kullanılmayan paketler ayırt edilmedi.", ReviewKind.Conditional);
        Add(local, @"NuGet\v3-cache", "NUGET-HTTP", "Önbellek adayı", "NuGet'in varsayılan HTTP önbellek konumuyla eşleşiyor.", cacheAdvice, ReviewKind.Cache);
        Add(local, @"NuGet\plugins-cache", "NUGET-PLUGINS", "Önbellek adayı", "NuGet eklentilerinin varsayılan önbellek konumuyla eşleşiyor. Eklentinin kurulu dosyaları ve kullanıcı ayarları bu öneriye dahil değildir.", cacheAdvice, ReviewKind.Cache);
        Add(home, @".cargo\registry\cache", "CARGO-ARCHIVES", "Önbellek adayı", "Cargo'nun varsayılan indirilmiş paket arşivi konumuyla eşleşiyor. .cargo/bin içindeki araçlar ve kullanıcı yapılandırması dahil değildir.", cacheAdvice, ReviewKind.Cache);
        Add(home, @".cargo\registry\src", "CARGO-SOURCES", "Paket deposu · kullanım bilinmiyor", "Cargo'nun indirilmiş paket kaynaklarını açtığı varsayılan konum. Derlemeler bu kaynakları kullanır; yerel değişiklik yapılmış olup olmadığı bilinmiyor.", "Etkin Rust derlemelerini kapatın. Kaynaklarda kendi değişiklikleriniz olmadığını doğrulayın. Paketler tekrar indirilemiyorsa veya çevrimdışı çalışıyorsanız kaldırmak derlemeleri engelleyebilir. Projeleriniz ve .cargo klasörünün bütünü önerilmez.", ReviewKind.Conditional);
        Add(home, @".cache\huggingface\hub", "HF-HUB", "Model / veri deposu · kullanım bilinmiyor", "Hugging Face Hub'ın varsayılan model, veri seti ve sürüm önbelleği konumuyla eşleşiyor. Büyük olması kullanılmadığı anlamına gelmez.", "Hugging Face'in kendi önbellek inceleme araçlarıyla gerekli model / veri sürümlerini ayırt edin. Tüm depoyu silmeyin; çevrimdışı modeller kaybolabilir ve yeniden indirme mümkün olmayabilir. Paylaşılan veriler ve bağlantılar nedeniyle boyut geri kazanılabilir alan değildir.", ReviewKind.Conditional);
        Add(home, @".ollama\models", "OLLAMA-MODELS", "Model deposu · kullanım bilinmiyor", "Ollama'nın varsayılan model konumuyla eşleşiyor. Bu veri, çalışan yerel modellerin kendisi olabilir; geçici dosya sayılmaz.", "Ollama içinde kurulu modelleri inceleyip yalnız kullanmadıklarınızı kendi model kaldırma yöntemiyle yönetin. Elle blob / manifest silmeyin; özel modeller veya çevrimdışı kullanım için gerekli veri geri getirilemeyebilir.", ReviewKind.Conditional);
        Add(home, @".android\avd", "ANDROID-AVD", "Sanal cihaz verisi · kullanım bilinmiyor", "Android sanal cihazlarının varsayılan konumuyla eşleşiyor. Yalnız indirilebilir sistem imajı değildir; cihazın kendi kullanıcı verileri ve anlık görüntüleri bulunabilir.", "Android Device Manager üzerinden kullanmadığınız sanal cihazları inceleyin. Uygulama / test verisi veya ihtiyaç duyulan bir cihaz içerebileceğinden tüm .android klasörünü ya da AVD deposunu topluca silmeyin.", ReviewKind.Conditional);
        if (CanonicalLocal(windows) != null)
            Add(Path.GetDirectoryName(windows.TrimEnd('\\')) ?? "", "Windows.old", "WINDOWS-OLD", "Sistem verisi · elle silmeyin", "Windows klasörünün yanındaki Windows.old konumuyla eşleşiyor. Önceki Windows'a dönüş verileri ve eski kişisel dosyalar bulunabilir.", "Önce eski kişisel dosyalarınızı ve önceki Windows'a dönme ihtiyacınızı kontrol edin. Uygunsa Windows Depolama > Geçici dosyalar içindeki önceki Windows yüklemesi seçeneğini kullanın. Temizleme geri dönüş olanağını kaldırır; elle topluca silmeyin.", ReviewKind.Protected);
        Add(local, @"pip\Cache", "PIP-CACHE", "Önbellek adayı", "pip'in varsayılan indirme / derleme önbelleği konumuyla eşleşiyor. Python ortamının kendisi değildir.", "Python kurulum / derlemelerini kapatın. pip'in kendi cache inceleme ve temizleme seçeneklerini kullanın. Silme sonrası paketler tekrar indirilebilir veya derlenebilir; kaynak erişimi yoksa bu mümkün olmayabilir.", ReviewKind.Cache);
        Add(local, "CrashDumps", "CRASH-DUMPS", "Hata dökümleri · kullanım bilinmiyor", "Uygulama çökme dökümlerinin bilinen kullanıcı konumuyla eşleşiyor; etkin bir hata incelemesi için gerekli olabilir.", "Destek / hata incelemesi için gerekmediğini doğrulayın. İçerik okunmadı; dökümler özel veri barındırabilir. Otomatik gereksiz sayılmaz.", ReviewKind.Conditional);
        if (CanonicalLocal(local) is { } browserBase)
            foreach (string suffix in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data" })
                browserRoots.Add(Path.Combine(browserBase, suffix));
        Add(local, @"NVIDIA\DXCache", "NVIDIA-DX", "Önbellek adayı", "NVIDIA DXCache için bilinen konumla eşleşiyor. Dosya içerikleri doğrulanmadı.", "Oyunları ve ilgili uygulamaları kapatın. Öncelikle Windows / sürücü temizleme seçeneklerini kullanın. Önbelleğin yeniden hazırlanması ilk açılışlarda takılma veya yükleme süresi artışı yaratabilir.", ReviewKind.Cache);
        Add(local, @"NVIDIA\GLCache", "NVIDIA-GL", "Önbellek adayı", "NVIDIA GLCache için bilinen konumla eşleşiyor. Dosya içerikleri doğrulanmadı.", cacheAdvice, ReviewKind.Cache);
        Add(local, "Temp", "USER-TEMP", "Geçici alan · incele", "Kullanıcının varsayılan geçici dosya konumuyla eşleşiyor. İçinde etkin uygulamaların verileri ve kurtarma dosyaları bulunabilir.", "Windows Ayarlar > Sistem > Depolama > Geçici dosyalar üzerinden inceleyin. Tüm klasörü topluca silmeyin; açık uygulamalar ve kurtarılacak çalışmalar olabilir.", ReviewKind.Temporary);
        Add(windows, "Temp", "WINDOWS-TEMP", "Geçici alan · incele", "Windows geçici dosya konumuyla eşleşiyor; kullanılmadığı doğrulanmadı.", "Windows Depolama / Disk Temizleme aracını kullanın. Klasörün tamamını elle silmeyin.", ReviewKind.Temporary);
        Add(programData, "Package Cache", "PACKAGE-CACHE", "Kurulum verisi · elle silmeyin", "ProgramData altındaki ortak kurulum paketi önbelleği. Onarma, güncelleme ve kaldırma işlemleri bu paketlere ihtiyaç duyabilir.", "Klasörü elle silmeyin. İlgili ürünün yükleyicisinin desteklediği önbellek yönetimini kullanın; tüm paketlerin aynı ürüne ait olduğu varsayılmadı.", ReviewKind.Protected);
        Add(windows, "Installer", "WINDOWS-INSTALLER", "Sistem verisi · elle silmeyin", "Windows Installer önbelleğinin bilinen konumu. Büyük görünmesi gereksiz olduğunu göstermez.", "Elle silmeyin; uygulamaların onarılması, güncellenmesi veya kaldırılması bozulabilir.", ReviewKind.Protected);
        Add(windows, "WinSxS", "WINDOWS-COMPONENTS", "Sistem verisi · elle silmeyin", "Windows bileşen deposunun bilinen konumu. Hard linkler nedeniyle görünen boyut geri kazanılabilir alan değildir.", "Elle silmeyin. Windows'un desteklediği Depolama / bileşen temizleme yöntemlerini kullanın.", ReviewKind.Protected);
        foreach (string game in new[] { "Naughty Dog", "Hogwarts Legacy" })
            Add(programData, game, "GAME-" + game, "Oyun verisi · kullanım bilinmiyor", "Bilinen oyun klasörü konumuyla eşleşiyor. Oyunun kaldırılmış olduğu ve dosyaların yalnız önbellek olduğu doğrulanmadı.", "Oyunu ve bu verileri artık kullanmadığınızı kendiniz doğrulayın. Kayıt / ayar / önbellek ayrımı içerik okunmadan yapılamaz; tamamını otomatik silinebilir saymayın.", ReviewKind.Conditional);
        locations.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));
    }

    internal static ReviewRules ForCurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    internal List<ReviewCandidate> Analyze(ScanResult scan, CancellationToken token = default)
    {
        // Stale paths must not become actionable recommendations, even with path actions disabled.
        if (scan.ScopeChanged && scan.UnverifiedPaths.Count == 0) return [];
        string[] roots = scan.RootPaths.Select(CanonicalLocal).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0) return [];
        List<ReviewCandidate> found = [];
        Stack<FolderEntry> pending = new();
        pending.Push(scan.Root);
        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            // Accept only metadata inside the explicitly scanned roots; the multi-root display
            // node is synthetic. This is a second boundary, independent of path recognition.
            bool inside = CanonicalLocal(folder.Path) != null && roots.Any(root => Within(folder.Path.TrimEnd('\\'), root.TrimEnd('\\')));
            if (!inside)
            {
                if (ReferenceEquals(folder, scan.Root) && scan.RootPaths.Count > 1 && !Path.IsPathFullyQualified(folder.Path))
                    foreach (var child in folder.Children) pending.Push(child);
                continue;
            }
            if (!scan.CanUsePath(folder.Path)) continue;
            Location? rule = locations.FirstOrDefault(r => Within(folder.Path, r.Path)) ?? BrowserRule(folder.Path);
            string[] markers = rule == null ? SdkMarkers(folder, scan) : [];
            bool sdk = markers.Length >= 2;
            if (rule != null || sdk)
            {
                if (folder.LogicalBytes > 0 || folder.FileCount > 0 || folder.IssueCount > 0)
                    found.Add(new(folder.Path, folder.LogicalBytes, folder.AllocatedBytes, folder.FileCount,
                        rule?.Category ?? "Geliştirme aracı · kullanım bilinmiyor",
                        sdk ? "Yapı benzerliği; kullanım bilinmiyor" : "Konum eşleşmesi; kullanım bilinmiyor",
                        rule?.Id ?? "ANDROID-SDK-STRUCTURE",
                        rule == null ? "SDK / Android_SDK / android-sdk adlı klasörde gözlenen doğrudan işaretler: " + string.Join(", ", markers) + ". Doğrudan sürüm kontrol klasörü görülmedi. İçerik doğrulanmadı."
                            : rule.Reason + " Eşleşen kural konumu: " + rule.Path,
                        rule?.Advice ?? "Android Studio dışında Flutter, Unity, komut satırı derlemeleri ve ADB de SDK'yı kullanabilir. Bunları artık kullanmadığınızı doğrulamadan kaldırmayın. Sanal cihaz / proje verileri ayrıca değerlendirilmelidir.",
                        scan.Cancelled || scan.Failed || folder.IssueCount > 0 || folder.Measurement != MeasurementState.Measured,
                        rule?.Kind ?? ReviewKind.Conditional,
                        new(sdk ? ReviewMatchKind.FolderStructure : ReviewMatchKind.KnownLocation, rule?.Path ?? folder.Path), folder.ExcludedSubtrees));
                // One row per recognized subtree. Never sum overlapping parent and child sizes.
                continue;
            }
            foreach (var child in folder.Children) pending.Push(child);
        }
        ReviewFileEvidence.Apply(scan, found, token);
        return found.OrderByDescending(f => f.LogicalBytes).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    Location? BrowserRule(string path)
    {
        foreach (string root in browserRoots)
        {
            if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            string[] parts = path[(root.Length + 1)..].Split('\\');
            if (parts.Length < 2 || !IsBrowserProfile(parts[0])) continue;
            if (!(parts[1].Equals("Cache", StringComparison.OrdinalIgnoreCase)
                || parts[1].Equals("Code Cache", StringComparison.OrdinalIgnoreCase)
                || parts[1].Equals("GPUCache", StringComparison.OrdinalIgnoreCase))) continue;
            string matched = Path.Combine(root, parts[0], parts[1]);
            return new(matched, "BROWSER-CACHE", "Önbellek adayı",
                "Chrome / Edge varsayılan veri konumunda belirli bir profilin Cache, Code Cache veya GPUCache alt klasörü. Kural profil kökünü ve diğer alt klasörleri hedeflemez; buraya taşınmış başka veriler kontrol edilmedi.",
                "Tarayıcının kendi tarama verilerini temizle ekranını kullanın; yalnız önbellek seçeneğini gözden geçirin. Tarayıcı kapatılmadan klasörleri elle temizlemeyin. İlk ziyaretlerde indirme / yükleme süresi artabilir; bu öneri tüm profili silme önerisi değildir.", ReviewKind.Cache);
        }
        return null;
    }

    static bool IsBrowserProfile(string name) => name.Equals("Default", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) && name.Length > 8
            && name.AsSpan(8).IndexOfAnyExceptInRange('0', '9') < 0);

    static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    // Do not reinterpret aliased, relative, network, dot-segment or trailing-dot paths.
    // Native scanning already supplies canonical local paths; this guards the rule boundary too.
    internal static string? CanonicalLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        try
        {
            string full = Path.GetFullPath(path).TrimEnd('\\');
            if (!full.Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
            if (full.Split('\\').Skip(1).Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':'))) return null;
            return full.Length == 2 && full[1] == ':' ? full + "\\" : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    static string[] SdkMarkers(FolderEntry folder, ScanResult scan)
    {
        if (!(folder.Name.Equals("SDK", StringComparison.OrdinalIgnoreCase)
            || folder.Name.Equals("Android_SDK", StringComparison.OrdinalIgnoreCase)
            || folder.Name.Equals("android-sdk", StringComparison.OrdinalIgnoreCase))) return [];
        string[] markers = ["platform-tools", "platforms", "build-tools", "emulator", "cmdline-tools"];
        var children = folder.Children.Where(c => ReferenceEquals(c.Parent, folder) && CanonicalLocal(c.Path) != null
                && Path.GetDirectoryName(c.Path)?.Equals(folder.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        // Conservative purpose recognition: general project directory names are insufficient.
        if (children.Any(c => c.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || c.Name.Equals(".hg", StringComparison.OrdinalIgnoreCase) || c.Name.Equals(".svn", StringComparison.OrdinalIgnoreCase))) return [];
        string[] observed = children.Where(c => c.Measurement is not (MeasurementState.Unknown or MeasurementState.Previous)
                && scan.CanUsePath(c.Path) && markers.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return observed.Any(n => n.Equals("platform-tools", StringComparison.OrdinalIgnoreCase) || n.Equals("cmdline-tools", StringComparison.OrdinalIgnoreCase))
            ? observed : [];
    }
}
