using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using YerelDisk.Presentation;

namespace YerelDisk.Wpf;

public partial class MainWindow : Window
{
    internal WorkspaceViewModel Model { get; }
    bool initialized, dark, closing, allowClose, exporting;
    Task exportTask = Task.CompletedTask;
    internal Task PendingView { get; private set; } = Task.CompletedTask;
    int navigationVersion;
    object? lastSelection;
    public MainWindow() : this(new WorkspaceViewModel()) { }
    internal MainWindow(WorkspaceViewModel model)
    {
        Model = model;
        InitializeComponent();
        DataContext = Model;
        initialized = true;
        Model.PropertyChanged += ModelChanged;
        DistributionChart.SelectionRequested += Model.SelectDistributionSegment;
        DistributionChart.NavigationRequested += async segment =>
        {
            if (Model.CanUseDistribution && Model.Distribution.Segments.Contains(segment) && segment.CanEnter && segment.Folder is { } target)
                await Model.EnterAsync(target);
        };
        ConfigureColumns();
        WindowPlacement.Attach(this);
    }

    async void Navigate(object sender, RoutedEventArgs e)
    {
        if (!initialized || sender is not RadioButton { Tag: string name } || !Enum.TryParse<WorkspaceView>(name, out var view)) return;
        await NavigateAsync(view);
    }
    internal async Task NavigateAsync(WorkspaceView view)
    {
        int version = ++navigationVersion;
        Task transition = Model.SetViewAsync(view);
        (view switch { WorkspaceView.Folders => FoldersNav, WorkspaceView.Files => FilesNav, WorkspaceView.Review => ReviewNav, WorkspaceView.Issues => IssuesNav, _ => SettingsNav }).IsChecked = true;
        ConfigureColumns(); FitWorkspace();
        await transition;
        if (version != navigationVersion || Model.View != view) return;
        FitWorkspace();
    }
    internal void ConfigureColumns()
    {
        ResultsGrid.Columns.Clear();
        ResultsGrid.RowHeight = Model.IsReview ? double.NaN : 42;
        ResultsGrid.MinRowHeight = Model.IsReview ? 80 : 0;
        VirtualizingPanel.SetScrollUnit(ResultsGrid, Model.IsReview ? ScrollUnit.Pixel : ScrollUnit.Item);
        void AddReview(string title, string key, string template, double minimum, double stars = 0)
        {
            var column = new DataGridTemplateColumn
            {
                Header = title, SortMemberPath = key, CellTemplate = (DataTemplate)FindResource(template), MinWidth = minimum,
                Width = stars > 0 ? new DataGridLength(stars, DataGridLengthUnitType.Star) : new DataGridLength(minimum),
                SortDirection = Model.SortKey == key ? Model.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending : null
            };
            ResultsGrid.Columns.Add(column);
        }
        void Add(string title, string key, double width, bool fill = false)
        {
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            textStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(".") { Converter = new CellTextConverter(), ConverterParameter = key }));
            DataGridColumn column = Model.View == WorkspaceView.Folders && key == "Name"
                ? new DataGridTemplateColumn { CellTemplate = (DataTemplate)FindResource("FolderNameTemplate") }
                : new DataGridTextColumn { Binding = new Binding(".") { Converter = new CellTextConverter(), ConverterParameter = key }, ElementStyle = textStyle };
            column.Header = title; column.SortMemberPath = key;
            column.Width = fill ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width);
            column.MinWidth = fill ? width : 70;
            column.SortDirection = Model.SortKey == key ? Model.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending : null;
            ResultsGrid.Columns.Add(column);
        }
        switch (Model.View)
        {
            case WorkspaceView.Folders:
                Add("Klasör", "Name", 180, true); Add("Boyut", "Size", 130); Add("Ayrılan alan¹", "Allocated", 130); Add("Dosya", "Count", 95); Add("Uyarı", "Issues", 80);
                Add("Ölçüm", "Measurement", 145);
                if (Model.TechnicalColumns) { Add("Kendi ek akışları", "Streams", 140); Add("Tam konum", "Path", 350); }
                break;
            case WorkspaceView.Files:
                Add("Dosya", "Name", 220, true); Add("Boyut", "Size", 125); Add("Ayrılan alan¹", "Allocated", 130); Add("Değiştirilme", "Modified", 145);
                Add("Ölçüm", "Measurement", 145);
                if (Model.TechnicalColumns) { Add("Ek akışlar", "Streams", 140); Add("Tam konum", "Path", 380); }
                break;
            case WorkspaceView.Review:
                if (Model.IsUnrecognized)
                {
                    AddReview("Klasör / üst konum", "Name", "UnrecognizedNameTemplate", 160, 1); Add("Boyut¹", "Size", 95);
                    AddReview("İlk kontrol", "Path", "UnrecognizedSignalTemplate", 250, 2);
                    ResultsGrid.Columns[^1].CanUserSort = false;
                    AddReview("Ölçüm", "Measurement", "ReviewMeasurementTemplate", 125);
                    if (Model.TechnicalColumns) { Add("Dosya", "Count", 95); Add("Uyarı", "Issues", 80); Add("Tam konum", "Path", 380); }
                    break;
                }
                AddReview("Klasör / amaç", "Name", "ReviewNameTemplate", 160, 1); Add("Boyut¹", "Size", 95);
                AddReview("Değerlendirme / ilk adım", "ReviewSignal", "ReviewSignalTemplate", 250, 2);
                AddReview("Ölçüm", "Measurement", "ReviewMeasurementTemplate", 125);
                if (Model.TechnicalColumns) { Add("Tür / karar koşulu", "Kind", 185); Add("Amaç dayanağı", "Evidence", 190); Add("Tam konum", "Path", 380); }
                break;
            case WorkspaceView.Issues:
                Add("Konum", "Path", 250); Add("Açıklama", "Reason", 260, true);
                break;
        }
    }

    void ChooseRoots(object sender, RoutedEventArgs e)
    {
        var dialog = new RootsWindow(Model.Roots, Model.NormalizeRoots, Model.Exclusions) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Model.Roots.Clear();
        foreach (string path in dialog.Paths) Model.Roots.Add(path);
        Model.Exclusions.Clear();
        foreach (string path in dialog.Exclusions) Model.Exclusions.Add(path);
        Model.ClearError();
    }
    async void StartScan(object sender, RoutedEventArgs e)
    {
        if (Model.IsSettings) await NavigateAsync(WorkspaceView.Folders);
        await Model.ScanAsync(); ConfigureColumns();
        if (Model.View == WorkspaceView.Folders) FoldersNav.IsChecked = true;
    }
    async void ShowTable(object sender, RoutedEventArgs e) => await Model.SetDistributionModeAsync(false);
    async void ShowDistribution(object sender, RoutedEventArgs e) => await Model.SetDistributionModeAsync(true);
    void StopScan(object sender, RoutedEventArgs e) => Model.CancelScan();
    async void GoUp(object sender, RoutedEventArgs e) => await Model.GoUpAsync();
    async void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || Model.IsPublishingState) return;
        // Snapshot the control value explicitly: TextChanged and two-way binding
        // updates must not race and leave the list one keystroke behind.
        Model.Search = SearchBox.Text;
        PendingView = Model.RefreshAsync(true); await PendingView;
    }
    async void ReviewFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || Model.IsPublishingState || !Model.IsReview) return;
        if (ReviewMinimumSelector.SelectedValue is int minimum) Model.ReviewMinimum = minimum;
        if (ReviewKindSelector.SelectedValue is int kind) Model.ReviewKindFilter = kind;
        PendingView = Model.RefreshAsync(); await PendingView;
    }
    async void ReviewCollectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || Model.IsPublishingState || !Model.IsReview || ReviewCollectionSelector.SelectedValue is not int next) return;
        PendingView = Model.SetReviewCollectionAsync(next);
        ConfigureColumns(); FitWorkspace(); await PendingView;
    }
    async void FolderFilterChanged(object sender, RoutedEventArgs e) => await Model.RefreshAsync();
    void TechnicalChanged(object sender, RoutedEventArgs e) => ConfigureColumns();
    void ClearSearch(object sender, RoutedEventArgs e) => Model.Search = "";
    async void ClearReviewFilters(object sender, RoutedEventArgs e) { PendingView = Model.ClearReviewFiltersAsync(); await PendingView; }
    async void SortResults(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        await Model.SortAsync(e.Column.SortMemberPath);
        foreach (var column in ResultsGrid.Columns) column.SortDirection = column.SortMemberPath == Model.SortKey ? Model.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending : null;
    }
    async void ResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-clicks on headers/empty space never act on the previous selection.
        if (ItemsControl.ContainerFromElement(ResultsGrid, e.OriginalSource as DependencyObject) is DataGridRow { Item: FolderEntry folder })
            await EnterFolderAsync(folder);
    }
    async Task EnterFolderAsync(FolderEntry folder)
    { var before = Model.View; await Model.EnterAsync(folder); if (Model.View != before) await NavigateAsync(Model.View); }
    async void ResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || !Model.CanUseSelection || (!Model.IsFolderView && !Model.IsUnrecognized)) return;
        if (ItemsControl.ContainerFromElement(ResultsGrid, e.OriginalSource as DependencyObject) is not DataGridRow { Item: FolderEntry folder }
            || !ReferenceEquals(folder, Model.SelectedRow)) return;
        e.Handled = true; PendingView = EnterFolderAsync(folder); await PendingView;
    }
    void OpenLocation(object sender, RoutedEventArgs e)
    {
        if (!Model.CanUseSelection || Model.SelectedPath is not { } path) return;
        try { Process.Start(PresentationText.Explorer(path)); } catch (Exception ex) { Model.ShowError(ex.Message); }
    }
    void CopyPath(object sender, RoutedEventArgs e)
    {
        if (!Model.CanUseSelection || Model.SelectedPath is not { } path) return;
        try { Clipboard.SetText(path); } catch (Exception ex) { Model.ShowError(ex.Message); }
    }
    void ShowDetails(object sender, RoutedEventArgs e)
    {
        if (!Model.CanSearch) return;
        CreateDetailsWindow().ShowDialog();
    }
    internal DetailsWindow CreateDetailsWindow() => new(Model.Details) { Owner = this };
    void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(WorkspaceViewModel.IsDistribution)) FitWorkspace();
        if (e.PropertyName is not (null or nameof(WorkspaceViewModel.SelectedRow))) return;
        if (!ReferenceEquals(lastSelection, Model.SelectedRow)) { lastSelection = Model.SelectedRow; SelectionScroll.ScrollToTop(); }
    }

    async void Export(object sender, RoutedEventArgs e)
    {
        if (!Model.CanExport || exporting) return;
        var dialog = new SaveFileDialog { Filter = "CSV raporu (*.csv)|*.csv", FileName = "YerelDisk-rapor.csv", AddExtension = true, DefaultExt = ".csv", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        // Capture the applied view before returning to the message loop. Further navigation
        // or a new scan cannot change the report being written in the background.
        var report = ReportSnapshot.Capture(Model);
        exporting = true;
        try { exportTask = Task.Run(() => report.Write(dialog.FileName)); await exportTask; }
        catch (Exception ex) { Model.ShowError(ex.Message); }
        finally { exporting = false; }
    }

    void ToggleTheme(object sender, RoutedEventArgs e) => SetTheme(!dark);
    async void InspectInventory(object sender, RoutedEventArgs e) => await Model.Inventory.InspectAsync();
    void ForgetInventory(object sender, RoutedEventArgs e) => Model.Inventory.Forget();
    internal void SetTheme(bool useDark)
    {
        dark = useDark;
        string[] keys = ["Canvas", "Surface", "Soft", "Ink", "Muted", "Line", "Accent", "Selection", "WarningSurface", "WarningLine"];
        string[] colors = dark ? ["#101827", "#192437", "#202E43", "#EDF2FA", "#A9B7CC", "#314159", "#8FA7FF", "#2C3F66", "#352E24", "#6F5B35"] : ["#F3F5FA", "#FFFFFF", "#F5F7FC", "#202C45", "#64728B", "#E2E7F0", "#4263EB", "#EAF0FF", "#FFF4DE", "#EBD6AA"];
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
    async void ResetSettings(object sender, RoutedEventArgs e)
    {
        Model.Inventory.Forget();
        Model.Workers = 0; Model.FileLimit = 100_000; Model.FullFolderPath = false; Model.TechnicalColumns = false; SetTheme(false); ConfigureColumns(); await Model.RefreshAsync();
    }
    void WindowSizeChanged(object sender, SizeChangedEventArgs e) { if (initialized) FitWorkspace(); }
    void FitWorkspace()
    {
        // At narrow widths details have a dedicated F6 dialog rather than squeezing
        // the table and explaining risks inside an unreadably small panel.
        bool side = ActualWidth >= 1220;
        DetailsCard.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        CompactSelectionActions.Visibility = side ? Visibility.Collapsed : Visibility.Visible;
        DetailSplitter.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        Workspace.ColumnDefinitions[1].Width = new GridLength(side ? 12 : 0);
        Workspace.ColumnDefinitions[2].Width = new GridLength(side ? 310 : 0);
        bool compact = ActualHeight < 790;
        bool compactReview = compact && Model.IsReview;
        ResultsPathRow.Visibility = compactReview ? Visibility.Collapsed : Visibility.Visible;
        ScopeNoticeCard.Tag = compactReview;
        PageHeader.Tag = compact;
        bool compactChart = compact && Model.IsDistribution || compactReview;
        ScopeCard.Padding = compactReview ? new Thickness(14, 4, 14, 4) : compactChart ? new Thickness(14, 8, 14, 8) : new Thickness(18, 15, 18, 15);
        ScopeCard.Margin = new Thickness(0, 0, 0, compactChart ? 10 : 16);
        // Expanded summary already contains the totals. Avoid duplicate cards on short windows.
        Metrics.Tag = compact;
        SummaryScroll.MaxHeight = compact ? 64 : 140;
        Metrics.Margin = new Thickness(0, 0, 0, compact ? 10 : 18);
        PageHeader.Margin = new Thickness(0, 0, 0, compact ? 12 : 20);
        foreach (Border card in Metrics.Children.OfType<Border>())
        {
            card.Padding = compact ? new Thickness(14, 10, 14, 10) : new Thickness(18, 15, 18, 15);
            var labels = ((StackPanel)card.Child).Children.OfType<TextBlock>().ToArray();
            labels[1].FontSize = compact ? 23 : 28;
            labels[2].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
    }
    void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && Model.CanSearch) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.F6) { ShowDetails(this, new RoutedEventArgs()); e.Handled = true; }
    }
    // Claude 1.7.2: Alt+Up and Esc run after the focused control (bubbling KeyDown), so an open
    // drop-down closes on Esc first. With Alt held WPF reports Key.System; the key is in SystemKey.
    // Handled is set before awaiting: routing has finished by the time an await resumes.
    async void WindowKeyDownAfterControls(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey == Key.Up && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows)) == 0 && Model.CanGoUp)
        { e.Handled = true; await Model.GoUpAsync(); }
        else if (e.Key == Key.Escape && Model.Search.Length > 0) { Model.Search = ""; e.Handled = true; }
    }
    async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        if (closing) { e.Cancel = true; return; }
        if (Model.IsBusy || exporting || Model.Inventory.IsBusy)
        {
            e.Cancel = true; closing = true; IsEnabled = false;
            Model.CancelScan(); Model.Inventory.Forget(); await Model.CurrentScan; await Model.Inventory.CurrentOperation;
            try { await exportTask; } catch (Exception) { /* The export handler reports its error. */ }
            Model.Dispose(); allowClose = true; Close();
        }
        else { allowClose = true; Model.Dispose(); }
    }
}

