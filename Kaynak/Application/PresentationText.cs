using System.Globalization;
using System.Diagnostics;

namespace YerelDisk;

// UI-independent formatting and the explicit Explorer action shared by both hosts.
internal static class PresentationText
{
    internal static string Size(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double number = bytes;
        int unit = 0;
        while (number >= 1024 && unit < units.Length - 1) { number /= 1024; unit++; }
        return number.ToString(unit == 0 ? "N0" : "N2", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    internal static string Csv(params string[] cells) => string.Join(";", cells.Select(cell =>
    {
        string trimmed = cell.TrimStart();
        bool formula = trimmed.Length > 0 && "=+-@\t\r\n".Contains(trimmed[0]);
        string safe = formula ? "'" + cell : cell;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }));

    internal static ProcessStartInfo Explorer(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains('"') || path.Contains('\0'))
            throw new ArgumentException("Geçersiz yerel konum.");
        return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            Arguments = "/select,\"" + path + "\"", UseShellExecute = false
        };
    }
}
