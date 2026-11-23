using System.Windows;
using YerelDisk.Presentation;

namespace YerelDisk.Wpf;

public partial class PopulationWindow : Window
{
    readonly ScanResult scan;
    readonly FolderEntry target;
    readonly CancellationTokenSource stop = new();
    bool started, closed, finished;
    internal Task PendingAnalysis { get; private set; } = Task.CompletedTask;
    internal FilePopulationAnalysis? Analysis { get; private set; }

    internal PopulationWindow(ScanResult scan, FolderEntry target)
    {
        this.scan = scan; this.target = target;
        InitializeComponent();
        Location.Text = target.Path;
        Limits.Text = FilePopulationAnalysis.Limits;
        Loaded += async (_, _) => await PrepareAsync();
        Closed += (_, _) =>
        {
            closed = true;
            if (!finished) stop.Cancel();
            if (!started) { finished = true; stop.Dispose(); }
        };
    }

    internal Task PrepareAsync()
    {
        if (closed) return Task.CompletedTask;
        if (!started) { started = true; PendingAnalysis = LoadAsync(); }
        return PendingAnalysis;
    }
    async Task LoadAsync()
    {
        var token = stop.Token;
        try
        {
            var result = await Task.Run(() => FilePopulationAnalysis.Create(scan, target, token), token);
            if (closed) return;
            Analysis = result;
            Heading.Text = result.Heading;
            Explanation.Text = result.Available ? result.Explanation : "Bu konum için dosya yapısı gösterilemiyor.";
            Coverage.Text = result.Coverage;
            TotalCount.Text = result.Available ? result.CountText : "—";
            SmallCount.Text = result.Available ? result.SmallText : "—";
            EmptyCount.Text = result.Available ? result.EmptyText : "—";
            TypesGrid.ItemsSource = result.Types; AreasGrid.ItemsSource = result.Areas;
            AnalysisTabs.IsEnabled = result.Available;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!closed) Explanation.Text = "Analiz hazırlanamadı: " + ex.Message; }
        finally { finished = true; stop.Dispose(); }
    }
}
