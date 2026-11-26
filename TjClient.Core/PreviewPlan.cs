namespace TjClient.Core;

public static class PreviewPlan
{
    public static IReadOnlyList<LocalFile> Select(ClientProfile profile, IReadOnlyList<LocalFile> files)
    {
        if (profile.Mode == InitialMode.NewFiles) return [];
        if (profile.Since == null) throw new ArgumentException("Выберите дату начала.");
        return files.Where(x => x.ModifiedTicks >= profile.Since.Value.UtcTicks).ToList();
    }
}
