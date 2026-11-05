using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YerelDisk.Presentation;

// Only open/query/enumerate/close are imported. No writes, commands, expansion,
// remote registry or target filesystem probes. Links are opened as link objects.
internal interface IInventoryRegistryNative
{
    int Open(SafeRegistryHandle parent, string name, uint options, uint access, out SafeRegistryHandle handle);
    int QuerySize(SafeRegistryHandle key, string field, out uint type, ref uint size);
    int QueryData(SafeRegistryHandle key, string field, out uint type, byte[] data, ref uint size);
    int Enumerate(SafeRegistryHandle key, uint index, StringBuilder name, ref uint length);
}
internal sealed class WindowsInventoryRegistryBackend(IInventoryRegistryNative? api = null) : IInventoryRegistryBackend
{
    readonly IInventoryRegistryNative native = api ?? new WindowsInventoryRegistryNative();
    const uint OpenLink = 8, QueryAndEnumerate = 0x0001 | 0x0008;
    static readonly string[] Fields = ["InstallLocation", "DisplayName", "Publisher", "DisplayVersion"];
    public RegistryReadResult Open(InventorySource source, out IInventoryRegistryKey? key, Action? checkpoint = null)
    {
        key = null;
        using var hive = new SafeRegistryHandle(new IntPtr(source.Machine ? unchecked((int)0x80000002) : unchecked((int)0x80000001)), false);
        SafeRegistryHandle current = hive; bool ownsCurrent = false; uint view = source.View64 ? 0x0100u : 0x0200u;
        try
        {
            foreach (string part in new[] { "Software", "Microsoft", "Windows", "CurrentVersion", "Uninstall" })
            {
                var result = OpenChecked(current, part, view, out var next, checkpoint);
                if (result != RegistryReadResult.Success || next == null) return result;
                if (ownsCurrent) current.Dispose(); current = next; ownsCurrent = true;
            }
            key = new Key(this, current, view); ownsCurrent = false; return RegistryReadResult.Success;
        }
        finally { if (ownsCurrent) current.Dispose(); }
    }
    RegistryReadResult OpenChecked(SafeRegistryHandle parent, string name, uint view, out SafeRegistryHandle? handle, Action? checkpoint = null)
    {
        checkpoint?.Invoke();
        handle = null; int error = native.Open(parent, name, OpenLink, QueryAndEnumerate | view, out var opened);
        if (error != 0) { opened?.Dispose(); return Map(error); }
        try
        {
            checkpoint?.Invoke();
            uint size = 0; int probe = native.QuerySize(opened, "SymbolicLinkValue", out uint type, ref size);
            if (probe == 0 && type == 6) return RegistryReadResult.Link;
            if (probe is not (0 or 2)) return Map(probe);
            handle = opened; return RegistryReadResult.Success;
        }
        finally { if (handle == null) opened.Dispose(); }
    }
    sealed class Key(WindowsInventoryRegistryBackend owner, SafeRegistryHandle handle, uint view) : IInventoryRegistryKey
    {
        public RegistryReadResult Name(uint index, out string name)
        { var buffer = new StringBuilder(256); uint size = 256; int error = owner.native.Enumerate(handle, index, buffer, ref size); name = error == 0 ? buffer.ToString() : ""; return Map(error); }
        public RegistryReadResult OpenChild(string name, out IInventoryRegistryKey? child, Action? checkpoint = null)
        {
            child = null;
            if (name.Length is 0 or > 255 || name.IndexOfAny(['\\', '/', '\0']) >= 0) return RegistryReadResult.Invalid;
            var result = owner.OpenChecked(handle, name, view, out var opened, checkpoint); if (opened != null) child = new Key(owner, opened, view); return result;
        }
        public RegistryText ReadText(string field, int maximumBytes, Action? checkpoint = null)
        {
            if (!Fields.Contains(field, StringComparer.Ordinal)) return new(RegistryReadResult.Invalid);
            checkpoint?.Invoke();
            uint size = 0; int error = owner.native.QuerySize(handle, field, out uint type, ref size);
            if (error != 0) return new(Map(error));
            if (type is not (1 or 2) || size > maximumBytes || size > 65536 || (size & 1) != 0) return new(RegistryReadResult.Invalid);
            if (size == 0) return new(RegistryReadResult.Success, "", 0);
            byte[] buffer = new byte[(int)size]; uint actual = size; uint originalType = type;
            checkpoint?.Invoke();
            error = owner.native.QueryData(handle, field, out type, buffer, ref actual);
            // A changing/growing/type-changing value is skipped, never resized unboundedly.
            if (error != 0 || type != originalType || actual > size || (actual & 1) != 0) return new(RegistryReadResult.Invalid, Bytes: (int)size);
            try
            {
                string text = new UnicodeEncoding(false, false, true).GetString(buffer, 0, (int)actual);
                if (text.EndsWith('\0')) text = text[..^1];
                return text.Contains('\0') ? new(RegistryReadResult.Invalid, Bytes: (int)actual) : new(RegistryReadResult.Success, text, (int)actual);
            }
            catch (DecoderFallbackException) { return new(RegistryReadResult.Invalid, Bytes: (int)actual); }
        }
        public void Dispose() => handle.Dispose();
    }
    static RegistryReadResult Map(int error) => error switch { 0 => RegistryReadResult.Success, 2 or 3 => RegistryReadResult.Missing, 5 => RegistryReadResult.Denied, 259 => RegistryReadResult.End, _ => RegistryReadResult.Error };
}
internal sealed class WindowsInventoryRegistryNative : IInventoryRegistryNative
{
    public int Open(SafeRegistryHandle parent, string name, uint options, uint access, out SafeRegistryHandle handle) => RegOpenKeyExW(parent, name, options, access, out handle);
    public int QuerySize(SafeRegistryHandle key, string field, out uint type, ref uint size) => RegQueryValueSize(key, field, IntPtr.Zero, out type, IntPtr.Zero, ref size);
    public int QueryData(SafeRegistryHandle key, string field, out uint type, byte[] data, ref uint size) => RegQueryValueData(key, field, IntPtr.Zero, out type, data, ref size);
    public int Enumerate(SafeRegistryHandle key, uint index, StringBuilder name, ref uint length) => RegEnumKeyExW(key, index, name, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern int RegOpenKeyExW(SafeRegistryHandle key, string subKey, uint options, uint access, out SafeRegistryHandle result);
    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern int RegQueryValueSize(SafeRegistryHandle key, string name, IntPtr reserved, out uint type, IntPtr data, ref uint bytes);
    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern int RegQueryValueData(SafeRegistryHandle key, string name, IntPtr reserved, out uint type, [Out] byte[] data, ref uint bytes);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern int RegEnumKeyExW(SafeRegistryHandle key, uint index, StringBuilder name, ref uint length, IntPtr reserved, IntPtr keyClass, IntPtr classLength, IntPtr lastWrite);
}
