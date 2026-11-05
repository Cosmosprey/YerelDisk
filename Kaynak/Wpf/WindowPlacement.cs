using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YerelDisk.Wpf;

// Window geometry belongs to the UI, never to Core/Application. Keep the native
// title bar and fit the OUTER window (including its caption) to the work area.
internal static class WindowPlacement
{
    internal sealed record Layout(Rect Bounds, double MinWidth, double MinHeight);

    internal static Layout Calculate(Rect workArea, double width, double height, double minWidth, double minHeight)
    {
        if (workArea.IsEmpty || !double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height)
            || workArea.Width <= 0 || workArea.Height <= 0) throw new ArgumentException("Geçersiz ekran çalışma alanı.");
        double availableWidth = workArea.Width * .92, availableHeight = workArea.Height * .92;
        double minimumWidth = Math.Min(minWidth, availableWidth), minimumHeight = Math.Min(minHeight, availableHeight);
        width = Math.Clamp(double.IsFinite(width) ? width : availableWidth, minimumWidth, availableWidth);
        height = Math.Clamp(double.IsFinite(height) ? height : availableHeight, minimumHeight, availableHeight);
        return new(new Rect(workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2, width, height), minimumWidth, minimumHeight);
    }

    internal static void Attach(Window window)
    {
        bool fitted = false;
        window.SourceInitialized += (_, _) =>
        {
            // Explicit manual placement is intentional (including offscreen UI test
            // fixtures); only automatically centered application windows are fitted.
            if (window.WindowStartupLocation == WindowStartupLocation.Manual) return;
            var layout = Calculate(WorkArea(window), window.Width, window.Height, window.MinWidth, window.MinHeight);
            window.MinWidth = layout.MinWidth; window.MinHeight = layout.MinHeight;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Width = layout.Bounds.Width; window.Height = layout.Bounds.Height;
            window.Left = layout.Bounds.Left; window.Top = layout.Bounds.Top;
            fitted = true;
        };
        window.Loaded += (_, _) =>
        {
            // Recheck after WPF's initial native sizing/centering has completed.
            // Never interfere with explicit manual placement or a maximized window.
            if (!fitted || window.WindowState != WindowState.Normal) return;
            var area = WorkArea(window);
            var bounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            if (area.Contains(bounds)) return;
            var layout = Calculate(area, window.Width, window.Height, window.MinWidth, window.MinHeight);
            window.MinWidth = layout.MinWidth; window.MinHeight = layout.MinHeight;
            window.Width = layout.Bounds.Width; window.Height = layout.Bounds.Height;
            window.Left = layout.Bounds.Left; window.Top = layout.Bounds.Top;
        };
    }

    internal static Rect WorkArea(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return SystemParameters.WorkArea;
        nint target = window.Owner is { } owner ? new WindowInteropHelper(owner).Handle : handle;
        if (target == 0) target = handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(target, 2), ref info)) return SystemParameters.WorkArea;
        var pixels = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        // MonitorInfo is in device coordinates; WPF Width/Height/Left/Top are DIPs.
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is not { } targetSurface) return SystemParameters.WorkArea;
        pixels.Transform(targetSurface.TransformFromDevice);
        return pixels;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PixelRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo { public int Size; public PixelRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
