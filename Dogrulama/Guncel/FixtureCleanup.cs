using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

// Developer-test inputs only; no permanent-delete fallback.
namespace YerelDisk.ValidationTools
{
    public static class FixtureCleanup
    {
        public static bool IsFixtureName(string name)
        {
            return Regex.IsMatch(name, @"\A(?:fixtures?|own-fixtures?|native-fixture)(?:-[0-9a-f]{32})?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || name == "capability-fixtures";
        }
        public static void Validate(string allowedRoot, string path)
        {
            string root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !IsFixtureName(Path.GetFileName(full)))
                throw new InvalidOperationException("Not a bounded generated fixture directory: " + full);
            string cursor = full;
            while (true)
            {
                FileAttributes attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Fixture/ancestor must be a real directory: " + cursor);
                if (string.Equals(cursor, root, StringComparison.OrdinalIgnoreCase)) break;
                cursor = Path.GetDirectoryName(cursor);
            }
        }
        public static string RecycleDirectory(string allowedRoot, string path)
        {
            Validate(allowedRoot, path);
            string destination = null; Exception failure = null;
            Thread thread = new Thread(delegate()
            {
                try { destination = RecycleSta(allowedRoot, Path.GetFullPath(path)); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) throw new IOException("Fixture was not confirmed in Recycle Bin: " + path, failure);
            return destination;
        }
        static string RecycleSta(string root, string path)
        {
            IFileOperation operation = null; IShellItem item = null;
            try
            {
                Validate(root, path); // Recheck immediately before queueing a mutation.
                operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));
                // RECYCLEONDELETE | EARLYFAILURE | SILENT | NOCONFIRMATION | NOERRORUI.
                Check(operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x0004 | 0x0010 | 0x0400));
                Guid iid = typeof(IShellItem).GUID;
                Check(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
                Sink sink = new Sink(path);
                Check(operation.DeleteItem(item, sink)); Check(operation.PerformOperations());
                bool aborted; Check(operation.GetAnyOperationsAborted(out aborted));
                if (aborted || sink.Destination == null || Directory.Exists(path) || !Directory.Exists(sink.Destination))
                    throw new IOException("Windows did not confirm a recycled directory.");
                return sink.Destination;
            }
            finally
            {
                if (item != null) Marshal.FinalReleaseComObject(item);
                if (operation != null) Marshal.FinalReleaseComObject(operation);
            }
        }
        static void Check(int result) { Marshal.ThrowExceptionForHR(result); }
        static string Name(IShellItem item)
        {
            IntPtr value; Check(item.GetDisplayName(0x80058000, out value));
            try { return Marshal.PtrToStringUni(value); } finally { Marshal.FreeCoTaskMem(value); }
        }
        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class Sink : IFileOperationProgressSink
        {
            readonly string source; public string Destination;
            public Sink(string path) { source = path; }
            public int StartOperations() { return 0; }
            public int FinishOperations(int result) { return 0; }
            public int PreRenameItem(uint f, IShellItem i, string n) { return unchecked((int)0x80004004); }
            public int PostRenameItem(uint f, IShellItem i, string n, int r, IShellItem c) { return 0; }
            public int PreMoveItem(uint f, IShellItem i, IShellItem d, string n) { return unchecked((int)0x80004004); }
            public int PostMoveItem(uint f, IShellItem i, IShellItem d, string n, int r, IShellItem c) { return 0; }
            public int PreCopyItem(uint f, IShellItem i, IShellItem d, string n) { return unchecked((int)0x80004004); }
            public int PostCopyItem(uint f, IShellItem i, IShellItem d, string n, int r, IShellItem c) { return 0; }
            public int PreDeleteItem(uint flags, IShellItem item)
            {
                // Veto permanent deletion even if Windows cannot use the bin.
                return (flags & 0x80) != 0 ? 0 : unchecked((int)0x80004004);
            }
            public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem created)
            {
                if (result < 0) return result;
                try
                {
                    if (string.Equals(Name(item), source, StringComparison.OrdinalIgnoreCase))
                    {
                        if (created == null) return unchecked((int)0x80004005);
                        Destination = Name(created);
                    }
                    return 0;
                }
                catch { return unchecked((int)0x80004005); }
            }
            public int PreNewItem(uint f, IShellItem d, string n) { return unchecked((int)0x80004004); }
            public int PostNewItem(uint f, IShellItem d, string n, string t, uint a, int r, IShellItem c) { return 0; }
            public int UpdateProgress(uint t, uint d) { return 0; }
            public int ResetTimer() { return 0; }
            public int PauseTimer() { return 0; }
            public int ResumeTimer() { return 0; }
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(uint name, out IntPtr result);
            [PreserveSig] int GetAttributes(uint mask, out uint attributes);
            [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
        }
        [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IFileOperationProgressSink
        {
            [PreserveSig] int StartOperations();
            [PreserveSig] int FinishOperations(int result);
            [PreserveSig] int PreRenameItem(uint f, IShellItem i, [MarshalAs(UnmanagedType.LPWStr)] string n);
            [PreserveSig] int PostRenameItem(uint f, IShellItem i, [MarshalAs(UnmanagedType.LPWStr)] string n, int r, IShellItem c);
            [PreserveSig] int PreMoveItem(uint f, IShellItem i, IShellItem d, [MarshalAs(UnmanagedType.LPWStr)] string n);
            [PreserveSig] int PostMoveItem(uint f, IShellItem i, IShellItem d, [MarshalAs(UnmanagedType.LPWStr)] string n, int r, IShellItem c);
            [PreserveSig] int PreCopyItem(uint f, IShellItem i, IShellItem d, [MarshalAs(UnmanagedType.LPWStr)] string n);
            [PreserveSig] int PostCopyItem(uint f, IShellItem i, IShellItem d, [MarshalAs(UnmanagedType.LPWStr)] string n, int r, IShellItem c);
            [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
            [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem created);
            [PreserveSig] int PreNewItem(uint f, IShellItem d, [MarshalAs(UnmanagedType.LPWStr)] string n);
            [PreserveSig] int PostNewItem(uint f, IShellItem d, [MarshalAs(UnmanagedType.LPWStr)] string n, [MarshalAs(UnmanagedType.LPWStr)] string t, uint a, int r, IShellItem c);
            [PreserveSig] int UpdateProgress(uint t, uint d);
            [PreserveSig] int ResetTimer();
            [PreserveSig] int PauseTimer();
            [PreserveSig] int ResumeTimer();
        }
        [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOperation
        {
            [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
            [PreserveSig] int Unadvise(uint cookie);
            [PreserveSig] int SetOperationFlags(uint flags);
            [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
            [PreserveSig] int SetProgressDialog(IntPtr dialog);
            [PreserveSig] int SetProperties(IntPtr properties);
            [PreserveSig] int SetOwnerWindow(uint window);
            [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
            [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
            [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
            [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
            [PreserveSig] int MoveItems(IntPtr items, IShellItem destination);
            [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
            [PreserveSig] int CopyItems(IntPtr items, IShellItem destination);
            [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink sink);
            [PreserveSig] int DeleteItems(IntPtr items);
            [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink sink);
            [PreserveSig] int PerformOperations();
            [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }
    }
}
