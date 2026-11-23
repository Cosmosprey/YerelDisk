using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YerelDisk;

internal sealed record RecentReference(string Path, DateTimeOffset MeasuredAt, long FileBytes)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string DateText => MeasuredAt.ToLocalTime().ToString("g");
    public string SizeText => PresentationText.Size(FileBytes);
}

internal sealed class ReferenceLibrary(string? storagePath = null)
{
    const int Limit = 10;
    const int MaxStoreBytes = 3 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new() { MaxDepth = 6, IgnoreReadOnlyProperties = true };
    readonly List<RecentReference> recent = [];
    // Claude 1.21.1: entries this session took off the list; a merge must not bring them back.
    readonly HashSet<string> removed = new(StringComparer.OrdinalIgnoreCase);
    Guid consent;
    bool consentChanged;

    internal IReadOnlyList<RecentReference> Recent => recent;
    internal string? DefaultFolder { get; private set; }
    internal bool Remember { get; private set; }
    internal string? LoadWarning { get; private set; }

    internal static ReferenceLibrary LoadDefault()
    {
        string directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Load(directory.Length == 0 ? null : Path.Combine(directory, "YerelDisk-referanslar.json"));
    }

    internal static ReferenceLibrary Load(string? path)
    {
        var library = new ReferenceLibrary(path);
        if (path == null) return library;
        try
        {
            string store = ValidatedStore(path);
            // Claude 1.21.1: read under the writers' lock so a concurrent replace in another
            // instance does not make this session start empty with remembering switched off.
            var saved = WithStoreLock(store, () => ReadSaved(store), required: false);
            if (saved == null) return library;
            library.consent = saved.Consent;
            library.Remember = true;
            library.DefaultFolder = IsCanonicalLocal(saved.Folder) ? saved.Folder : null;
            library.AddSaved(saved.Recent);
        }
        catch (Exception ex) when (IsStoreError(ex))
        { library.LoadWarning = "Kayıtlı referans tercihleri okunamadı. Bu oturum boş bir listeyle başladı."; }
        return library;
    }

    internal void Record(string path, DateTimeOffset measuredAt, long fileBytes)
    {
        path = Path.GetFullPath(path);
        recent.RemoveAll(item => SamePath(item.Path, path));
        removed.Remove(path);
        recent.Insert(0, new(path, measuredAt, fileBytes));
        if (recent.Count > Limit) recent.RemoveAt(Limit);
    }

    internal void Remove(string path)
    {
        recent.RemoveAll(item => SamePath(item.Path, path));
        removed.Add(path);
    }

    // Claude 1.21.1: Windows paths are case-insensitive; "A.yereldisk" and "a.yereldisk" are one file.
    static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    void AddSaved(RecentReference?[]? entries)
    {
        foreach (var entry in entries ?? [])
        {
            if (recent.Count == Limit) break;
            if (entry == null || !IsCanonicalLocal(entry.Path) || entry.MeasuredAt == default || entry.FileBytes < 0
                || removed.Contains(entry.Path) || recent.Any(item => SamePath(item.Path, entry.Path))) continue;
            recent.Add(entry);
        }
    }

    internal void SetDefaultFolder(string? folder)
        => DefaultFolder = folder == null ? null : DiskScanner.ValidateRoot(folder);

    internal string PickerDirectory()
    {
        if (DefaultFolder == null) return "";
        try { return DiskScanner.ValidateRoot(DefaultFolder); }
        catch (Exception ex) when (IsStoreError(ex)) { return ""; }
    }

    internal void SetRemember(bool remember)
    {
        if (Remember == remember) return;
        Remember = remember;
        consentChanged = true;
    }

    internal string? Persist()
    {
        if (!Remember && !consentChanged) return null;
        try
        {
            string path = ValidatedStore(storagePath);
            // Serialize preference changes across application instances, including opt-out.
            return WithStoreLock(path, () =>
            {
                var saved = consentChanged ? null : ReadSaved(path);
                if (!consentChanged && saved?.Consent != consent)
                {
                    Remember = false;
                    return "Bu bilgisayardaki hatırlama ayarı başka bir oturumda değişti. Bu oturumun listesi bellekte kalacak.";
                }
                if (Remember)
                {
                    // Claude 1.21.1: keep references another open instance remembered meanwhile;
                    // the last writer used to replace the whole list with its own.
                    if (saved != null) AddSaved(saved.Recent);
                    Guid nextConsent = consentChanged ? Guid.NewGuid() : consent;
                    WriteSaved(path, new(1, nextConsent, DefaultFolder, recent.ToArray()));
                    consent = nextConsent;
                    removed.Clear();
                }
                else if (CheckStoreFile(path)) File.Delete(path);
                consentChanged = false;
                LoadWarning = null;
                return (string?)null;
            }, required: true);
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            return Remember
                ? "Referans tercihleri diske kaydedilemedi. Bu oturumdaki listeyi kullanabilirsin."
                : "Önceden kaydedilmiş referans tercihleri silinemedi.";
        }
    }

    static T WithStoreLock<T>(string path, Func<T> action, bool required)
    {
        string name = "YerelDisk-References-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var mutex = new Mutex(false, name);
        bool acquired;
        try { acquired = mutex.WaitOne(500); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired && required) throw new IOException();
        try { return action(); }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    static string ValidatedStore(string? path)
    {
        if (!IsCanonicalLocal(path)) throw new InvalidDataException();
        DiskScanner.ValidateRoot(Path.GetDirectoryName(path!)!);
        return path!;
    }

    static bool IsCanonicalLocal(string? path) => FolderMeasurements.IsLocalPath(path)
        && Path.GetFullPath(path!) == path;

    static bool CheckStoreFile(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
            throw new InvalidDataException();
        return true;
    }

    static SavedLibrary? ReadSaved(string path)
    {
        if (!CheckStoreFile(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > MaxStoreBytes) throw new InvalidDataException();
        var saved = JsonSerializer.Deserialize<SavedLibrary>(file, Json);
        return saved is { Version: 1 } && saved.Consent != Guid.Empty ? saved : throw new InvalidDataException();
    }

    static void WriteSaved(string path, SavedLibrary saved)
    {
        CheckStoreFile(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".yereldisk-referanslar-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                JsonSerializer.Serialize(file, saved, Json);
                if (file.Length > MaxStoreBytes) throw new InvalidDataException();
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            created = false;
        }
        finally { if (created) File.Delete(temporary); }
    }

    static bool IsStoreError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException
        or NotSupportedException or System.Security.SecurityException or JsonException or InvalidDataException;

    sealed record SavedLibrary(int Version, Guid Consent, string? Folder, RecentReference?[]? Recent);
}
