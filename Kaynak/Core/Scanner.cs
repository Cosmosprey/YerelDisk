using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YerelDisk;

internal enum MeasurementState { Measured, Partial, Unknown, Previous }

internal sealed class FolderEntry(string path, FolderEntry? parent)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    public FolderEntry? Parent { get; internal set; } = parent;
    public List<FolderEntry> Children { get; } = [];
    public long LogicalBytes;
    public long AllocatedBytes;
    public long FileCount;
    public int IssueCount;
    public int OwnStreams;
    public long OwnStreamBytes;
    public MeasurementState Measurement;
    public long? LastObservedBytes;
    public int ExcludedSubtrees;
    internal ulong ObservedId;
}

internal sealed record FileEntry(string Path, long Size, long? Allocated, DateTime Modified, bool HardLinked, string Hint,
    int ExtraStreams = 0, long ExtraStreamBytes = 0, bool Unverified = false)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

internal sealed record ScanIssue(string Path, string Reason);
internal enum ScanPhase { InitialScan, Verification, Refresh, Finalizing }
internal sealed record ScanTimings(TimeSpan InitialScan, TimeSpan Verification, TimeSpan Refresh);
internal sealed record ScanProgress(long Files, long Folders, long Bytes, long Issues, string Current, TimeSpan Elapsed,
    ScanPhase Phase = ScanPhase.InitialScan);
internal sealed class ScanResult(FolderEntry root)
{
    public FolderEntry Root { get; } = root;
    public List<FileEntry> Files { get; set; } = [];
    public List<ScanIssue> Issues { get; } = [];
    public long Folders;
    public long IssueCount;
    public long UnknownAllocation;
    public long HardLinkAliases;
    public bool Cancelled;
    public bool ScopeChanged;
    public TimeSpan Elapsed;
    internal ScanTimings? Timings;
    public int Workers = 1; // Claude 1.3.0: worker threads used by the scan.
    public int RetainedFileLimit = FileLimit;
    // Claude 1.3.1: stopped early by a root-level error (root not readable, root check failed,
    // or a selected root that could not be scanned): the result is partial.
    public bool Failed;
    public List<string> RootPaths { get; } = [root.Path];
    public IReadOnlyList<string> ExcludedPaths { get; internal set; } = Array.Empty<string>();
    public HashSet<string> UnverifiedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> VerificationHistory { get; } = [];
    public int RetryCount;
    public long RefreshedFolders, VerifiedFolders;
    public int VerificationRounds;
    internal bool NeedsRetry;
    internal Dictionary<(uint Volume, ulong Id), LinkAllocation> PhysicalLinks { get; } = [];
    internal IEnumerable<(FolderEntry Owner, FileObservation File)>? FileCandidates;
    internal ObservedFileEvidence? ObservedEvidence;
    public bool HasUnverified => UnverifiedPaths.Count > 0 || Root.Measurement != MeasurementState.Measured;
    public bool CanUsePath(string? path)
    {
        if (path == null || (ScopeChanged && UnverifiedPaths.Count == 0)) return false;
        for (int i = 0; i < ExcludedPaths.Count; i++)
            if (ScanExclusions.SameOrBelow(path, ExcludedPaths[i])) return false;
        if (UnverifiedPaths.Count == 0) return true;
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current.TrimEnd('\\')))
            if (UnverifiedPaths.Contains(current) || UnverifiedPaths.Contains(current.TrimEnd('\\')) || UnverifiedPaths.Contains(current.TrimEnd('\\') + "\\")) return false;
        return true;
    }
    public const int FileLimit = 100_000;
    public const int IssueLimit = 10_000;
}

// Claude 1.9.1: Size and Linked (Links > 1 when observed) tell a real hard link from a stale
// observation of a file that was moved: one name each time means the earlier name is gone.
internal sealed record LinkAllocation(FolderEntry Owner, long Bytes, string? Name = null, long Size = 0, bool Linked = true);
internal sealed record ScanPolicy((uint Volume, ulong Id) RootIdentity);

