using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace YerelDisk.Wpf;

public partial class ReferenceLibraryWindow : Window
{
    readonly ReferenceLibrary library;
    internal bool Changed { get; private set; }
    internal string? SelectedPath { get; private set; }

    internal ReferenceLibraryWindow(ReferenceLibrary library)
    {
        this.library = library;
        InitializeComponent();
        LibraryFeedback.Text = library.LoadWarning ?? "";
        RememberOption.IsChecked = library.Remember;
        RefreshFolder();
        RefreshList();
    }

    void RefreshList()
    {
        References.ItemsSource = library.Recent.ToArray();
        EmptyLibrary.Visibility = library.Recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        References.SelectedIndex = library.Recent.Count > 0 ? 0 : -1;
        UpdateActions();
    }

    void RefreshFolder()
    {
        FolderLabel.Text = library.DefaultFolder is { } folder ? $"Varsayılan kayıt klasörü · {folder}" : "Varsayılan kayıt klasörü seçilmedi.";
        ClearFolderButton.IsEnabled = library.DefaultFolder != null;
    }

    void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Referanslar için yerel klasör seçin", InitialDirectory = library.PickerDirectory() };
        if (picker.ShowDialog(this) != true) return;
        try { UpdateFolder(picker.FolderName); }
        catch (Exception ex) when (DiskScanner.IsFileError(ex) || ex is ArgumentException or NotSupportedException)
        { LibraryFeedback.Text = "Klasör kullanılamıyor. Erişilebilir, yerel bir klasör seçin."; }
    }

    void ClearFolder(object sender, RoutedEventArgs e) => UpdateFolder(null);

    internal void UpdateFolder(string? path)
    {
        library.SetDefaultFolder(path);
        Changed = true;
        LibraryFeedback.Text = "";
        RefreshFolder();
    }

    void RememberChanged(object sender, RoutedEventArgs e)
    {
        library.SetRemember(RememberOption.IsChecked == true);
        Changed = true;
    }

    void ReferenceSelected(object sender, SelectionChangedEventArgs e) => UpdateActions();

    void UpdateActions()
        => CompareButton.IsEnabled = RemoveButton.IsEnabled = References.SelectedItem is RecentReference;

    void Compare(object sender, RoutedEventArgs e)
    {
        if (References.SelectedItem is not RecentReference reference) return;
        SelectedPath = reference.Path;
        DialogResult = true;
    }

    void Remove(object sender, RoutedEventArgs e)
    {
        if (References.SelectedItem is not RecentReference reference) return;
        library.Remove(reference.Path);
        Changed = true;
        RefreshList();
    }
}
