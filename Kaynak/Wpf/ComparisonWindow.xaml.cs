using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using YerelDisk.Presentation;

namespace YerelDisk.Wpf;

public partial class ComparisonWindow : Window
{
    const string CompactFilter = "Sıkıştırılmış YerelDisk ölçümü (*.yereldisk)|*.yereldisk";
    const string OpenFilter = "YerelDisk ölçümleri (*.yereldisk, *.yereldisk.json)|*.yereldisk;*.yereldisk.json";
    readonly ScanResult scan;
    readonly DateTimeOffset measuredAt;
    readonly ReferenceLibrary library;
    FolderMeasurements? current;
    FolderComparison? comparison;
    string? parent;
    bool busy;
    internal string? InspectionPath { get; private set; }

    internal ComparisonWindow(ScanResult scan, DateTimeOffset measuredAt, ReferenceLibrary? library = null)
    {
        this.scan = scan;
        this.measuredAt = measuredAt;
        this.library = library ?? new ReferenceLibrary();
        InitializeComponent();
        Dates.Text = $"Son ölçüm · {measuredAt.ToLocalTime():g}";
        Feedback.Text = "İlk kullanımda bu ölçümü kaydedin. Sonraki taramadan sonra aynı dosyayı burada açın.";
    }

    FolderMeasurements Current() => current ??= FolderMeasurements.Capture(scan, measuredAt);

    async void SaveReference(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new SaveFileDialog { Filter = CompactFilter, DefaultExt = ".yereldisk", InitialDirectory = library.PickerDirectory(),
            FileName = $"YerelDisk-olcum-{measuredAt.ToLocalTime():yyyy-MM-dd-HHmm}.yereldisk", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        await SaveReferenceAsync(dialog.FileName);
    }

    internal Task SaveReferenceAsync(string filename)
        => RunAsync("Ölçüm kaydediliyor…", async () =>
        {
            long fileBytes = await Task.Run(() => Current().WriteWithSize(filename, compressed: true));
            Feedback.Text = "Referans kaydedildi. Daha sonra aynı kapsamı tarayıp bu dosyayı açın.";
            await RecordReference(filename, measuredAt, fileBytes);
        });

    async void OpenReference(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Filter = OpenFilter, CheckFileExists = true, InitialDirectory = library.PickerDirectory() };
        if (dialog.ShowDialog(this) != true) return;
        await OpenReferenceAsync(dialog.FileName);
    }

    internal Task OpenReferenceAsync(string filename)
        => RunAsync("Tarama farkları hazırlanıyor…", async () =>
        {
            var opened = await Task.Run(() =>
            {
                var (reference, fileBytes) = FolderMeasurements.ReadWithSize(filename);
                return (Comparison: new FolderComparison(reference, Current()), FileBytes: fileBytes);
            });
            comparison = opened.Comparison;
            Dates.Text = $"Referans · {comparison.Reference.MeasuredAt.ToLocalTime():g}     Son ölçüm · {measuredAt.ToLocalTime():g}";
            if (comparison.HasUnverifiedVolumeIdentity)
                Dates.Text += " · Disk kimliği doğrulanamadı";
            Navigation.Visibility = Changes.Visibility = Visibility.Visible;
            DisplayLargest();
            await RecordReference(filename, comparison.Reference.MeasuredAt, opened.FileBytes);
        });

    async Task RecordReference(string filename, DateTimeOffset date, long fileBytes)
    {
        library.Record(filename, date, fileBytes);
        string? warning = await Task.Run(library.Persist);
        if (warning != null) Feedback.Text += " " + warning;
    }

    async void ShowLibrary(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var window = new ReferenceLibraryWindow(library) { Owner = this };
        bool selected = window.ShowDialog() == true;
        string? warning = null;
        if (window.Changed)
            await RunAsync("Referans tercihleri uygulanıyor…", async () =>
            {
                warning = await Task.Run(library.Persist);
                Feedback.Text = warning ?? "Referans tercihleri güncellendi.";
            });
        if (selected && window.SelectedPath is { } path)
        {
            await OpenReferenceAsync(path);
            if (warning != null && !Feedback.Text.Contains(warning, StringComparison.Ordinal)) Feedback.Text += " " + warning;
        }
    }

