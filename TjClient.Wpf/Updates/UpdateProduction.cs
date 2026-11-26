using System;
using System.IO;
using System.Reflection;
using TjClient.Updates;

namespace TjClient.Wpf;

internal static class UpdateProduction
{
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
