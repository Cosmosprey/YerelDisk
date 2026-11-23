using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YerelDisk;

internal sealed class ScanProfile
{
    internal Guid Id { get; }
    public string Name { get; }
    internal IReadOnlyList<string> Roots { get; }
    internal IReadOnlyList<string> Exclusions { get; }
    public string ScopeText => $"{Roots.Count} kök · {Exclusions.Count} hariç yol";
    ScanProfile(Guid id, string name, string[] roots, string[] exclusions)
    { Id = id; Name = name; Roots = Array.AsReadOnly(roots); Exclusions = Array.AsReadOnly(exclusions); }

    internal static ScanProfile Create(Guid id, string name, IEnumerable<string> roots, IEnumerable<string> exclusions)
    {
        name = name.Trim();
        if (id == Guid.Empty || name.Length is 0 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException("Profil adı 1–80 karakter olmalı ve kontrol karakteri içermemeli.");
        string[] included = roots.Take(65).ToArray(), excluded = exclusions.Take(129).ToArray();
        if (included.Length is 0 or > 64 || excluded.Length > 128)
            throw new ArgumentException("Bir profilde 1–64 kök ve en fazla 128 hariç yol olabilir.");
        foreach (string path in included.Concat(excluded))
            if (!FolderMeasurements.IsLocalPath(path) || path.Any(char.IsControl)
                || path.AsSpan().ContainsAny('*', '?', '"') || path.AsSpan().ContainsAny('<', '>', '|')
                || path.Split('\\').Skip(1).Any(part => part.EndsWith('.') || part.EndsWith(' ')))
                throw new ArgumentException("Profillerde tam yerel klasör yolları kullanın; ağ, joker veya sonu nokta/boşluk olan yollar desteklenmez.");
        // Lexical only: disconnected or missing roots are checked by the normal
        // scope acceptance and scan pipeline, never by loading a profile.
        included = MultiScan.NormalizeRootPaths(included);
        excluded = ScanExclusions.Create(included, excluded).Paths.ToArray();
        return new(id, name, included, excluded);
    }
}

// Session library; persistence only after an explicit remember choice. Merges
// explicit ID-based edits so another window's additions/deletions are preserved.
internal sealed class ScanProfiles(string? storagePath = null)
{
    internal sealed record ProfileConflict(Guid Id, ScanProfile? Original, ScanProfile? Local, ScanProfile? Saved)
    {
        internal string Name => Local?.Name ?? Original?.Name ?? Saved?.Name ?? "Profil";
        internal string Message => Local == null
            ? $"{Name} başka bir pencerede değiştirilmiş. Kaldırma isteği uygulanmadı. Diskteki sürümü kullanıp gerekiyorsa tekrar kaldırın."
            : Saved == null
            ? $"{Name} başka bir pencerede silinmiş. Değişiklik diske yazılmadı; oturumdaki düzenleme korunuyor. Ayrı profil kaydedin veya diskteki durumu kullanın."
            : $"{Name} başka bir pencerede değiştirilmiş. Değişiklik diske yazılmadı; oturumdaki düzenleme korunuyor. Ayrı profil kaydedin veya diskteki sürümü kullanın.";
    }
    internal const int Limit = 20, MaxStoreBytes = 1024 * 1024;
    static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 7, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false
    };
    readonly List<ScanProfile> profiles = [];
    readonly Dictionary<Guid, ScanProfile?> changes = [];
    readonly Dictionary<Guid, ScanProfile> baseline = [];
    Guid consent;
    bool consentChanged;
    internal IReadOnlyList<ScanProfile> Profiles => profiles.AsReadOnly();
    internal bool Remember { get; private set; }
    internal string? LoadWarning { get; private set; }
    internal ProfileConflict? Conflict { get; private set; }