    void DisplayLargest()
    {
        if (comparison == null) return;
        string coverage = comparison.UncomparableCount == 0 ? ""
            : $" {comparison.UncomparableCount:N0} klasör karşılaştırılamadı. Seçili köklerden bu alanlara inebilirsiniz.";
        Feedback.Text = (comparison.Largest.Count == 0 ? "Karşılaştırılabilen klasörlerde boyut farkı bulunmadı."
            : $"En büyük {comparison.Largest.Count:N0} doğrulanmış fark, değişimin büyüklüğüne göre sıralandı.") + coverage;
        ShowRows(comparison.Largest, null, "Tüm seçili alanlardaki en büyük farklar");
    }

    void ShowLargest(object sender, RoutedEventArgs e) => DisplayLargest();
    async void ShowRoots(object sender, RoutedEventArgs e) => await ShowFolder(null);
    async void GoUp(object sender, RoutedEventArgs e)
    {
        if (parent == null || comparison == null) return;
        await ShowFolder(comparison.Current.Roots.Contains(parent, StringComparer.Ordinal) ? null : FolderMeasurements.ParentOf(parent));
    }
    async void EnterSelected(object sender, RoutedEventArgs e) => await EnterAsync();
    async void RowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Changes, e.OriginalSource as DependencyObject) is DataGridRow)
            await EnterAsync();
    }
    async void GridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Changes.SelectedItem is not FolderChange { CanEnter: true }) return;
        e.Handled = true;
        await EnterAsync();
    }
    Task EnterAsync() => Changes.SelectedItem is FolderChange { CanEnter: true } row ? ShowFolder(row.Path) : Task.CompletedTask;

    Task ShowFolder(string? path) => RunAsync("Alt klasörler hazırlanıyor…", async () =>
    {
        if (comparison == null) return;
        var rows = await Task.Run(() => comparison.At(path));
        Feedback.Text = "Bu düzeydeki klasörler. Karşılaştırılamayan bir klasörde ölçülebilen alt alanlar bulunabilir.";
        ShowRows(rows, path, path ?? "Seçili kökler");
    });

    void ShowRows(IReadOnlyList<FolderChange> rows, string? path, string caption)
    {
        parent = path;
        UpButton.IsEnabled = parent != null;
        Location.Text = caption;
        Changes.ItemsSource = rows;
        Changes.SelectedItem = null;
        UpdateSelectionActions();
    }

    void SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelectionActions();

    void UpdateSelectionActions()
    {
        var row = Changes.SelectedItem as FolderChange;
        EnterButton.IsEnabled = !busy && row is { CanEnter: true };
        InspectButton.IsEnabled = !busy && ComparisonInspection.Resolve(scan, row) != null;
        string hint = ComparisonInspection.Hint(scan, row);
        InspectionHint.Text = hint + " Üst ve alt klasörler örtüşebilir; satırları toplamayın.";
        InspectButton.ToolTip = hint;
    }

    void InspectSelected(object sender, RoutedEventArgs e)
    {
        if (busy || Changes.SelectedItem is not FolderChange row
            || ComparisonInspection.Resolve(scan, row) is not { } folder) return;
        InspectionPath = folder.Path;
        Close();
    }

    async Task RunAsync(string status, Func<Task> action)
    {
        if (busy) return;
        busy = true;
        UpdateSelectionActions();
        FileActions.IsEnabled = Navigation.IsEnabled = Changes.IsEnabled = false;
        Feedback.Text = status;
        try { await action(); }
        catch (JsonException) { Feedback.Text = "Ölçüm dosyası okunamadı. YerelDisk ile kaydedilmiş geçerli bir ölçüm seçin."; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        { Feedback.Text = FileErrorText(ex) ?? ex.Message; }
        finally
        {
            busy = false;
            FileActions.IsEnabled = Navigation.IsEnabled = Changes.IsEnabled = true;
            UpdateSelectionActions();
        }
    }

    // Claude 1.20.1: .NET file APIs report these with English text ("Access to the path is
    // denied."). YerelDisk's own IOExceptions already carry Turkish text and pass through.
    internal static string? FileErrorText(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "Dosya veya klasör bulunamadı. Konumu denetleyip yeniden deneyin.",
        UnauthorizedAccessException => "Dosyaya erişilemedi. Dosya başka bir programda açık, salt okunur veya bu konuma yazma izni yok olabilir. Başka bir ad ya da konum seçin.",
        IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) }
            => "Dosya başka bir programda açık. Programı kapatıp yeniden deneyin ya da başka bir ad seçin.",
        _ => null
    };

    void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!busy) return;
        // Claude 1.20.1: say why the window stays open instead of ignoring the request.
        e.Cancel = true;
        Feedback.Text = "İşlem sürüyor. Bitince pencereyi kapatabilirsiniz.";
    }
}
