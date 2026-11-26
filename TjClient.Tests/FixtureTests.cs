static class FixtureTests
{
    public static void IsolatedTemporaryRoot()
    {
        var original = Environment.GetEnvironmentVariable("TMPDIR");
        var temporary = Path.Combine(original ?? Path.GetTempPath(), "tjcloud-fixture-contract", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("TMPDIR", temporary);
            using var fixture = new Fixture();
            var expected = Path.GetFullPath(Path.Combine(temporary, "TJCloudClient-tests")) + Path.DirectorySeparatorChar;
            Assert.True(Path.GetFullPath(fixture.Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase),
                "Тестовая фикстура должна использовать отдельный временный корень без зависимости от среды разработчика");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", original);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }
}