internal static class DiskScanner
{
    internal static string NormalizeRootPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Bir sürücü veya klasör seçin.");
        string path = System.IO.Path.GetFullPath(input.Trim().Trim('"'));
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Yalnızca yerel sürücüler destekleniyor. Ağ ve aygıt yolları kabul edilmez.");
        string root = System.IO.Path.GetPathRoot(path) ?? throw new ArgumentException("Geçersiz yol.");
        return path.Length > root.Length ? path.TrimEnd('\\') : root;
    }
    public static string ValidateRoot(string input)
    {
        string path = NormalizeRootPath(input);
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new ArgumentException("Yerel sabit disk veya USB sürücü seçin.");
        // Resolve each component relative to its opened parent; never follow a reparse target.
        try { using var validation = NativeDirectory.OpenRoot(path, false); }
        catch (ScopeChangedException ex) { throw new ArgumentException(ex.Message, ex); }
        return path;
    }

    public static ScanResult Scan(string input, IProgress<ScanProgress>? progress, CancellationToken token,
        int fileLimit = ScanResult.FileLimit)
        => ScanCore(input, progress, token, fileLimit, []);

    // Claude 1.3.0: the traversal moved to ScanRun, which runs it on one or more worker threads.
    // workers: 0 = automatic (VolumeInfo.ScanWorkers); tests pass an explicit count.
    internal static ScanResult ScanCore(string input, IProgress<ScanProgress>? progress, CancellationToken token,
        int fileLimit, HashSet<(uint Volume, ulong Id)> seenLinks, int workers = 0)
    {
        ScanResult result = ScanForMerge(input, progress, token, fileLimit, seenLinks, workers);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        FileHints.Apply(result);
        result.Elapsed += watch.Elapsed;
        return result;
    }

    // The multi-root caller applies descriptions once, after its global retention limit.
    internal static ScanResult ScanForMerge(string input, IProgress<ScanProgress>? progress, CancellationToken token,
        int fileLimit, HashSet<(uint Volume, ulong Id)> seenLinks, int workers)
    {
        if (fileLimit < 1) throw new ArgumentOutOfRangeException(nameof(fileLimit));
        if (workers < 0) throw new ArgumentOutOfRangeException(nameof(workers));
        string rootPath = ValidateRoot(input);
        ScanResult result = new ScanRun(rootPath, progress, token, fileLimit, seenLinks).Execute(workers);
        result.RetainedFileLimit = fileLimit;
        return result;
    }

    internal static bool IsFileError(Exception ex) => ex is UnauthorizedAccessException or IOException or System.Security.SecurityException;
    internal static string FriendlyError(Exception ex) => ex is UnauthorizedAccessException ? "Erişim izni yok; taranmadı." : ex.Message;
}

internal readonly record struct FileMetadata(long Size, long Allocated, uint Links, uint Volume, ulong Id,
    FileAttributes Attributes, int ExtraStreams, long ExtraStreamBytes, bool StreamsKnown, string StreamError);
