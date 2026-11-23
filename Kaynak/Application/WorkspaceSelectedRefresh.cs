namespace YerelDisk.Presentation;

internal sealed partial class WorkspaceViewModel
{
    readonly IRefreshService refreshService;
    int refreshCacheMiB;
    public Choice[] RefreshCacheChoices { get; } = [new("Kapalı · varsayılan", 0), new("64 MiB · düşük indeks bütçesi", 64), new("128 MiB · hızlı yenileme için önerilen", 128)];
    public int RefreshCacheMiB
    {
        get => refreshCacheMiB;
        set
        {
            if (disposed || busy || value is not (0 or 64 or 128) || value == refreshCacheMiB) return;
            refreshCacheMiB = value;
            // Advisory metadata only; rows, measurements and provenance survive.
            if (scan?.RefreshIndex is { } retained && (value == 0 || retained.MetadataBytes > RefreshIndexBudget)) scan.RefreshIndex = null;
            NotifyAll();
        }
    }
    long RefreshIndexBudget => (long)refreshCacheMiB << 20;
    public string RefreshCacheHint => refreshCacheMiB == 0
        ? "Varsayılan kapalı. Tam tarama çalışır; ek dosya indeksi tutulmaz. Açmak yalnız bu oturumda geçerlidir."
        : $"En fazla {refreshCacheMiB} MiB ek dosya indeksi bellekte tutulur; diske kaydedilmez. İşlem sırasında eski/yeni indeks ve çalışma alanı birlikte daha fazla RAM kullanır; bu toplam RAM sınırı değildir. İndeks sığmazsa klasör yenileme özgün kapsamın tamamını ölçebilir. Kapatmak mevcut indeksi bırakır.";
    public string RefreshCacheStatus => refreshCacheMiB == 0 ? "İndeks kapalı."
        : scan?.RefreshIndex is { Captured: true } ready ? $"İndeks hazır · {ready.FileCount:N0} dosya kaydı ve klasör bilgileri bellekte."
        : scan?.RefreshIndex?.State == RefreshIndexState.BudgetExceeded ? "Son indeks bütçeye sığmadı. Daha yüksek bütçe seçip yeniden ölçebilirsiniz; hızlı yol hazır olana kadar tüm kapsam ölçülür."
        : "Bu sonuç için hazır indeks yok. Sonraki tarama veya klasör yenilemenin tam kapsam turu indeksi hazırlamayı dener.";
    FolderEntry? RefreshTarget => IsFolderView ? selection is FolderEntry selected && rows.Contains(selected) ? selected : folder : null;
    public bool ShowRefreshTarget => refreshCacheMiB > 0 && RefreshTarget != null;
    public string RefreshActionLabel => ReferenceEquals(RefreshTarget, scan?.Root) && scan != null ? "Tüm kapsamı yenile" : "Klasörü yenile";
    public string RefreshTargetNotice => RefreshTarget is not { } target ? ""
        : "Yenileme hedefi: " + target.Path + (ReferenceEquals(target, scan?.Root) ? " · tüm kapsam"
            : ReferenceEquals(target, selection) ? " · seçili klasör" : " · açık klasör");
    public string RefreshDecisionNotice { get; private set; } = "";
    public bool CanRefreshSelected => CanRemeasure && refreshCacheMiB > 0 && RefreshTarget != null;
    public string RefreshSelectionHint => (refreshCacheMiB == 0
        ? "Ayarlar > Klasör yenileme bölümünden bellekte indeks tutmayı açın."
        : RefreshTarget is not { } target ? "Klasörler görünümünde bir klasör seçin veya açın."
        : "Yenilenecek klasör: " + target.Path + "\nSeçim yoksa açık klasör kullanılır. Diğer alanların önceki ölçümü korunur. "
            + (scan?.RefreshIndex is { Captured: true } ? "" : "İlk kullanım / hazır indeks yok: " + RefreshCacheStatus + " ")
            + "Kök seçildiğinde veya hızlı yol uygun değilse sonucun özgün kökleri/hariçleriyle tam ölçüm yapılır. İptalde eski sonuç korunur.")
            + (RefreshDecisionNotice.Length == 0 ? "" : "\nSon tamamlanan klasör yenileme: " + RefreshDecisionNotice);
    internal static string FullRefreshExplanation(RefreshReason reason, RefreshIndexState? previousIndexState) => reason switch
    {
        RefreshReason.WholeScope => "Kök / tüm kapsam hedeflendi; alt klasör seçilmedi.",
        RefreshReason.MissingIndex when previousIndexState == RefreshIndexState.BudgetExceeded => "Önceki indeks bellek bütçesine sığmadı.",
        RefreshReason.MissingIndex => "Bu sonuç için kullanılabilir dosya indeksi yok.",
        RefreshReason.IndexMismatch => "Önceki indeks mevcut kapsamla eşleşmiyor.",
        RefreshReason.SelectedUncertain => "Hedef klasörün önceki ölçümü veya konumu doğrulanamadı.",
        RefreshReason.UnsafeEvidence => "Önceki ölçümde hızlı birleştirme için yeterli doğrulanmış bilgi yok.",
        RefreshReason.SharedOutside => "Hedef ile dış alan arasında ortak dosya kimliği bulundu; çift sayımı önlemek için tüm kapsam ölçülüyor.",
        RefreshReason.MetadataBudget => "İndeks veya yenileme çalışma alanı bellek bütçesine sığmadı.",
        RefreshReason.MeasurementHistoryLimit => "Farklı zamanlarda yenilenen alanların kayıt sınırına ulaşıldı; tüm kapsam yeniden ölçülüyor.",
        RefreshReason.IdentityBudget => "Dosya kimliklerini karşılaştırma bütçesi yetmedi.",
        RefreshReason.FreshUnavailable => "Hedefin yeni dosya indeksi kullanılamıyor.",
        RefreshReason.FreshScopeMismatch => "Hedefin yeni ölçümü beklenen kapsamla eşleşmiyor.",
        RefreshReason.SelectedIdentityChanged => "Hedef klasörün kimliği veya konumu değişti.",
        RefreshReason.FreshUncertain => "Hedefin yeni ölçümü hızlı birleştirme için yeterince doğrulanamadı.",
        _ => "Hızlı yenilemenin güvenli birleştirme koşulları sağlanmadı."
    };
    public bool HasMixedMeasurements => scan?.HasMixedMeasurementTimes == true;
    public string MeasurementNotice => scan == null ? "Henüz ölçüm yok." : MeasurementProvenance.Summary(scan, MeasurementCompletedAt);
    public string ComparisonHint => HasMixedMeasurements
        ? "Farklı zamanlarda ölçülen alanlar var. Referans / karşılaştırma için Tümünü yeniden ölç ile tüm kapsamı ölçün."
        : "Bir taramayı referans olarak saklayın veya önceki ölçümü açıp farkları görün.";
    public Task RefreshSelectedAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (busy) return CurrentScan;
        if (!CanRefreshSelected) return Task.CompletedTask;
        CurrentScan = RunRemeasureAsync(RefreshTarget!);
        return CurrentScan;
    }
}
