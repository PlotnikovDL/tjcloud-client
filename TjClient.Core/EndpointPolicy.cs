namespace TjClient.Core;

public static class EndpointPolicy
{
    public static Uri Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/")
            throw new ArgumentException("Нужен адрес HTTPS без логина, параметров и подпути.");
        return uri;
    }
}