internal static class NativeMetadata
{
    [StructLayout(LayoutKind.Sequential)]
    struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleInformation
    {
        public uint Attributes;
        public FileTime Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct StandardInformation
    {
        public long AllocationSize, EndOfFile;
        public uint Links;
        public byte DeletePending, Directory;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out StandardInformation info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryBuffer(SafeFileHandle handle, int infoClass, IntPtr buffer, uint size);

    static ByHandleInformation ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("Dosya kimliği okunamadı: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        return info;
    }
    public static FileAttributes Attributes(SafeFileHandle handle) => (FileAttributes)ReadIdentity(handle).Attributes;
    // A failed query cannot justify relaxing a location mismatch; return false (fail closed).
    public static bool IsDeletePending(SafeFileHandle handle) =>
        GetFileInformationByHandleEx(handle, 1, out var info, (uint)Marshal.SizeOf<StandardInformation>()) && info.DeletePending != 0;
    public static (uint Volume, ulong Id) Identity(SafeFileHandle handle)
    {
        var info = ReadIdentity(handle);
        return (info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow);
    }
    public static FileMetadata Read(SafeFileHandle handle, bool namedStreams)
    {
        var identity = ReadIdentity(handle);
        var attributes = (FileAttributes)identity.Attributes;
        if ((attributes & (FileAttributes.ReparsePoint | NativeDirectory.CloudFlags)) != 0)
            throw new ScopeChangedException("Dosya bağlantı / bulut yer tutucusuna dönüştü; içerik izlenmedi.");
        if (!GetFileInformationByHandleEx(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInformation>()))
            throw new IOException("Ayrılan alan okunamadı: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        bool directory = (attributes & FileAttributes.Directory) != 0;
        // Directory index / filesystem metadata is not a data stream. Only named $DATA
        // streams belong in a directory's own-data totals; an empty directory stays zero.
        long logical = directory ? 0 : Math.Max(0, standard.EndOfFile);
        long allocation = directory ? 0 : Math.Max(0, standard.AllocationSize);
        int extra = 0;
        long extraBytes = 0;
        bool streamsKnown = true;
        string streamError = "";
        if (namedStreams)
        {
            // FILE_STREAM_INFO enumerates all $DATA streams on the SAME opened file.
            // No path lookup, alternate-stream open, or content read is required.
            bool obtained = false;
            for (int size = 4096; size <= 16 * 1024 * 1024; size *= 2)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!QueryBuffer(handle, 7, buffer, (uint)size))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error is 122 or 234) continue;
                        if (error == 38 && logical == 0 && allocation == 0) { obtained = true; break; }
                        streamError = new Win32Exception(error).Message;
                        break;
                    }
                    long streamLogical = 0, streamAllocation = 0;
                    int offset = 0;
                    while (true)
                    {
                        if (offset < 0 || offset > size - 24) throw new IOException("Geçersiz akış üstverisi.");
                        IntPtr row = IntPtr.Add(buffer, offset);
                        int next = Marshal.ReadInt32(row, 0), length = Marshal.ReadInt32(row, 4);
                        long bytes = Marshal.ReadInt64(row, 8), allocated = Marshal.ReadInt64(row, 16);
                        if (length < 0 || (length & 1) != 0 || length > size - offset - 24 || bytes < 0 || allocated < 0)
                            throw new IOException("Geçersiz akış boyutu / adı.");
                        string name = Marshal.PtrToStringUni(IntPtr.Add(row, 24), length / 2)!;
                        bool named = !name.Equals("::$DATA", StringComparison.OrdinalIgnoreCase);
                        if (!directory || named)
                        {
                            streamLogical = checked(streamLogical + bytes);
                            streamAllocation = checked(streamAllocation + allocated);
                        }
                        if (named)
                        {
                            extra++;
                            extraBytes = checked(extraBytes + bytes);
                        }
                        if (next == 0) break;
                        if (next < 24 || next > size - offset) throw new IOException("Geçersiz akış kayıt aralığı.");
                        offset += next;
                    }
                    logical = streamLogical;
                    allocation = streamAllocation;
                    obtained = true;
                    break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            if (!obtained)
            {
                streamsKnown = false;
                if (streamError.Length == 0) streamError = "Akış listesi bellek sınırını aştı.";
            }
        }
        return new(logical, allocation, identity.Links, identity.Volume,
            ((ulong)identity.IndexHigh << 32) | identity.IndexLow, attributes, extra, extraBytes, streamsKnown, streamError);
    }
}

internal static class FileHints
{
    internal readonly record struct Parts(string Context, string Type, string Action, string AdditionalAction = "", FileDataRisk DataRisk = FileDataRisk.None);
    // Keys contain only the finite literal descriptions returned by Classify, never paths.
    // Concurrent callers share the canonical string; measurement-specific notes stay per file.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Parts, string> descriptions = new();

    internal static void Apply(ScanResult result)
    {
        for (int i = 0; i < result.Files.Count; i++)
        {
            var file = result.Files[i];
            result.Files[i] = file with { Hint = Describe(file.Path) + file.Hint };
        }
    }

