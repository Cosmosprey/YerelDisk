using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace YerelDisk.Wpf;

internal sealed class RootsWindow : Window
{
    internal readonly ListBox IncludedList = new() { SelectionMode = SelectionMode.Extended };
    internal readonly ListBox ExcludedList = new() { SelectionMode = SelectionMode.Extended };
    internal readonly TextBox PathInput = new() { MinWidth = 120 };
    internal readonly Button IncludedTab = new(), ExcludedTab = new();
    internal readonly Button AddPathButton = new() { Content = "Yolu ekle", Margin = new Thickness(8, 0, 0, 0) };
    internal readonly Button RemoveButton = new() { Content = "Seçileni çıkar", Margin = new Thickness(8, 0, 0, 0) };
    internal readonly Button AcceptButton = new() { Content = "Bu kapsamı kullan", HorizontalAlignment = HorizontalAlignment.Right };
    internal readonly TextBlock Note = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) };
    readonly Func<IEnumerable<string>, string[]> normalizeRoots;
    bool editingExclusions;
    internal string[] Paths => IncludedList.Items.Cast<string>().ToArray();
    internal string[] Exclusions => ExcludedList.Items.Cast<string>().ToArray();
    ListBox ActiveList => editingExclusions ? ExcludedList : IncludedList;

    internal RootsWindow(IEnumerable<string> roots, Func<IEnumerable<string>, string[]> normalizeRoots, IEnumerable<string>? excluded = null)
    {
        this.normalizeRoots = normalizeRoots;
        Title = "Tarama kapsamı"; Width = 760; Height = 600; MinWidth = 540; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Canvas"); SetResourceReference(ForegroundProperty, "Ink"); FontFamily = new FontFamily("Segoe UI");
        foreach (string root in roots) IncludedList.Items.Add(root);
        foreach (string path in excluded ?? []) ExcludedList.Items.Add(path);
        var panel = new DockPanel { Margin = new Thickness(24) }; Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Nereyi tarayalım?", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        var tabs = new WrapPanel(); IncludedTab.Margin = new Thickness(0, 0, 8, 0);
        tabs.Children.Add(IncludedTab); tabs.Children.Add(ExcludedTab); top.Children.Add(tabs); top.Children.Add(description);
        IncludedTab.Click += (_, _) => SelectExclusions(false); ExcludedTab.Click += (_, _) => SelectExclusions(true);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        var add = new Button { Content = "Klasör ekle · çoklu seçim" };
        add.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Multiselect = true, Title = editingExclusions ? "Hariç tutulacak alt klasörleri seçin" : "Taranacak klasörleri seçin" };
            if (picker.ShowDialog(this) == true) foreach (string path in picker.FolderNames) Add(path);
        };
        RemoveButton.Click += (_, _) => { foreach (object item in ActiveList.SelectedItems.Cast<object>().ToArray()) ActiveList.Items.Remove(item); UpdateCounts(); };
        var controls = new WrapPanel { Margin = new Thickness(0, 12, 0, 10) }; controls.Children.Add(add); controls.Children.Add(RemoveButton); bottom.Children.Add(controls);
        var manual = new DockPanel(); DockPanel.SetDock(AddPathButton, Dock.Right); manual.Children.Add(AddPathButton); manual.Children.Add(PathInput); bottom.Children.Add(manual);
        System.Windows.Automation.AutomationProperties.SetName(PathInput, "Tam yerel klasör yolu");
        AddPathButton.Click += (_, _) => { Add(PathInput.Text); PathInput.Clear(); };
        PathInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Add(PathInput.Text); PathInput.Clear(); e.Handled = true; } };
        bottom.Children.Add(new ScrollViewer { Content = Note, MaxHeight = 85, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 10) });
        AcceptButton.Click += (_, _) => { if (ValidateScope()) DialogResult = true; };
        bottom.Children.Add(AcceptButton);
        var lists = new Grid(); lists.Children.Add(IncludedList); lists.Children.Add(ExcludedList); panel.Children.Add(lists);
        ((System.Collections.Specialized.INotifyCollectionChanged)IncludedList.Items).CollectionChanged += (_, _) => UpdateCounts(); ((System.Collections.Specialized.INotifyCollectionChanged)ExcludedList.Items).CollectionChanged += (_, _) => UpdateCounts();
        UpdateCounts(); SelectExclusions(false);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        WindowPlacement.Attach(this);
    }
    internal void SelectExclusions(bool selected)
    {
        editingExclusions = selected;
        IncludedList.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        ExcludedList.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        IncludedTab.SetResourceReference(BackgroundProperty, selected ? "Surface" : "Selection");
        ExcludedTab.SetResourceReference(BackgroundProperty, selected ? "Selection" : "Surface");
        description.Text = selected
            ? "Bu yollar ve altları taranmaz. Önce taranacak kökü ekleyin; ardından örneğin C:\\Windows'u hariç tutun. Seçili kökün tamamı hariç tutulamaz."
            : "Birden fazla yerel klasör seçebilirsiniz. Üst klasör altlarını zaten kapsar. İstemediğiniz alt klasörleri Hariç tutulanlar bölümüne ekleyin.";
        Note.Text = "Bu tercihler yalnız oturumluk. Hariç tutulan yolların varlığı / boyutu ölçülmez; sonuç ve CSV'de kapsam dışında gösterilir. Ağ ve klasör bağlantıları izlenmez.";
    }
    void UpdateCounts()
    {
        IncludedTab.Content = $"Taranacaklar · {IncludedList.Items.Count}";
        ExcludedTab.Content = $"Hariç tutulanlar · {ExcludedList.Items.Count}";
    }
    internal bool ValidateScope()
    {
        try
        {
            string[] normalized = normalizeRoots(Paths);
            string[] excluded = ScanExclusions.Create(normalized, Exclusions).Paths.ToArray();
            IncludedList.Items.Clear(); foreach (string root in normalized) IncludedList.Items.Add(root);
            ExcludedList.Items.Clear(); foreach (string path in excluded) ExcludedList.Items.Add(path);
            return true;
        }
        catch (Exception ex) { Note.Text = ex.Message; return false; }
    }
    void Add(string path)
    {
        path = path.Trim().Trim('"'); if (path.Length == 0) return;
        if (!ActiveList.Items.Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase)) ActiveList.Items.Add(path);
    }
}
