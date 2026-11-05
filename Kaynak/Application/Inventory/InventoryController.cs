using System.ComponentModel;

namespace YerelDisk.Presentation;

internal sealed class InventoryController(IInventoryProvider provider) : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    ScanResult? scan;
    InventoryPathIndex? index;
    CancellationTokenSource? cancellation;
    int generation;
    bool disposed, attempted, exportHints;
    string status = "Kapalı · Bu oturumda uygulama kayıtları okunmadı.", coverage = "";
    public bool IsBusy { get; private set; }
    public bool HasSnapshot => index != null;
    public bool CanInspect => !disposed && !IsBusy && scan != null && scan.Root.Measurement != MeasurementState.Unknown && scan.RootPaths.Any(scan.CanUsePath);
    public bool CanForget => IsBusy || index != null || coverage.Length > 0;
    public string Status => status;
    public string Coverage => coverage;
    public int Revision { get; private set; }
    public bool ExportHints { get => exportHints; set { bool next = value && HasSnapshot; if (exportHints != next) { exportHints = next; Notify(); } } }
    internal InventoryPathIndex? Index => index;
    internal Task CurrentOperation { get; private set; } = Task.CompletedTask;

    internal void Bind(ScanResult? next)
    {
        generation++; cancellation?.Cancel(); scan = next; index = null; coverage = ""; exportHints = false;
        status = attempted ? "Önceki ipuçları bırakıldı; bu tarama için inceleme düğmesini yeniden kullanabilirsiniz." : "Kapalı · Bu oturumda uygulama kayıtları okunmadı.";
        Changed();
    }
    public Task InspectAsync()
    {
        if (!CanInspect) return CurrentOperation;
        CurrentOperation = RunAsync(); return CurrentOperation;
    }
    async Task RunAsync()
    {
        var context = scan!; int version = ++generation; var source = new CancellationTokenSource(); cancellation = source;
        attempted = true; IsBusy = true; index = null; exportHints = false; coverage = ""; status = "Uygulama kayıtları yerelde inceleniyor…"; Changed();
        try
        {
            var snapshot = await provider.ReadAsync(source.Token);
            source.Token.ThrowIfCancellationRequested();
            var prepared = snapshot.Readable ? await Task.Run(() => InventoryPathIndex.Create(context, snapshot, source.Token), source.Token) : null;
            if (disposed || source.IsCancellationRequested || version != generation || !ReferenceEquals(scan, context)) return;
            coverage = snapshot.Coverage; index = prepared;
            status = prepared == null ? "İnceleme tamamlanamadı; kurulum kaydı ilişkisi üretilmedi."
                : $"{snapshot.Applications.Count:N0} kullanılabilir yol kaydı · {(snapshot.Partial ? "kısmi inceleme" : "belirtilen kaynaklar okundu")} · {snapshot.Finished.ToLocalTime():g}. Güncel kullanım bilinmiyor.";
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { if (version == generation) status = "İnceleme durduruldu; ipuçları kullanılmıyor."; }
        catch (Exception) { if (version == generation && !disposed) { index = null; coverage = ""; status = "Kayıt incelemesi tamamlanamadı. Hiçbir ilişki doğrulanmış sayılmadı."; } }
        finally
        {
            if (ReferenceEquals(cancellation, source)) { cancellation = null; IsBusy = false; if (!disposed) Changed(); }
            source.Dispose();
        }
    }
    public void Forget()
    { generation++; cancellation?.Cancel(); index = null; exportHints = false; coverage = ""; status = attempted ? "İpuçları bırakıldı · Bu oturumda daha önce inceleme yapıldı." : "Kapalı · Bu oturumda okunmadı."; Changed(); }
    internal string RowSummary(object? item) => item is ReviewCandidate or FolderEntry ? index?.Summary(RowValues.PathOf(item)) ?? "" : "";
    internal SelectionDetails Enrich(SelectionDetails details, object? item, bool review)
    {
        if (!review || item is not (ReviewCandidate or FolderEntry) || details.Path.Length == 0 || index == null) return details;
        string text = index.Details(details.Path);
        var sections = details.Sections.ToList(); sections.Insert(Math.Min(2, sections.Count), new("Uygulama kaydı ilişkisi", text));
        return details with { Sections = sections, FullText = details.FullText + "\n\nUygulama kaydı ilişkisi\n" + text };
    }
    internal InventoryExport? CaptureExport(bool review) => review && exportHints && index != null ? new(index) : null;
    void Changed() { Revision++; Notify(); }
    void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() { if (disposed) return; disposed = true; generation++; cancellation?.Cancel(); index = null; coverage = ""; exportHints = false; }
}

internal sealed record InventoryExport(InventoryPathIndex Index)
{
    // Invoked by the report writer, not on the UI capture path. No new reads.
    internal void Write(TextWriter writer, IEnumerable<object> rows)
    {
        writer.WriteLine(PresentationText.Csv(["Uygulama kaydı kapsamı", Index.Snapshot.Coverage]));
        int written = 0; const int maximumLinks = 4096;
        foreach (var row in rows)
        {
            if (row is not (FolderEntry or ReviewCandidate)) continue;
            string path = RowValues.PathOf(row);
            foreach (var hit in Index.Matches(path))
            {
                if (written >= maximumLinks)
                { writer.WriteLine(PresentationText.Csv(["Uygulama ipucu dışa aktarma sınırı", "4096 ilişki sınırı doldu; sonraki satır ipuçları yazılmadı. Ana tablo satırları korunur."])); return; }
                writer.WriteLine(PresentationText.Csv(["Uygulama kaydı ipucu", path, InventoryPathIndex.HitText(hit), "Anlamı", "Yalnız kayıt/yol örtüşmesi; kurulu olma, güncel kullanım veya gereksizlik kanıtı değil."])); written++;
            }
        }
        writer.WriteLine(PresentationText.Csv(["Yazılan uygulama ilişkisi", written.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
    }
}
