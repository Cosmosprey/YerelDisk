using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace YerelDisk.Wpf;

public partial class ProfilesWindow : Window
{
    readonly ScanProfiles library;
    readonly string[] roots, excluded;
    bool busy, updating;
    internal ScanProfile? SelectedProfile { get; private set; }
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;
    internal ProfilesWindow(ScanProfiles library, IEnumerable<string> roots, IEnumerable<string> excluded)
    {
        this.library = library; this.roots = roots.ToArray(); this.excluded = excluded.ToArray();
        InitializeComponent();
        CurrentScope.Text = $"Ekle / Güncelle ile kaydedilecek mevcut seçim: {this.roots.Length} kök · {this.excluded.Length} hariç yol";
        CurrentScope.ToolTip = ScopeDescription(this.roots, this.excluded);
        Feedback.Text = library.LoadWarning ?? "Ekle ve Güncelle, klasör seçim ekranındaki mevcut kapsamı kaydeder.";
        RefreshList();
        WindowPlacement.Attach(this);
    }
    void RefreshList(Guid? selected = null)
    {
        updating = true;
        try
        {
            RememberOption.IsChecked = library.Remember;
            ProfileList.ItemsSource = library.Profiles.ToArray();
            Guid? target = library.Conflict?.Id ?? selected;
            ProfileList.SelectedItem = library.Profiles.FirstOrDefault(p => p.Id == target) ?? library.Profiles.FirstOrDefault();
        }
        finally { updating = false; }
        ShowSelection(); UpdateActions();
    }
    void ProfileSelected(object sender, SelectionChangedEventArgs e) { if (!updating) { ShowSelection(); UpdateActions(); } }
    void ShowSelection()
    {
        if (library.Conflict is { } conflict)
        {
            SelectedScope.Text = "Oturumdaki düzenleme\n" + (conflict.Local == null ? "Profilin kaldırılması istendi." : ScopeDescription(conflict.Local.Roots, conflict.Local.Exclusions))
                + "\n\nDiskteki sürüm\n" + (conflict.Saved == null ? "Bu profil silinmiş." : conflict.Saved.Name + "\n" + ScopeDescription(conflict.Saved.Roots, conflict.Saved.Exclusions));
            CurrentScope.Text = conflict.Local == null ? "Kaldırma isteği çakıştı; diskteki sürümü kullanarak devam edin." : "Ayrı kayıt, çakışan oturum düzenlemesini kullanır. Aşağıdan kopyanın adını değiştirebilirsiniz.";
            CurrentScope.ToolTip = SelectedScope.Text;
            NameInput.Text = conflict.Local == null ? conflict.Name : library.SuggestedCopyName();
            return;
        }
        CurrentScope.Text = $"Ekle / Güncelle ile kaydedilecek mevcut seçim: {roots.Length} kök · {excluded.Length} hariç yol";
        CurrentScope.ToolTip = ScopeDescription(roots, excluded);
        var profile = ProfileList.SelectedItem as ScanProfile;
        SelectedScope.Text = profile == null ? "Henüz profil yok. Mevcut kapsamı bir adla ekleyin." : ScopeDescription(profile.Roots, profile.Exclusions);
        if (profile != null) NameInput.Text = profile.Name;
    }
    static string ScopeDescription(IEnumerable<string> roots, IEnumerable<string> excluded)
        => "Taranacaklar\n" + string.Join("\n", roots) + "\n\nHariç tutulanlar\n" + (excluded.Any() ? string.Join("\n", excluded) : "Yok");
    void UpdateActions()
    {
        bool conflict = library.Conflict != null;
        EditActions.IsEnabled = RememberOption.IsEnabled = !busy;
        ProfileList.IsEnabled = !busy && !conflict;
        NameInput.IsEnabled = !busy && (!conflict || library.Conflict?.Local != null);
        NormalActions.Visibility = conflict ? Visibility.Collapsed : Visibility.Visible;
        ConflictActions.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;
        CopyConflictButton.IsEnabled = !busy && library.Conflict?.Local != null;
        UseSavedButton.IsEnabled = !busy && conflict;
        SaveButton.IsEnabled = roots.Length > 0;
        UpdateButton.IsEnabled = roots.Length > 0 && ProfileList.SelectedItem is ScanProfile;
        RemoveButton.IsEnabled = ProfileList.SelectedItem is ScanProfile;
        UseButton.IsEnabled = !busy && !conflict && ProfileList.SelectedItem is ScanProfile;
    }
    async void SaveNew(object sender, RoutedEventArgs e) => await SaveAsync(false);
    async void UpdateSelected(object sender, RoutedEventArgs e) => await SaveAsync(true);
    internal Task SaveAsync(bool update)
    {
        if (busy) return PendingOperation;
        if (library.Conflict != null) return Task.CompletedTask;
        Guid? id = update ? (ProfileList.SelectedItem as ScanProfile)?.Id : null;
        if (update && id == null) return Task.CompletedTask;
        return PendingOperation = MutateAsync(() => library.Save(NameInput.Text, roots, excluded, id).Id,
            library.Remember ? "Profil yerel kayda eklendi." : "Profil bu oturumda kaydedildi.");
    }
    async void RemoveSelected(object sender, RoutedEventArgs e)
    {
        if (busy || library.Conflict != null || ProfileList.SelectedItem is not ScanProfile profile) return;
        PendingOperation = MutateAsync(() => { library.Remove(profile.Id); return null; }, "Profil kaldırıldı; klasörlere dokunulmadı.");
        await PendingOperation;
    }
    async void RememberChanged(object sender, RoutedEventArgs e)
    {
        if (updating || busy) return;
        bool remember = RememberOption.IsChecked == true;
        PendingOperation = MutateAsync(() => { library.SetRemember(remember); return (ProfileList.SelectedItem as ScanProfile)?.Id; },
            remember ? "Profiller bu bilgisayarda hatırlanıyor." : "Liste bu oturumda kaldı; kalıcı kayıt kapalı.");
        await PendingOperation;
    }
    async void CopyConflict(object sender, RoutedEventArgs e)
    {
        if (busy || library.Conflict?.Local == null) return;
        PendingOperation = MutateAsync(() => library.CopyConflict(NameInput.Text).Id, "Düzenleme ayrı profil olarak kaydedildi; diskteki diğer sürüm korundu.");
        await PendingOperation;
    }
    async void UseSavedConflict(object sender, RoutedEventArgs e)
    {
        if (busy || library.Conflict == null) return;
        PendingOperation = MutateAsync(() => { library.UseSavedConflict(); return null; }, "Diskteki durum yüklendi; çakışan oturum düzenlemesi bırakıldı.");
        await PendingOperation;
    }
    async Task MutateAsync(Func<Guid?> action, string success)
    {
        if (busy) return;
        busy = true; UpdateActions();
        try
        {
            Guid? selection = action();
            string? warning = await Task.Run(library.Persist);
            Feedback.Text = warning ?? success;
            Feedback.FontWeight = library.Conflict == null ? FontWeights.Normal : FontWeights.SemiBold;
            RefreshList(selection);
        }
        catch (ArgumentException ex) { Feedback.Text = ex.Message; }
        finally { busy = false; UpdateActions(); }
    }
    void UseSelected(object sender, RoutedEventArgs e)
    {
        if (busy || library.Conflict != null || ProfileList.SelectedItem is not ScanProfile profile) return;
        SelectedProfile = profile; Close();
    }
    void WindowClosing(object? sender, CancelEventArgs e)
    { if (busy) { e.Cancel = true; Feedback.Text = "Profil tercihi uygulanıyor. Bitince pencereyi kapatabilirsiniz."; } }
}
