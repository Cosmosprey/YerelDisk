using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

// Probe only newly-created fixtures. A failed probe is not a product test failure.
internal static class Capabilities
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string name, string existing, nint security);
    static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root)) throw new IOException("Capability fixture must be new.");
        Directory.CreateDirectory(root);
        var results = new Dictionary<string, object>();
        void Probe(string name, Action test)
        {
            try { test(); results[name] = new { Available = true, Reason = "" }; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException or PlatformNotSupportedException)
            { results[name] = new { Available = false, Reason = ex.GetType().Name + ": " + ex.Message }; }
        }
        Probe("HardLink", () =>
        {
            string file = Path.Combine(root, "hardlink-original.bin"); File.WriteAllBytes(file, [1]);
            if (!CreateHardLinkW(Path.Combine(root, "hardlink-alias.bin"), file, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        });
        Probe("Streams", () =>
        {
            string file = Path.Combine(root, "streams.bin"); File.WriteAllBytes(file, [1]);
            File.WriteAllBytes(file + ":probe", [2, 3]);
            if (File.ReadAllBytes(file + ":probe").Length != 2) throw new IOException("Named stream probe failed.");
        });
        Probe("Junction", () =>
        {
            string target = Path.Combine(root, "junction-target"), link = Path.Combine(root, "junction-link"); Directory.CreateDirectory(target);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
            using var process = Process.Start(start) ?? throw new IOException("PowerShell did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000)) { process.Kill(); throw new IOException("Junction probe timed out."); }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0 || (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) throw new IOException("Junction unavailable: " + stderr.Result.Trim());
            Directory.Delete(link); // Unlink only; never recurse through the target.
        });
        Probe("Acl", () =>
        {
            string path = Path.Combine(root, "acl-directory"); Directory.CreateDirectory(path);
            var directory = new DirectoryInfo(path); var original = directory.GetAccessControl();
            string sddl = original.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            try
            {
                var changed = directory.GetAccessControl();
                changed.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
                directory.SetAccessControl(changed);
                bool denied = false;
                try { _ = Directory.GetFileSystemEntries(path); } catch (UnauthorizedAccessException) { denied = true; }
                if (!denied) throw new IOException("Directory-list denial is not enforced for this process.");
            }
            finally
            {
                var restored = new DirectorySecurity(); restored.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
                try { directory.SetAccessControl(restored); }
                catch (Exception ex) { throw new InvalidOperationException("Capability fixture ACL could not be restored: " + path, ex); }
            }
        });
        File.WriteAllText(args[1], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
