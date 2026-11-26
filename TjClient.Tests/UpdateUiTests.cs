using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TjClient.Updates;

namespace TjClient.Wpf;

static class UpdateUiTests
{
    public static void ProductionWiring() => CompactWindowTests.InSta(() =>
    {
        using var fixture = new Fixture();
        Assert.True(UpdateProduction.IsInstalledDirectory(AppContext.BaseDirectory));
        Assert.True(UpdateProduction.IsInstalledDirectory(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)));
        Assert.True(!UpdateProduction.IsInstalledDirectory(null) && !UpdateProduction.IsInstalledDirectory(fixture.Root));
        var real = App.CreateWindow(fixture.Root, false, true);
        var smoke = App.CreateWindow(fixture.Root, true, true);
        var portable = App.CreateWindow(fixture.Root, false, false);
        try { real.AssertProductionUpdates(true); smoke.AssertProductionUpdates(false); portable.AssertProductionUpdates(false); }
        finally { real.DisposeTestWindow(); smoke.DisposeTestWindow(); portable.DisposeTestWindow(); }
        using var backend = UpdateProduction.CreateBackend(fixture.Root, new SignedUpdateTests.Lifetime());
        Assert.True(backend is SignedUpdateBackend);
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("TjCloudClient.Updates.PublicKey");
        Assert.True(stream != null, "Production public key должен быть embedded resource");
        Assert.Equal("fe62fa2f8c8133168d9b3593b5428457826434aed996667acd0c32637fffc159",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream!)));
        return Task.CompletedTask;
    });
    public static void Surface() => CompactWindowTests.InSta(async () =>
    {
        using var fixture = new Fixture();
        var backend = new UpdateConsentTests.CountingBackend();
        var prompts = 0;
        var constructor = typeof(MainWindow).GetConstructors().Single();
        Assert.True(constructor.GetParameters().Length >= 5, "Production окно должно принимать отдельную updater factory и consent prompt");
        Func<string, IUpdateLifetime, UpdateCoordinator> factory = (path, _) => new(path, () => backend);
        Func<Task<bool>> prompt = () => { prompts++; return Task.FromResult(false); };
        var window = (MainWindow)constructor.Invoke([fixture.Root, false, null, factory, prompt]);
        try
        {
            window.ShowActivated = false;
            window.Show();
            await Task.Delay(50);
            var checkbox = (CheckBox?)window.FindName("AutoUpdateBox");
            var manual = (MenuItem?)window.FindName("UpdateButton");
            Assert.True(checkbox != null && manual != null, "Нужны отдельные checkbox и действие в Ещё");
            Assert.Equal(1, prompts);
            Assert.Equal(0, backend.Calls);
            Assert.True(checkbox!.IsChecked == false);
            manual!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(30);
            Assert.Equal(0, backend.Calls);
            Assert.True(((TextBlock)window.FindName("UpdateStatusText")).Text.Contains("настройк"));
            checkbox.IsChecked = true;
            checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(30);
            Assert.Equal(1, backend.Calls);
            checkbox.IsChecked = false;
            checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(30);
            Assert.Equal(1, backend.Calls);
            Assert.Equal(1, prompts);
            window.AssertUpdateCheckboxUsable();
            var image = Environment.GetEnvironmentVariable("TJ_UPDATE_VISUAL");
            if (!string.IsNullOrWhiteSpace(image))
            {
                ((Expander)window.FindName("SettingsExpander")).IsExpanded = true;
                window.UpdateLayout();
                await Task.Delay(150);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(image);
                encoder.Save(output);
            }
        }
        finally { window.DisposeTestWindow(); }
    });
}

public partial class MainWindow
{
    internal void AssertProductionUpdates(bool enabled) => Assert.True((updates != null) == enabled);
    internal void AssertUpdateCheckboxUsable()
    {
        running = true;
        SetBusy(true);
        Assert.True(AutoUpdateBox.IsEnabled && !TransferSettings.IsEnabled && !SourceSettings.IsEnabled);
        running = false;
        SetBusy(false);
    }
}
