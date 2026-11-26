using System.Drawing;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace TjClient.Wpf;

static class BrandingTests
{
    internal const string ResourceName = "TjCloudClient.Branding.Icon";

    public static int ExecutableIcon(string path)
    {
        try
        {
            using var actual = Icon.ExtractAssociatedIcon(Path.GetFullPath(path))
                ?? throw new Exception("Windows could not extract the executable icon");
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(ResourceName)!;
            using var expected = new Icon(stream, actual.Size);
            AssertIconPixels(expected, actual);
            Console.WriteLine("PASS branding_exe_native_icon_matches_embedded_asset");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"FAIL branding_exe_native_icon_matches_embedded_asset: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    public static void TrayIcon() => CompactWindowTests.InSta(() =>
    {
        using var fixture = new Fixture();
        var window = new MainWindow(fixture.Root, false) { ShowActivated = false };
        try
        {
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(ResourceName)!;
            using var expected = new Icon(stream);
            AssertIconPixels(expected, window.TestTrayIcon);
        }
        finally { window.DisposeTestWindow(); }
        return Task.CompletedTask;
    });

    public static void IconLifetime() => CompactWindowTests.InSta(() =>
    {
        using var fixture = new Fixture();
        var window = new MainWindow(fixture.Root, false) { ShowActivated = false };
        var icon = window.TestTrayIcon;
        var handle = icon.Handle;
        var trayDisposed = false;
        var iconAliveAtTrayDispose = false;
        window.TestTray.Disposed += (_, _) => { trayDisposed = true; iconAliveAtTrayDispose = IsValidIcon(handle); };
        try
        {
            window.Show();
            window.Close();
            Assert.True(!window.IsVisible && window.TestTray.Visible && !trayDisposed, "Close hides to tray");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.True(IsValidIcon(handle), "Icon must outlive its source stream and hide-to-tray");
            using var bitmap = icon.ToBitmap();
            window.Show();
            Assert.True(window.IsVisible && icon.Handle == handle);
        }
        finally { window.CloseBrandingTestWindow(); }
        Assert.True(!IsValidIcon(handle), "Owned icon handle must be released on actual Closed");
        Assert.True(trayDisposed && iconAliveAtTrayDispose, "Tray must be disposed before icon");
        return Task.CompletedTask;
    });

    public static void WindowVersion() => CompactWindowTests.InSta(() =>
    {
        using var fixture = new Fixture();
        var window = new MainWindow(fixture.Root, false) { ShowActivated = false };
        try
        {
            var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
            Assert.Equal("TJ Cloud Client - доставка ТЖ - " + version, window.Title);
        }
        finally { window.DisposeTestWindow(); }
        return Task.CompletedTask;
    });

    public static void WindowIcon() => CompactWindowTests.InSta(() =>
    {
        using var fixture = new Fixture();
        var window = new MainWindow(fixture.Root, false) { ShowActivated = false };
        try
        {
            Assert.True(window.Icon is BitmapFrame, "Window must use the embedded multiframe icon");
            var frame = (BitmapFrame)window.Icon;
            Assert.True(frame.IsFrozen, "Window icon must not depend on the disposed stream");
            Assert.True(frame.Decoder!.Frames.Select(x => x.PixelWidth).Order().SequenceEqual(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }));
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(ResourceName)!;
            var expected = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(expected.PixelWidth, frame.PixelWidth);
            var stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
            var expectedPixels = new byte[stride * frame.PixelHeight];
            var actualPixels = new byte[expectedPixels.Length];
            expected.CopyPixels(expectedPixels, stride, 0);
            frame.CopyPixels(actualPixels, stride, 0);
            Assert.True(expectedPixels.SequenceEqual(actualPixels), "Window pixels must match the same ICO as tray");
        }
        finally { window.DisposeTestWindow(); }
        return Task.CompletedTask;
    });

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint XHotspot, YHotspot;
        public nint Mask, Color;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint value);
    private static bool IsValidIcon(nint icon)
    {
        if (!GetIconInfo(icon, out var info)) return false;
        if (info.Mask != 0) DeleteObject(info.Mask);
        if (info.Color != 0) DeleteObject(info.Color);
        return true;
    }

    internal static void AssertIconPixels(Icon expected, Icon actual)
    {
        using var left = expected.ToBitmap();
        using var right = actual.ToBitmap();
        Assert.Equal(left.Size, right.Size);
        for (var y = 0; y < left.Height; y++)
            for (var x = 0; x < left.Width; x++)
                Assert.Equal(left.GetPixel(x, y).ToArgb(), right.GetPixel(x, y).ToArgb());
    }
}

public partial class MainWindow
{
    internal System.Drawing.Icon TestTrayIcon => tray.Icon!;
    internal System.Windows.Forms.NotifyIcon TestTray => tray;
    internal void CloseBrandingTestWindow() { exiting = true; timer.Stop(); Close(); }
}