    internal static ScanProfiles LoadDefault()
    {
        string directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Load(directory.Length == 0 ? null : Path.Combine(directory, "YerelDisk-profiller.json"));
    }
    internal static ScanProfiles Load(string? path)
    {
        var library = new ScanProfiles(path);
        if (path == null) return library;
        try
        {
            string store = StorePath(path);
            var saved = Locked(store, () => ReadSaved(store));
            if (saved == null) return library;
            library.profiles.AddRange(Decode(saved));
            library.Rebase();
            library.consent = saved.Consent; library.Remember = true;
        }
        catch (Exception ex) when (StoreError(ex))
        { library.LoadWarning = "Kayıtlı profiller okunamadı. Dosya değiştirilmedi; bu oturumda yeni profiller oluşturabilirsiniz."; }
        return library;
    }
    internal ScanProfile Save(string name, IEnumerable<string> roots, IEnumerable<string> exclusions, Guid? updateId = null)
    {
        if (updateId.HasValue && profiles.All(p => p.Id != updateId.Value)) throw new ArgumentException("Güncellenecek profil bulunamadı.");
        var item = ScanProfile.Create(updateId ?? Guid.NewGuid(), name, roots, exclusions);
        if (profiles.Any(p => p.Id != item.Id && string.Equals(p.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Bu adla bir profil var. Başka bir ad seçin veya o profili güncelleyin.");
        if (!updateId.HasValue && profiles.Count >= Limit) throw new ArgumentException("En fazla 20 profil saklanabilir. Önce bir profili kaldırın.");
        int index = profiles.FindIndex(p => p.Id == item.Id);
        if (index < 0) profiles.Add(item); else profiles[index] = item;
        changes[item.Id] = item; return item;
    }
    internal void Remove(Guid id)
    { if (profiles.RemoveAll(p => p.Id == id) != 0) changes[id] = null; }
    internal void SetRemember(bool remember)
    { if (Remember != remember) { Remember = remember; consentChanged = true; Conflict = null; } }

    // Explicit conflict resolution changes session state only. Persist re-reads
    // the current store afterwards; the conflict snapshot never replaces it.
    internal void UseSavedConflict()
    {
        var conflict = Conflict ?? throw new ArgumentException("Çözümlenecek profil çakışması yok.");
        AcceptSaved(conflict); Conflict = null;
    }
    internal ScanProfile CopyConflict(string name)
    {
        var conflict = Conflict ?? throw new ArgumentException("Çözümlenecek profil çakışması yok.");
        if (conflict.Local == null) throw new ArgumentException("Kaldırma işlemi ayrı profil olarak kopyalanamaz. Diskteki sürümü kullanın.");
        var copy = ScanProfile.Create(Guid.NewGuid(), name, conflict.Local.Roots, conflict.Local.Exclusions);
        var remaining = profiles.Where(p => p.Id != conflict.Id).ToList();
        if (conflict.Saved != null) remaining.Add(conflict.Saved);
        if (remaining.Count >= Limit) throw new ArgumentException("Ayrı profil için listede yer yok. Diskteki durumu kullanıp bir profili kaldırdıktan sonra mevcut kapsamı yeni profil olarak ekleyebilirsiniz.");
        if (remaining.Any(p => string.Equals(p.Name, copy.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Ayrı profil için farklı bir ad seçin.");
        AcceptSaved(conflict);
        profiles.Add(copy); changes[copy.Id] = copy; Conflict = null;
        return copy;
    }
    internal string SuggestedCopyName()
    {
        string name = Conflict?.Local?.Name ?? "Profil";
        if (name.Length > 60) name = name[..60];
        if (name.Length > 0 && char.IsHighSurrogate(name[^1])) name = name[..^1];
        for (int i = 1; ; i++)
        {
            string candidate = name + (i == 1 ? " (kopya)" : $" (kopya {i})");
            if (profiles.All(p => !string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase))
                && !string.Equals(Conflict?.Saved?.Name, candidate, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
    }
    void AcceptSaved(ProfileConflict conflict)
    {
        profiles.RemoveAll(p => p.Id == conflict.Id); changes.Remove(conflict.Id); baseline.Remove(conflict.Id);
        if (conflict.Saved != null) { profiles.Add(conflict.Saved); baseline[conflict.Id] = conflict.Saved; }
    }
    void Rebase() { baseline.Clear(); foreach (var profile in profiles) baseline.Add(profile.Id, profile); }
    static bool Same(ScanProfile? a, ScanProfile? b) => a != null && b != null && a.Id == b.Id
        && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && a.Roots.SequenceEqual(b.Roots, StringComparer.Ordinal)
        && a.Exclusions.SequenceEqual(b.Exclusions, StringComparer.Ordinal);

    internal string? Persist()
    {
        if (!Remember && !consentChanged) return null;
        try
        {
            string path = StorePath(storagePath);
            return Locked(path, () =>
            {
                var saved = ReadSaved(path);
                Conflict = null;
                if (!consentChanged && saved?.Consent != consent)
                {
                    Remember = false;
                    return "Hatırlama başka bir pencerede değişti. Bu oturumdaki profiller bellekte kaldı; diskteki kayıt yeniden oluşturulmadı.";
                }
                if (!Remember)
                {
                    if (saved != null) File.Delete(path); // Only this application's explicit opt-out preference file.
                    consent = Guid.Empty; consentChanged = false; return (string?)null;
                }
                bool freshConsent = consentChanged && saved == null;
                var merged = freshConsent ? profiles.ToList() : saved == null ? new List<ScanProfile>() : Decode(saved).ToList();
                // Compare every pending edit before applying any of them. A
                // conflicting transaction leaves the entire persisted file intact.
                if (!freshConsent)
                    foreach (var change in changes)
                    {
                        baseline.TryGetValue(change.Key, out var expected);
                        var current = merged.FirstOrDefault(p => p.Id == change.Key);
                        bool valid = expected == null
                            ? change.Value == null || current == null || Same(current, change.Value)
                            : change.Value == null ? current == null || Same(current, expected)
                            : current != null && (Same(current, expected) || Same(current, change.Value));
                        if (!valid)
                        {
                            Conflict = new(change.Key, expected, change.Value, current);
                            return Conflict.Message;
                        }
                    }
                foreach (var change in changes)
                {
                    if (change.Value == null && !baseline.ContainsKey(change.Key)) continue;
                    merged.RemoveAll(p => p.Id == change.Key);
                    if (change.Value != null) merged.Add(change.Value);
                }
                if (merged.Count > Limit || merged.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != merged.Count)
                    return "Başka bir pencereyle profil adı veya sayı sınırı çakıştı. Oturumdaki profiller korundu; diske yazılmadı. Adı değiştirin veya bir profili kaldırıp yeniden deneyin.";
                Guid next = consentChanged ? Guid.NewGuid() : consent;
                if (consentChanged || changes.Count > 0)
                    WriteSaved(path, new(1, next, merged.Select(Encode).ToArray()));
                profiles.Clear(); profiles.AddRange(merged); changes.Clear();
                Rebase();
                consent = next; consentChanged = false; LoadWarning = null;
                return (string?)null;
            });
        }
        catch (Exception ex) when (StoreError(ex))
        { return Remember ? "Profiller diske kaydedilemedi; bu oturumdaki liste korunuyor." : "Kayıtlı profil dosyası kaldırılamadı; hatırlama tercihini yeniden deneyin."; }
    }
    static ScopeRecord Encode(ScanProfile p) => new(p.Id, p.Name, p.Roots.ToArray(), p.Exclusions.ToArray());
    static ScanProfile[] Decode(SavedProfiles saved)
    {
        if (saved.Version != 1 || saved.Consent == Guid.Empty || saved.Profiles == null || saved.Profiles.Length > Limit) throw new InvalidDataException();
        var result = saved.Profiles.Select(p => p == null || p.Roots == null || p.Exclusions == null || p.Name == null
            ? throw new InvalidDataException() : ScanProfile.Create(p.Id, p.Name, p.Roots, p.Exclusions)).ToArray();
        if (result.Select(p => p.Id).Distinct().Count() != result.Length
            || result.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length) throw new InvalidDataException();
        return result;
    }
    static string StorePath(string? path)
    {
        if (!FolderMeasurements.IsLocalPath(path) || Path.GetFullPath(path!) != path) throw new InvalidDataException();
        DiskScanner.ValidateRoot(Path.GetDirectoryName(path!)!); return path!;
    }
    static T Locked<T>(string path, Func<T> action)
    {
        string name = "YerelDisk-Profiles-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var mutex = new Mutex(false, name); bool acquired;
        try { acquired = mutex.WaitOne(750); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Profil kaydı başka bir pencerede kullanılıyor.");
        try { return action(); } finally { mutex.ReleaseMutex(); }
    }
    static SavedProfiles? ReadSaved(string path)
    {
        using var directory = NativeDirectory.OpenRoot(Path.GetDirectoryName(path)!, false);
        try
        {
            using var handle = directory.OpenFileForRead(Path.GetFileName(path));
            if ((NativeMetadata.Attributes(handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint | NativeDirectory.CloudFlags)) != 0) throw new InvalidDataException();
            directory.EnsureRootLocation();
            using var stream = new FileStream(handle, FileAccess.Read);
            if (stream.Length is <= 0 or > MaxStoreBytes) throw new InvalidDataException();
            byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException();
            var saved = JsonSerializer.Deserialize<SavedProfiles>(bytes, Json) ?? throw new InvalidDataException();
            Decode(saved); directory.VerifyBinding(Path.GetDirectoryName(path)!); directory.EnsureRootLocation();
            return saved;
        }
        catch (NativePathException ex) when (ex.NativeError is 2 or 3) { return null; }
    }
    static void WriteSaved(string path, SavedProfiles saved)
    {
        using var memory = new MemoryStream();
        using (var capped = new FolderMeasurements.CappedOutput(memory, MaxStoreBytes)) JsonSerializer.Serialize(capped, saved, Json);
        // Validate again before replacement. A failed save never truncates the old file.
        using var directory = NativeDirectory.OpenRoot(Path.GetDirectoryName(path)!, false);
        directory.EnsureRootLocation();
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".yereldisk-profiller-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; memory.Position = 0; memory.CopyTo(file); file.Flush(true); }
            directory.VerifyBinding(Path.GetDirectoryName(path)!); directory.EnsureRootLocation();
            File.Move(temporary, path, overwrite: true); created = false;
        }
        finally { if (created) File.Delete(temporary); }
    }
    static bool StoreError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException
        or NotSupportedException or System.Security.SecurityException or JsonException or InvalidDataException;
    sealed record ScopeRecord(Guid Id, string Name, string[] Roots, string[] Exclusions);
    sealed record SavedProfiles(int Version, Guid Consent, ScopeRecord[] Profiles);
}