    public static string Describe(string path)
        => descriptions.GetOrAdd(Classify(path), static parts => Compose(parts));

    static string Compose(Parts parts)
    {
        // Combine actions only on a cache miss, not once per file.
        string action = parts.AdditionalAction.Length == 0 ? parts.Action : parts.Action + " " + parts.AdditionalAction;
        if (parts.Context.Length == 0)
            return action.Length == 0 ? parts.Type : parts.Type + " " + action;
        return action.Length == 0 ? parts.Context + " · " + parts.Type
            : $"{parts.Context} · {parts.Type} {action}";
    }

    // Pure path/name inference. Context never erases the type's data-loss warning.
    internal static Parts Classify(string path)
    {
        ReadOnlySpan<char> local = path.AsSpan();
        if (local.StartsWith(@"\\?\", StringComparison.Ordinal)) local = local[4..];
        ReadOnlySpan<char> name = Path.GetFileName(local), ext = Path.GetExtension(local);
        bool rootFile = IsDrivePath(local) && local.LastIndexOf('\\') == 2;
        string context = "", contextAction = "", typeAction = "";
        bool actionFromContext = false, includeTypeAction = false, highRiskType = false;
        FileDataRisk dataRisk = RiskForExtension(ext);
        if (RootFolder(local, "$Recycle.Bin"))
        {
            context = "Geri Dönüşüm Kutusu'nda";
            contextAction = "Kutudan yönetin; geri yükleme ihtiyacını kontrol edin. Kutuyu boşaltmak veriyi kalıcı olarak kaybettirebilir.";
            actionFromContext = true;
        }
        else if (KnownTemp(local))
        {
            context = "Geçici alan adıyla eşleşiyor";
            contextAction = "Açık uygulama veya kurtarma verisi olabilir; geçici adı kullanılmadığını kanıtlamaz.";
            actionFromContext = true;
            includeTypeAction = true;
        }
        else if (local.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase))
        {
            context = "Steam oyun kütüphanesi düzeniyle eşleşiyor";
            contextAction = "Oyunu Steam üzerinden yönetin; kayıt ve ayarlar ayrıca bulunabilir.";
            actionFromContext = true;
            includeTypeAction = true;
        }
        else if (RootFolder(local, "Windows"))
        {
            context = "Windows klasörü altında";
            contextAction = "Windows'un desteklenen yönetim araçlarından inceleyin; elle topluca temizlemeyin.";
        }
        else if (RootFolder(local, "Program Files") || RootFolder(local, "Program Files (x86)"))
        {
            context = "Program Files altında";
            contextAction = "Kurulu uygulamanın parçası olabilir; uygulamanın kendi yönetim aracını kullanın.";
        }
        else if (RootFolder(local, "ProgramData"))
        {
            context = "ProgramData altında";
            contextAction = "Ortak uygulama / kurulum verisi olabilir; ilgili uygulama içinden inceleyin.";
        }
        else if (local.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase))
        {
            context = "AppData altında";
            contextAction = "Önbellek, ayar veya kişisel veri olabilir; uygulama içinden inceleyin.";
        }

        string type;
        if (rootFile && name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase))
        {
            type = "Windows sayfa dosyası adıyla eşleşiyor; sanal bellek ve hata dökümleri için gerekli olabilir.";
            typeAction = "Elle silmeyin; boyutu Windows sanal bellek ayarlarından yönetilir.";
        }
        else if (rootFile && name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase))
        {
            type = "Hazırda bekletme / Hızlı Başlatma dosyası adıyla eşleşiyor.";
            typeAction = "Elle silmeyin; Windows güç ayarlarından yönetilir. Özelliği kapatmak işlev kaybına yol açar.";
        }
        else if (rootFile && name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
        {
            type = "Windows takas dosyası adıyla eşleşiyor; sistem tarafından yönetilir.";
            typeAction = "Elle silmeyin; Windows bellek yönetimine bırakın.";
        }
        else if (dataRisk == FileDataRisk.VirtualDisk)
        {
            type = "Sanal disk olabilir; içinde işletim sistemi ve kişisel veriler bulunabilir.";
            highRiskType = true;
            typeAction = "Sanal makine / WSL / Docker gibi ilgili araçtan yönetin; elle silmek bütün diskin verisini kaybettirebilir.";
        }
        else if (dataRisk == FileDataRisk.Model)
        {
            type = "AI modeli olabilir; kullanılan veya özel bir model bulunabilir.";
            highRiskType = true;
            typeAction = "Kullanan uygulamayı kontrol edin; yeniden indirme mümkün olmayabilir.";
        }
        else if (dataRisk == FileDataRisk.Mail && ext.Equals(".ost", StringComparison.OrdinalIgnoreCase))
        {
            type = "Outlook yerel posta verisi olabilir; sunucuda bulunmayan veya eşitlenmemiş veriler içerebilir.";
            highRiskType = true;
            typeAction = "Outlook içinden inceleyin; eşitleme ve yedek durumu doğrulanmadan silmeyin.";
        }
        else if (dataRisk == FileDataRisk.Mail)
        {
            type = "Kişisel posta arşivi olabilir; tek kopya olabilir.";
            highRiskType = true;
            typeAction = "Yedeği doğrulanmadan silmeyin; Outlook içinden yönetin.";
        }
        else if (dataRisk == FileDataRisk.Diagnostics)
        {
            type = "Bellek / çökme dökümü olabilir; hata incelemesi için gerekebilir ve özel veri içerebilir.";
            typeAction = "Hata incelemesi ihtiyacını doğrulayın; sistem dökümlerini Windows depolama / hata dökümü seçeneklerinden inceleyin.";
        }
        else if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) || ext.Equals(".rar", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".7z", StringComparison.OrdinalIgnoreCase) || ext.Equals(".iso", StringComparison.OrdinalIgnoreCase))
            type = "Arşiv / disk görüntüsü olabilir; ihtiyaç duyulan dosyalar içerebilir.";
        else if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msix", StringComparison.OrdinalIgnoreCase))
            type = "Program / kurulum dosyası olabilir; çalışan uygulamanın parçası olabilir.";
        else if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase) || ext.Equals(".avi", StringComparison.OrdinalIgnoreCase))
            type = "Video / kayıt olabilir; kişisel içerik bulunabilir.";
        else type = "Kişisel veya uygulamaya ait dosya olabilir. Büyük olması gereksiz olduğu anlamına gelmez.";

        return new(context, type, actionFromContext || typeAction.Length == 0 ? contextAction : typeAction,
            actionFromContext && includeTypeAction && highRiskType ? typeAction : "", dataRisk);
    }

    internal static FileDataRisk RiskFor(string path) => RiskForExtension(Path.GetExtension(path.AsSpan()));
    static FileDataRisk RiskForExtension(ReadOnlySpan<char> extension)
    {
        if (extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vhdx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vmdk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".qcow2", StringComparison.OrdinalIgnoreCase)) return FileDataRisk.VirtualDisk;
        if (extension.Equals(".pst", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ost", StringComparison.OrdinalIgnoreCase)) return FileDataRisk.Mail;
        if (extension.Equals(".gguf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".onnx", StringComparison.OrdinalIgnoreCase)) return FileDataRisk.Model;
        return extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase) ? FileDataRisk.Diagnostics : FileDataRisk.None;
    }

    static bool KnownTemp(ReadOnlySpan<char> path) => RootFolder(path, "Temp") || RootFolder(path, @"Windows\Temp")
        || (IsDrivePath(path) && path.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase));

    static bool IsDrivePath(ReadOnlySpan<char> path) => path.Length >= 3
        && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
    static bool RootFolder(ReadOnlySpan<char> path, string folder)
    {
        if (!IsDrivePath(path)) return false;
        var relative = path[3..];
        return relative.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            && (relative.Length == folder.Length || relative.Length > folder.Length && relative[folder.Length] == '\\');
    }
}
