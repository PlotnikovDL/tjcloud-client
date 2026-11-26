using System;
using System.IO;
using System.Linq;
using System.Windows;
using TjClient.Core;

namespace TjClient.Wpf;

public partial class App : Application
{
    private ProfileLock? profileLock;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke-test");
        var folder = smoke
            ? Path.GetFullPath(Environment.GetEnvironmentVariable("TJCLOUD_SMOKE_DIRECTORY") ?? Path.Combine(Path.GetTempPath(), "TJCloudClient-gui-smoke"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TJCloudClient");
        try
        {
            profileLock = new ProfileLock(folder);
            var window = CreateWindow(folder, smoke, UpdateProduction.IsInstalledCopy());
            if (smoke) window.ShowActivated = false;
            MainWindow = window;
            window.Show();
        }
        catch (Exception)
        {
            if (!smoke) MessageBox.Show("Не удалось открыть профиль. Возможно, клиент уже работает в трее или папка профиля недоступна.", "TJ Cloud Client");
            Shutdown(2);
        }
    }
    internal static MainWindow CreateWindow(string folder, bool smoke, bool installed) =>
        new(folder, smoke, createUpdates: installed && !smoke ? UpdateProduction.Create : null);
    protected override void OnExit(ExitEventArgs e) { profileLock?.Dispose(); base.OnExit(e); }
}
