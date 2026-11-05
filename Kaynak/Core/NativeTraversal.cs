using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YerelDisk;

internal class ScopeChangedException(string message) : IOException(message);
internal sealed class RootScopeChangedException(string message) : ScopeChangedException(message);
internal sealed class PathUnavailableException(string message) : IOException(message);
internal sealed class NativePathException(string message, int nativeError) : IOException(message)
{
    public int NativeError { get; } = nativeError;
    public bool MayBeTransient => NativeError is 2 or 3 or 32 or 33;
}
internal sealed record NativeEntry(string Name, long Size, DateTime Modified, FileAttributes Attributes, ulong Id);

// Every lookup after the drive root is relative to an already opened directory.
// No path-based fallback is used if handle enumeration or relative opens fail.
internal sealed class NativeDirectory : IDisposable
{
    public const FileAttributes CloudFlags = (FileAttributes)(0x1000 | 0x40000 | 0x400000);
    public SafeFileHandle Handle { get; }
    public string ExpectedPath { get; }
    NativeDirectory(SafeFileHandle handle, string expectedPath)
    {
        Handle = handle;
        ExpectedPath = expectedPath.TrimEnd('\\');
        try { EnsureRootLocation(); }
        catch { handle.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory, ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }

    [DllImport("ntdll.dll")]
    static extern int NtCreateFile(out SafeFileHandle handle, uint desiredAccess, ref ObjectAttributes attributes,
        out IoStatusBlock status, IntPtr allocationSize, uint fileAttributes, uint shareAccess,
        uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
    [DllImport("ntdll.dll")]
    static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, IntPtr buffer, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, IntPtr volumeName, uint volumeNameSize,
        out uint serial, out uint maxComponent, out uint flags, IntPtr fsName, uint fsNameSize);

    static IOException Error(string operation) => new(operation + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);

    public static NativeDirectory OpenRoot(string path, bool enumerate = true)
    {
        string drive = Path.GetPathRoot(path)!;
        string[] parts = path[drive.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string canonical = @"\\?\" + drive;
        uint access = 0x100080u | (parts.Length == 0 && enumerate ? 1u : 0u);
        SafeFileHandle handle = CreateFileW(canonical, access, 7, IntPtr.Zero, 3,
            0x02000000 | 0x00200000 | 0x00100000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw Error("Yerel sürücü açılamadı"); }
        NativeDirectory current = new(handle, canonical);
        try
        {
            for (int i = 0; i < parts.Length; i++)
            {
                NativeDirectory next = current.OpenDirectory(parts[i], null, enumerate && i == parts.Length - 1);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    public bool SupportsNamedStreams => !GetVolumeInformationByHandleW(Handle, IntPtr.Zero, 0,
        out _, out _, out uint flags, IntPtr.Zero, 0) || (flags & 0x40000) != 0;

    // strict=false: a path that can no longer be opened (directory deleted mid-scan) throws the
    // non-fatal PathUnavailableException; an identity mismatch is always fatal.
    public void VerifyBinding(string displayedPath, bool strict = true)
    {
        // GetFinalPathNameByHandle can retain a cached ancestor name. Re-resolve the
        // display path using no-follow component opens and compare the anchored identity.
        // The probe never enumerates directories or opens file contents.
        try
        {
            using NativeDirectory probe = OpenRoot(displayedPath, false);
            if (NativeMetadata.Identity(probe.Handle) != NativeMetadata.Identity(Handle))
                throw new ScopeChangedException("Gösterilen yol artık taranan klasör kimliğine bağlı değil; tarama durduruldu.");
        }
        catch (ScopeChangedException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!strict) throw new PathUnavailableException("Klasör tarama sırasında silindi veya taşındı; içeriği kısmi olabilir. " + ex.Message);
            throw new ScopeChangedException("Klasör yolu / kimliği yeniden doğrulanamadı; tarama durduruldu. " + ex.Message);
        }
    }

    // Claude 1.2.1: non-root close-out check. Re-open only the last path component, with the same
    // no-follow open, relative to the still-open parent, and compare identities. Outcomes match
    // VerifyBinding(strict: false): missing -> PathUnavailableException, other object -> fatal.
    // Each ancestor gets the same check when it closes and the root keeps the strict full-path
    // check, so the whole path is no longer re-resolved from the drive for every folder.
    public void VerifyLink(NativeDirectory parent, string name)
    {
        try
        {
            using SafeFileHandle probe = parent.OpenRelative(name, true, false);
            if (NativeMetadata.Identity(probe) != NativeMetadata.Identity(Handle))
                throw new ScopeChangedException("Gösterilen yol artık taranan klasör kimliğine bağlı değil; tarama durduruldu.");
        }
        catch (ScopeChangedException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new PathUnavailableException("Klasör tarama sırasında silindi veya taşındı; içeriği kısmi olabilir. " + ex.Message); }
    }

    public void EnsureLocation() => CheckLocation(allowDeleted: true);
    public void EnsureRootLocation() => CheckLocation(allowDeleted: false);
    void CheckLocation(bool allowDeleted)
    {
        var attributes = NativeMetadata.Attributes(Handle);
        if ((attributes & FileAttributes.Directory) == 0)
            throw new ScopeChangedException("Beklenen klasör artık bir klasör değil; tarama durduruldu.");
        if ((attributes & (FileAttributes.ReparsePoint | CloudFlags)) != 0)
            throw new ScopeChangedException("Bağlantı veya bulut klasörü izlenmedi; tarama kapsamı korunamadı.");
        StringBuilder path = new(512);
        uint needed = GetFinalPathNameByHandleW(Handle, path, (uint)path.Capacity, 0);
        if (needed == 0) throw Error("Klasörün gerçek konumu doğrulanamadı");
        if (needed >= path.Capacity)
        {
            path = new StringBuilder(checked((int)needed + 1));
            needed = GetFinalPathNameByHandleW(Handle, path, (uint)path.Capacity, 0);
            if (needed == 0 || needed >= path.Capacity) throw Error("Klasörün gerçek konumu doğrulanamadı");
        }
        if (!string.Equals(path.ToString().TrimEnd('\\'), ExpectedPath, StringComparison.OrdinalIgnoreCase))
        {
            if (allowDeleted && NativeMetadata.IsDeletePending(Handle))
            {
                // Deletion can give an open handle a $Deleted path. Before treating this as
                // unavailable, re-open the display path without following links. A replacement
                // object must still be fatal. Newly opened objects and roots stay strict.
                VerifyBinding(ExpectedPath[4..], strict: false);
            }
            throw new ScopeChangedException("Tarama sırasında klasörün konumu değişti. Yeni hedef izlenmeden tarama durduruldu.");
        }
    }

    SafeFileHandle OpenRelative(string name, bool directory, bool enumerate)
    {
        if (name is "." or ".." || name.Length == 0 || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
            throw new IOException("Tek bileşenli dosya adı bekleniyordu.");
        IntPtr nameBuffer = Marshal.StringToHGlobalUni(name);
        IntPtr stringBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        bool addedRef = false;
        try
        {
            UnicodeString text = new() { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2 + 2)), Buffer = nameBuffer };
            Marshal.StructureToPtr(text, stringBuffer, false);
            Handle.DangerousAddRef(ref addedRef);
            ObjectAttributes oa = new()
            {
                Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = Handle.DangerousGetHandle(),
                ObjectName = stringBuffer, Attributes = 0x1040 // OBJ_DONT_REPARSE | OBJ_CASE_INSENSITIVE
            };
            uint access = 0x100080u | (directory && enumerate ? 1u : 0u); // attributes + synchronize (+ list directory)
            // FILE_OPEN + synchronous + NO_RECALL + OPEN_REPARSE_POINT.
            // Windows rejects NO_RECALL together with FILE_DIRECTORY_FILE. Open directories
            // type-neutral and verify the Directory attribute on the returned handle before use.
            int status = NtCreateFile(out SafeFileHandle file, access, ref oa, out _, IntPtr.Zero, 0, 7, 1,
                0x20 | 0x00400000 | 0x00200000 | (directory ? 0u : 0x40u), IntPtr.Zero, 0);
            if (status < 0)
            {
                file?.Dispose();
                int error = (int)RtlNtStatusToDosError(status);
                if (status == unchecked((int)0xC000050B))
                    throw new ScopeChangedException("Açılış sırasında bağlantı algılandı; hedef izlenmedi.");
                if (error == 5) throw new UnauthorizedAccessException("Erişim izni yok; taranmadı.");
                throw new NativePathException("Dosya / klasör açılamadı: " + new Win32Exception(error).Message, error);
            }
            return file;
        }
        finally
        {
            if (addedRef) Handle.DangerousRelease();
            Marshal.FreeHGlobal(stringBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    public NativeDirectory OpenDirectory(string name, ulong? expectedId, bool enumerate = true)
    {
        SafeFileHandle handle = OpenRelative(name, true, enumerate);
        try
        {
            if (expectedId.HasValue && expectedId != 0 && NativeMetadata.Identity(handle).Id != expectedId.Value)
                throw new ScopeChangedException("Klasör kimliği tarama sırasında değişti; yeni hedef izlenmedi.");
            return new NativeDirectory(handle, ExpectedPath + "\\" + name);
        }
        catch { handle.Dispose(); throw; }
    }

    public SafeFileHandle OpenFile(string name) => OpenRelative(name, false, false);

    public IEnumerable<NativeEntry> Entries()
    {
        const int bufferSize = 64 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        bool restart = true;
        try
        {
            while (true)
            {
                if (!GetFileInformationByHandleEx(Handle, restart ? 11 : 10, buffer, bufferSize))
                {
                    if (Marshal.GetLastWin32Error() == 18) yield break; // ERROR_NO_MORE_FILES
                    throw Error("Klasör içeriği tanıtıcı üzerinden okunamadı");
                }
                restart = false;
                int offset = 0;
                while (true)
                {
                    if (offset < 0 || offset > bufferSize - 104) throw new IOException("Geçersiz klasör üstverisi.");
                    IntPtr row = IntPtr.Add(buffer, offset);
                    int next = Marshal.ReadInt32(row, 0), nameLength = Marshal.ReadInt32(row, 60);
                    if (nameLength < 0 || (nameLength & 1) != 0 || nameLength > bufferSize - offset - 104)
                        throw new IOException("Geçersiz dosya adı üstverisi.");
                    string name = Marshal.PtrToStringUni(IntPtr.Add(row, 104), nameLength / 2)!;
                    if (name is not ("." or ".."))
                        yield return new NativeEntry(name, Math.Max(0, Marshal.ReadInt64(row, 40)),
                            SafeFileTime(Marshal.ReadInt64(row, 24)),
                            (FileAttributes)Marshal.ReadInt32(row, 56), unchecked((ulong)Marshal.ReadInt64(row, 96)));
                    if (next == 0) break;
                    if (next < 104 || next > bufferSize - offset) throw new IOException("Geçersiz klasör kayıt aralığı.");
                    offset += next;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    // Some files carry timestamps outside the DateTime range; one bad stamp must not abort a scan.
    static DateTime SafeFileTime(long value)
    {
        try { return DateTime.FromFileTime(value); }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }
    public void Dispose() => Handle.Dispose();
}

// Claude 1.3.0: parallel metadata reads pay off on SSD / NVMe but can make a spinning disk seek.
// Windows is only asked whether the disk under the volume has a seek penalty. The volume is
// opened with no access rights, so nothing on it can be read; any failure keeps one worker.
internal static class VolumeInfo
{
    [StructLayout(LayoutKind.Sequential)]
    struct PropertyQuery { public int PropertyId, QueryType; public byte AdditionalParameters; }
    [StructLayout(LayoutKind.Sequential)]
    struct SeekPenaltyDescriptor { public uint Version, Size; public byte IncursSeekPenalty; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref PropertyQuery input, int inputSize,
        out SeekPenaltyDescriptor output, int outputSize, out int returned, IntPtr overlapped);

    // Half the logical processors, at most 8: on a 12-thread NVMe machine 6 workers were ~3.9x and
    // 8 ~4.3x faster than one, 12 no better than 8 (Dogrulama\Claude-1.3.0\sonuclar).
    public static int ScanWorkers(string rootPath)
    {
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        return workers > 1 && NoSeekPenalty(rootPath) ? workers : 1;
    }

    public static bool NoSeekPenalty(string rootPath)
    {
        string drive = (Path.GetPathRoot(rootPath) ?? "").TrimEnd('\\');
        if (drive.Length != 2 || drive[1] != ':') return false;
        using SafeFileHandle volume = CreateFileW(@"\\.\" + drive, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (volume.IsInvalid) return false;
        // IOCTL_STORAGE_QUERY_PROPERTY, StorageDeviceSeekPenaltyProperty, PropertyStandardQuery.
        PropertyQuery query = new() { PropertyId = 7, QueryType = 0 };
        return DeviceIoControl(volume, 0x2D1400, ref query, Marshal.SizeOf<PropertyQuery>(),
                out SeekPenaltyDescriptor answer, Marshal.SizeOf<SeekPenaltyDescriptor>(), out int returned, IntPtr.Zero)
            && returned >= 9 && answer.IncursSeekPenalty == 0;
    }
}
