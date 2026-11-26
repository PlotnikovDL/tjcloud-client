using System;
using System.IO;
using System.Reflection;
using TjClient.Updates;

namespace TjClient.Wpf;

internal static class UpdateProduction
{
    internal static bool IsInstalledCopy()
    {
        const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{12ca27e9-70d7-48f0-b0ac-350146b5578a}_is1";
        try
        {
            using var registration = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(uninstallKey);
            return IsInstalledDirectory(registration?.GetValue("InstallLocation") as string);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool IsInstalledDirectory(string? location) =>
        !string.IsNullOrWhiteSpace(location) && Path.IsPathFullyQualified(location)
        && string.Equals(Path.TrimEndingDirectorySeparator(location), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            StringComparison.OrdinalIgnoreCase);

    internal static UpdateCoordinator Create(string directory, IUpdateLifetime lifetime) =>
        new(directory, () => CreateBackend(directory, lifetime));

    internal static SignedUpdateBackend CreateBackend(string directory, IUpdateLifetime lifetime)
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("TjCloudClient.Updates.PublicKey")
            ?? throw new InvalidDataException("Не найден ключ проверки обновлений.");
        using var reader = new StreamReader(stream);
        var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        return new SignedUpdateBackend(directory, version, reader.ReadToEnd(), lifetime);
    }
}
