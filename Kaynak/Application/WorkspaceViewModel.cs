using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace YerelDisk.Presentation;

internal enum WorkspaceView { Folders, Files, Review, Issues, Settings }
internal sealed record Choice(string Label, int Value);

// All scan/filter/sort state belongs here, not in the visual controls. Engine objects
// are reused directly; there is no per-file UI wrapper or per-file notification object.
internal sealed class WorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    readonly IScanService scanService;
    public InventoryController Inventory { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    ScanResult? scan;
    FolderEntry? folder;
    List<ReviewCandidate> candidates = [];
    UnrecognizedAreas? unrecognizedAreas;
    int reviewCollection;
    readonly Dictionary<int, ViewState> reviewStates = [];
    readonly Dictionary<string, string> feedback = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<WorkspaceView, ViewState> viewStates = [];
    sealed record ViewState(string Search, string SortKey, bool Descending, object? Selection);
    SelectionDetails? details;
    object? preferredSelection;
    internal bool IsPublishingState { get; private set; }
    CancellationTokenSource? scanCancellation, refreshCancellation;
    int scanVersion, refreshVersion;
    WorkspaceView view;
    IReadOnlyList<object> rows = Array.Empty<object>();
    object? selection;
    bool busy, refreshing, disposed;
    string search = "", status = "Bir veya birkaç klasör seçerek başlayın.", error = "";
    string sortKey = "Size";
    bool descending = true;
    int workers, fileLimit = 100_000, reviewMinimum = 10 * 1024 * 1024, reviewKind;
    bool fullPath, technical;
    bool distributionMode;
    public bool IsFolderView => view == WorkspaceView.Folders;
    public bool IsDistribution => IsFolderView && distributionMode;
    public bool ShowTable => !IsDistribution;
    public bool CanSwitchPresentation => CanSearch && !refreshing && IsFolderView;
    public bool CanUseDistribution => CanSwitchPresentation && IsDistribution;
    public SpaceDistribution Distribution { get; private set; } = SpaceDistribution.Empty;

    public ObservableCollection<string> Roots { get; } = [];
    public ObservableCollection<string> Exclusions { get; } = [];
    public Choice[] WorkerChoices { get; } = [new("Otomatik · önerilen", 0), new("1 · tek işçi", 1), new("2 işçi", 2), new("4 işçi", 4), new("6 işçi", 6), new("8 işçi", 8), new("12 işçi", 12), new("16 işçi", 16)];
    public Choice[] LimitChoices { get; } = [new("10.000 dosya", 10_000), new("50.000 dosya", 50_000), new("100.000 dosya · önerilen", 100_000), new("250.000 dosya", 250_000), new("500.000 dosya", 500_000)];
    public Choice[] MinimumChoices { get; } = [new("Boyut sınırı yok", 0), new("En az 10 MiB", 10 * 1024 * 1024), new("En az 100 MiB", 100 * 1024 * 1024), new("En az 1 GiB", 1024 * 1024 * 1024)];
    public Choice[] KindChoices { get; } = [new("Tüm türler", 0), new("Önbellek / geçici alan", 1), new("Kullanımına bağlı", 2), new("Elle silmeyin", 3)];
    public Choice[] ReviewCollectionChoices { get; } = [new("Kural eşleşmeleri", 0), new("Diğer büyük alanlar", 1)];
    public int ReviewCollection => reviewCollection;
    public bool IsUnrecognized => IsReview && reviewCollection == 1;
    public bool IsKnownReview => IsReview && reviewCollection == 0;
    public string[] FeedbackChoices { get; } = ["Değerlendirilmedi", "Faydalı aday", "Yanlış eşleşme", "Tutacağım"];

    public WorkspaceViewModel() : this(new NativeScanService()) { }
    internal WorkspaceViewModel(IScanService scanService) : this(scanService, new RegistryInventoryProvider()) { }
    internal WorkspaceViewModel(IScanService scanService, IInventoryProvider inventoryProvider)
    {
        this.scanService = scanService;
        Inventory = new(inventoryProvider);
        Inventory.PropertyChanged += (_, _) => { details = null; Notify(nameof(Details)); Notify(nameof(FullDetails)); Notify(nameof(DetailText)); };
        Roots.CollectionChanged += (_, _) => { Notify(nameof(RootsLabel)); StateChanged(); };
        Exclusions.CollectionChanged += (_, _) => Notify(nameof(RootsLabel));
    }
    public WorkspaceView View => view;
    public IReadOnlyList<object> Rows => rows;
    public int RowCount => rows.Count;
    public long MaximumVisibleSize { get; private set; }
    public bool IsBusy => busy;
    public bool IsRefreshing => refreshing;
    public bool CanScan => !disposed && !busy && Roots.Count > 0;
    public bool CanStop => busy;
    public bool CanConfigure => !busy;
    public bool CanSearch => !disposed && !busy && scan != null && view != WorkspaceView.Settings;
    public bool CanExport => CanSearch && !refreshing && AppliedQuery?.View == view;
    public bool CanUseSelection => CanSearch && !refreshing && scan?.CanUsePath(SelectedPath) == true && selection != null && AppliedQuery?.View == view
        && selection is not FileEntry { Unverified: true } && selection is not FolderEntry { Measurement: MeasurementState.Unknown or MeasurementState.Previous };
    public bool CanGoUp => !disposed && !busy && !refreshing && folder?.Parent is { } parent && scan?.CanUsePath(parent.Path) == true && view == WorkspaceView.Folders;
    public bool IsReview => view == WorkspaceView.Review;
    public bool IsSettings => view == WorkspaceView.Settings;
    public bool IsResults => !IsSettings;
    public bool ShowMetrics => view == WorkspaceView.Files || (view == WorkspaceView.Folders && !distributionMode);
    public ScanSummary? Summary { get; private set; }
    public bool ShowSummary => !IsSettings && !busy && Summary != null;
    public bool ShowScopeNotice => !IsSettings && !busy && scan != null && (scan.ScopeChanged || scan.Failed || scan.Cancelled || scan.HasUnverified || scan.IssueCount > 0 || scan.UnknownAllocation > 0 || scan.ExcludedPaths.Count > 0);
    public string ScopeNotice => scan == null ? "" : $"{ResultStatus(scan)} · {scan.IssueCount:N0} uyarı · {scan.UnverifiedPaths.Count:N0} doğrulanamayan konum · {scan.UnknownAllocation:N0} eksik alan / akış ölçümü. "
        + (scan.ExcludedPaths.Count > 0 ? $"{scan.ExcludedPaths.Count:N0} yol bilerek hariç tutuldu; toplamlar bu alanları içermez. Hariç tutulan yollar F6 ve CSV'de listelenir. " : "")
        + "Ölçüm uyarıları Uyarılar görünümünde ve F6 içindedir.";
    public bool HasRows => rows.Count > 0;
    public string CompactScopeNotice => scan == null ? "" : $"Ölçüm: {scan.IssueCount:N0} uyarı · {scan.UnverifiedPaths.Count:N0} doğrulanamayan · {scan.UnknownAllocation:N0} eksik alan/akış · {scan.ExcludedPaths.Count:N0} hariç yol. Ayrıntı: F6 / Uyarılar.";
    public bool IsEmpty => rows.Count == 0;
    public bool HasError => error.Length > 0;
    public string Error => error;
    public string RootsLabel => (Roots.Count switch { 0 => "Henüz bir konum seçilmedi", 1 => Roots[0], _ => $"{Roots.Count} klasör seçildi · listeyi düzenleyebilirsiniz" })
        + (Exclusions.Count > 0 ? $" · sonraki taramada {Exclusions.Count} hariç tutma" : "");
    public string Title => view switch { WorkspaceView.Folders => "Alan nerede kullanılıyor?", WorkspaceView.Files => "En büyük dosyalar", WorkspaceView.Review when IsUnrecognized => "Diğer büyük alanlar", WorkspaceView.Review => "İnceleme adayları", WorkspaceView.Issues => "Tarama uyarıları", _ => "Kullanımınıza göre ayarlayın" };
    public string Subtitle => view switch { WorkspaceView.Folders => "Klasörleri boyutlarına göre keşfedin. Alt klasöre girmek için çift tıklayın.", WorkspaceView.Files => "Listede tutulan dosyaları inceleyin; konumunu açarak kendiniz değerlendirin.", WorkspaceView.Review when IsUnrecognized => "DENEYSEL · " + UnrecognizedAreas.Notice, WorkspaceView.Review => "DENEYSEL · Amaç, ilişki ipuçları ve kaldırma etkisi. Güncel kullanım incelenmedi; gereksizlik kanıtı değildir.", WorkspaceView.Issues => "Atlanan konumlar ve ölçüm sorunları; toplamlar eksik olabilir.", _ => "Ayarlar bu oturumda tutulur. Tarama tercihleri bir sonraki taramaya uygulanır." };
    public string LogicalSize { get; private set; } = "—";
    public string AllocatedSize { get; private set; } = "—";
    public string FileCount { get; private set; } = "—";
    public string Status => status;
    public string ScanActionLabel => scan?.HasUnverified == true ? "Yeniden doğrula" : "Taramayı başlat";
    public string FolderPath => view == WorkspaceView.Folders ? folder?.Path ?? "Taranan konum" : scan == null ? "Henüz tarama yok" : $"{scan.RootPaths.Count} kök · {scan.IssueCount:N0} uyarı · {scan.UnknownAllocation:N0} eksik ölçüm";
    public string AppliedSearch { get; private set; } = "";
    internal ViewQuery? AppliedQuery { get; private set; }
    internal QueryFilterCounts? AppliedCounts { get; private set; }
    public bool HasReviewFilters => IsReview && scan != null && (search.Trim().Length > 0 || reviewMinimum > 0 || (IsKnownReview && reviewKind != 0));
    public bool CanClearReviewFilters => HasReviewFilters && CanSearch;
    public string SortDescription => AppliedQuery?.SortDescription ?? "Varsayılan görünüm";
    public string ListCaption => refreshing ? "Liste hazırlanıyor…" : (IsReview && AppliedQuery?.View == view && AppliedQuery.Unrecognized == IsUnrecognized && AppliedCounts is { } counts
        ? $"{rows.Count:N0} / {counts.Available:N0} kayıt · {counts.Hidden:N0} filtrelendi" : $"{rows.Count:N0} kayıt")
        + (IsUnrecognized ? " · Ayrık alt ağaçlar; doğrudan dosyalar için Büyük dosyalar" : "") + (view == WorkspaceView.Files && scan != null ? $" · listede en fazla {scan.RetainedFileLimit:N0}; toplamlar tüm taranan dosyaları içerir" : "");
    public string ListCaptionDetail => ListCaption + (IsReview && AppliedCounts is { } counts
        ? $"\nFiltre nedenleri: boyut {counts.BySize:N0} · tür {counts.ByKind:N0} · arama {counts.BySearch:N0}. Sıra boyut, tür, aramadır; her kayıt bir kez sayılır. Sayılar bu listeye aittir, tüm diskin dosya / klasör toplamı değildir." : "");
    public string EmptyMessage => busy ? "Dosya içerikleri açılmadan bilgiler taranıyor…" : scan == null ? "Alan kullanımını görmek için üstten klasör seçin ve taramayı başlatın."
        : scan.ScopeChanged && scan.UnverifiedPaths.Count == 0 && IsReview ? "Kapsam değiştiği için adaylar gizlendi. Yeniden tarayın."
        : search.Length > 0 || (IsKnownReview && candidates.Count > 0) || (IsUnrecognized && unrecognizedAreas?.Folders.Count > 0) ? "Bu filtrelere uyan kayıt bulunamadı. Aramayı ve filtreleri kontrol edin."
        : IsUnrecognized ? "Bu filtrelerle listelenebilen başka alt ağaç yok. Köklerin / bölünen klasörlerin doğrudan dosyalarını Büyük dosyalar görünümünde inceleyin. Ölçülemeyen alanlar boş sayılmaz." : IsReview ? "Bu kapsamda kural eşleşmesi bulunamadı. Bu, gereksiz dosya olmadığı anlamına gelmez. Uyarılar ve tarama sınırları geçerlidir."
        : "Bu görünümde gösterilecek kayıt yok. Uyarılar ve tarama kapsamını da kontrol edin.";

    public string Search { get => search; set { if (Set(ref search, value)) { Notify(nameof(EmptyMessage)); FiltersChanged(); } } }
    public int Workers { get => workers; set { if (Set(ref workers, value)) Notify(nameof(WorkerHint)); } }
    public int FileLimit { get => fileLimit; set { if (Set(ref fileLimit, value)) Notify(nameof(LimitHint)); } }
    public bool FullFolderPath { get => fullPath; set => Set(ref fullPath, value); }
    public bool TechnicalColumns { get => technical; set => Set(ref technical, value); }
    public int ReviewMinimum { get => reviewMinimum; set { if (Set(ref reviewMinimum, value)) FiltersChanged(); } }
    public int ReviewKindFilter { get => reviewKind; set { if (Set(ref reviewKind, value)) FiltersChanged(); } }
    void FiltersChanged() { Notify(nameof(HasReviewFilters)); Notify(nameof(CanClearReviewFilters)); }
    public string WorkerHint => workers switch
    {
        0 => "Önerilen başlangıç. SSD'de işlemciye göre en fazla 8 işçi; dönen veya türü belirlenemeyen diskte tek işçi kullanılır.",
        1 => "Düşük CPU yükü ve hız karşılaştırması için. Çok klasörlü SSD taramalarında daha uzun sürebilir.",
        2 => "Düşük paralellik. Diğer işler için kaynak bırakmak istediğinizde deneyebilirsiniz.",
        4 or 6 => "SSD/NVMe için dengeli bir deneme seçimi. 4 ve 6 işçiyi aynı klasör ve liste sınırıyla karşılaştırın; hız ağacın yapısına bağlıdır.",
        8 => "Daha yüksek paralellik CPU yükünü artırabilir; 4–6 işçiye göre ek hız sınırlı olabilir.",
        _ => "Yüksek işçi sayısı ek CPU ve iş parçacığı yükü getirir. Otomatik veya 4–6 işçiden hızlı olması garanti değildir; karşılaştırma için kullanın."
    };
    public string LimitHint => (fileLimit <= 50_000 ? "Sonuç listesinde daha düşük bellek tüketimi. Yalnız en büyük dosyalar listede tutulur; diğer dosyalar toplamda sayılır fakat bu listede aranamaz."
        : fileLimit == 100_000 ? "Önerilen denge. Bu sınır taranacak dosya sayısını değil, listede tutulacak en büyük dosya sayısını belirler."
        : "Daha kapsamlı liste daha fazla bellek ve arama/sıralama zamanı gerektirebilir. Yalnız daha küçük liste yetmiyorsa artırın; toplam ölçüm değişmez.")
        + " Tarama sırasında hedefli doğrulama için tüm dosyaların kısa metadata kayıtları geçici olarak bellekte tutulur; bu sınır sonuç listesine uygulanır.";

    public object? SelectedRow { get => selection; set { if (Set(ref selection, value)) SelectionChanged(); } }
    public string SelectionTitle => selection switch { FolderEntry f => f.Name, FileEntry f => f.Name, ReviewCandidate c => Path.GetFileName(c.Path.TrimEnd('\\')), ScanIssue => "Tarama uyarısı", _ => "Bir kayıt seçin" };
    public string? SelectedPath => selection switch { FolderEntry f => f.Path, FileEntry f => f.Path, ReviewCandidate c => c.Path, ScanIssue i => i.Path, _ => null };
    public string Feedback { get => selection is ReviewCandidate c ? feedback.GetValueOrDefault(c.Path, "Değerlendirilmedi") : "Değerlendirilmedi"; set { if (selection is ReviewCandidate c) { feedback[c.Path] = value; SelectionChanged(); } } }
    internal string FeedbackFor(ReviewCandidate candidate) => feedback.GetValueOrDefault(candidate.Path, "Değerlendirilmedi");
    public SelectionDetails Details => details ??= Inventory.Enrich(SelectionDetails.Create(scan, selection, IsReview, Feedback, ScopeText, IsUnrecognized), selection, IsReview);
    public string DetailText => Details.PlainText;
    public string FullDetails => Details.FullText;
    public string ScopeText => scan == null ? "Henüz tarama yok." : $"{ResultStatus(scan)} · {scan.RootPaths.Count} kök · {scan.IssueCount:N0} uyarı · {scan.UnverifiedPaths.Count:N0} doğrulanamayan konum · {scan.RetryCount:N0} hedefli yeniden ölçüm · {scan.RefreshedFolders:N0} klasör yenilendi · {scan.UnknownAllocation:N0} öğede alan / akış ölçümü eksik · {scan.HardLinkAliases:N0} ek hard link adı tekrar sayılmadı."
        + (scan.ExcludedPaths.Count > 0 ? $" {scan.ExcludedPaths.Count} yol bilerek hariç tutuldu; toplamlar bunları içermez.\nHariç tutulan yollar (varlıkları / boyutları ölçülmedi):\n" + string.Join("\n", scan.ExcludedPaths) : "")
        + "\n" + ReviewText.Limits;
    internal ScanResult? Scan => scan;
    internal Task CurrentScan { get; private set; } = Task.CompletedTask;
    internal string SortKey => sortKey;
    internal bool SortDescending => descending;
    internal string[] NormalizeRoots(IEnumerable<string> paths) => scanService.NormalizeRoots(paths);

    public async Task SetViewAsync(WorkspaceView next)
    {
        if (view != next)
        {
            viewStates[view] = new(search, sortKey, descending, selection);
            view = next;
            var saved = viewStates.GetValueOrDefault(next);
            search = saved?.Search ?? "";
            sortKey = saved?.SortKey ?? (next == WorkspaceView.Issues ? "Path" : "Size");
            descending = saved?.Descending ?? next != WorkspaceView.Issues;
            preferredSelection = saved?.Selection;
            selection = null; rows = Array.Empty<object>(); AppliedQuery = null; AppliedCounts = null; AppliedSearch = "";
            NotifyAll();
        }
        await RefreshAsync();
    }

    public Task ScanAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (busy) return CurrentScan;
        CurrentScan = RunScanAsync();
        return CurrentScan;
    }

    async Task RunScanAsync()
    {
        string[] roots, excluded;
        try
        {
            roots = scanService.PrepareRootsForScan(Roots.ToArray());
            if (roots.Length == 0) throw new ArgumentException("Önce klasör seçin.");
            excluded = ScanExclusions.Create(roots, Exclusions.ToArray()).Paths.ToArray();
        }
        catch (Exception ex) { ShowError(ex.Message); return; }
        CancelRefresh();
        Inventory.Bind(null);
        var source = new CancellationTokenSource();
        scanCancellation = source;
        int version = ++scanVersion, count = workers, limit = fileLimit;
        busy = true; error = ""; scan = null; Summary = null; Distribution = SpaceDistribution.Empty; folder = null; candidates = []; unrecognizedAreas = null; feedback.Clear(); viewStates.Clear(); reviewStates.Clear(); preferredSelection = null; rows = Array.Empty<object>(); selection = null;
        if (view == WorkspaceView.Settings) view = WorkspaceView.Folders;
        search = ""; AppliedSearch = ""; AppliedQuery = null; AppliedCounts = null; LogicalSize = AllocatedSize = FileCount = "—";
        status = "Dosya bilgileri taranıyor…";
        NotifyAll();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!busy || version != scanVersion) return;
            LogicalSize = PresentationText.Size(p.Bytes); FileCount = p.Files.ToString("N0");
            status = $"{ScanSummary.PhaseName(p.Phase)} · {ScanSummary.Duration(p.Elapsed)} toplam · {p.Folders:N0} klasör · {p.Issues:N0} uyarı · {p.Current}";
            Notify(nameof(LogicalSize)); Notify(nameof(FileCount)); Notify(nameof(Status));
        });
        try
        {
            var completed = await scanService.ScanAsync(new ScanRequest(roots, count, limit, excluded), progress, source.Token);
            if (disposed || version != scanVersion) return;
            ApplyOutcome(completed);
        }
        catch (Exception ex) { if (!disposed) { ShowError(ex.Message); status = "Tarama tamamlanamadı. Ayrıntıyı kontrol edin."; } }
        finally { busy = false; scanCancellation = null; source.Dispose(); if (!disposed) NotifyAll(); }
        await RefreshAsync();
    }

    public void CancelScan() => scanCancellation?.Cancel();
    public void ShowError(string message) { error = message; Notify(nameof(Error)); Notify(nameof(HasError)); }
    public void ClearError() { error = ""; Notify(nameof(Error)); Notify(nameof(HasError)); }
    public async Task GoUpAsync() { if (CanGoUp) await BrowseAsync(folder!.Parent!); }
    public async Task EnterAsync(FolderEntry target)
    {
        if (!disposed && !busy && !refreshing && (view == WorkspaceView.Folders || IsUnrecognized) && scan?.CanUsePath(target.Path) == true
            && target.Measurement is not (MeasurementState.Unknown or MeasurementState.Previous)
            && rows.Any(item => ReferenceEquals(item, target)))
        {
            if (IsUnrecognized)
            {
                viewStates[view] = new(search, sortKey, descending, target);
                view = WorkspaceView.Folders; sortKey = "Size"; descending = true;
            }
            await BrowseAsync(target);
        }
    }
    public async Task SetReviewCollectionAsync(int next)
    {
        if (!IsReview || next is not (0 or 1) || next == reviewCollection || busy || disposed) return;
        reviewStates[reviewCollection] = new(search, sortKey, descending, selection);
        reviewCollection = next;
        var saved = reviewStates.GetValueOrDefault(next);
        search = saved?.Search ?? ""; sortKey = saved?.SortKey ?? "Size"; descending = saved?.Descending ?? true;
        preferredSelection = saved?.Selection; selection = null; rows = Array.Empty<object>(); AppliedQuery = null; AppliedCounts = null; AppliedSearch = "";
        NotifyAll(); await RefreshAsync();
    }
    public async Task ClearReviewFiltersAsync()
    {
        if (!CanClearReviewFilters) return;
        search = ""; reviewMinimum = 0; if (IsKnownReview) reviewKind = 0;
        preferredSelection = selection; selection = null; rows = Array.Empty<object>(); AppliedQuery = null; AppliedCounts = null; AppliedSearch = "";
        NotifyAll(); await RefreshAsync();
    }
    async Task BrowseAsync(FolderEntry target)
    {
        folder = target; selection = null; preferredSelection = null; Distribution = SpaceDistribution.Empty; search = ""; rows = Array.Empty<object>(); AppliedQuery = null; AppliedCounts = null; AppliedSearch = "";
        NotifyAll(); await RefreshAsync();
    }
    public Task SortAsync(string key)
    {
        descending = sortKey == key ? !descending : key is "Size" or "Allocated" or "Modified";
        sortKey = key;
        return RefreshAsync();
    }
    public Task SetDistributionModeAsync(bool enabled)
    {
        if (!CanSwitchPresentation || distributionMode == enabled) return Task.CompletedTask;
        distributionMode = enabled;
        Notify(nameof(IsDistribution)); Notify(nameof(ShowTable)); Notify(nameof(ShowMetrics));
        return RefreshAsync();
    }
    public void SelectDistributionSegment(DistributionSegment segment)
    {
        if (CanUseDistribution && Distribution.Segments.Contains(segment) && segment.Folder is { } target
            && rows.Any(row => ReferenceEquals(row, target))) SelectedRow = target;
    }

    public async Task RefreshAsync(bool debounce = false)
    {
        CancelRefresh();
        if (busy || disposed) return;
        var result = scan;
        if (result == null || view == WorkspaceView.Settings) { rows = Array.Empty<object>(); selection = null; Distribution = SpaceDistribution.Empty; NotifyAll(); return; }
        var source = new CancellationTokenSource(); refreshCancellation = source;
        var token = source.Token;
        int version = refreshVersion;
        var query = new ViewQuery(view, folder, search.Trim(), fullPath, reviewMinimum, reviewKind, sortKey, descending, IsUnrecognized);
        var sourceCandidates = candidates;
        var sourceAreas = unrecognizedAreas;
        refreshing = true; StateChanged();
        try
        {
            if (debounce) await Task.Delay(200, token);
            bool chart = IsDistribution;
            var prepared = await Task.Run(() =>
            {
                var filtered = ResultQuery.Execute(result, sourceCandidates, query, token, sourceAreas);
                var distribution = chart && query.Folder != null
                    ? SpaceDistribution.Create(result, query.Folder, filtered.Rows, query.Search.Length > 0, token) : SpaceDistribution.Empty;
                return (Filtered: filtered, Distribution: distribution);
            }, token);
            if (token.IsCancellationRequested || version != refreshVersion || !ReferenceEquals(scan, result)) return;
            var filtered = prepared.Filtered;
            if (filtered.Areas != null) unrecognizedAreas = filtered.Areas;
            Distribution = prepared.Distribution;
            object? current = selection ?? preferredSelection;
            rows = filtered.Rows; MaximumVisibleSize = filtered.Maximum; selection = current != null && filtered.Rows.Contains(current) ? current : null;
            preferredSelection = null;
            AppliedSearch = filtered.Query.Search; AppliedQuery = filtered.Query; AppliedCounts = filtered.Counts;
            NotifyAll();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (InvalidOperationException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (version == refreshVersion) ShowError(ex.Message); }
        finally { if (ReferenceEquals(refreshCancellation, source)) { refreshCancellation = null; refreshing = false; StateChanged(); } source.Dispose(); }
    }

    void CancelRefresh() { refreshVersion++; var old = refreshCancellation; refreshCancellation = null; old?.Cancel(); refreshing = false; }
    void SelectionChanged() { details = null; Notify(nameof(Details)); Notify(nameof(SelectionTitle)); Notify(nameof(SelectedPath)); Notify(nameof(DetailText)); Notify(nameof(FullDetails)); Notify(nameof(Feedback)); StateChanged(); }
    void StateChanged()
    {
        foreach (string name in new[] { nameof(IsBusy), nameof(IsRefreshing), nameof(CanScan), nameof(CanStop), nameof(CanConfigure), nameof(CanSearch), nameof(CanExport), nameof(CanUseSelection), nameof(CanGoUp), nameof(ListCaption), nameof(ListCaptionDetail), nameof(HasReviewFilters), nameof(CanClearReviewFilters), nameof(EmptyMessage), nameof(CanSwitchPresentation), nameof(CanUseDistribution) }) Notify(name);
    }
    void NotifyAll()
    {
        details = null; bool previous = IsPublishingState; IsPublishingState = true;
        try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
        finally { IsPublishingState = previous; }
    }
    void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name); return true; }
    internal static string ResultStatus(ScanResult s) => s.ScopeChanged ? "Kapsam değişti · kısmi sonuç" : s.Cancelled ? "Durduruldu · kısmi sonuç"
        : s.NeedsRetry ? "Kararlı ölçüm alınamadı · son ölçüm · kısmi sonuç" : s.Failed ? "Ölçülemeyen kök var · kısmi sonuç"
        : s.HasUnverified ? "Tamamlandı · doğrulanamayan alanlar var · kısmi sonuç"
        : s.IssueCount > 0 || s.UnknownAllocation > 0 ? "Tamamlandı · ölçüm uyarıları var"
        : s.RetryCount > 0 ? "Yeniden doğrulanarak tamamlandı" : s.ExcludedPaths.Count > 0 ? "Tamamlandı · hariç tutulan alanlar var" : "Tamamlandı · atlanan alanlar olabilir";
    void ApplyOutcome(ScanOutcome outcome)
    {
        scan = outcome.Scan; folder = scan.Root; candidates = outcome.Candidates; selection = null;
        Inventory.Bind(scan);
        var result = outcome.Scan;
        Summary = ScanSummary.Create(result);
        LogicalSize = result.Root.Measurement == MeasurementState.Unknown ? "Bilinmiyor" : PresentationText.Size(result.Root.LogicalBytes);
        AllocatedSize = result.Root.Measurement == MeasurementState.Unknown ? "Bilinmiyor" : PresentationText.Size(result.Root.AllocatedBytes);
        FileCount = result.Root.Measurement == MeasurementState.Unknown ? "Bilinmiyor" : result.Root.FileCount.ToString("N0");
        status = $"{ResultStatus(result)} · {result.Elapsed.TotalSeconds:N2} sn · {result.Folders:N0} klasör · {result.IssueCount:N0} uyarı · {result.RetryCount} hedefli yeniden ölçüm · {result.RefreshedFolders:N0} klasör yenilendi · {result.Workers} işçi";
    }
    public void Dispose() { if (disposed) return; disposed = true; scanVersion++; scanCancellation?.Cancel(); CancelRefresh(); Inventory.Dispose(); }
}

