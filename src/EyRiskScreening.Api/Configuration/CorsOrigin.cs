namespace EyRiskScreening.Api.Configuration;

internal static class CorsOrigin
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Contains('*', StringComparison.Ordinal)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || (uri.AbsolutePath != "/" && uri.AbsolutePath.Length > 0)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri
            .GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            .TrimEnd('/');
        return true;
    }
}
