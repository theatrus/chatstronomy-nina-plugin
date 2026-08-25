namespace Chatstronomy.NINA.Security;

/// <summary>
/// Keeps incidental debugger and diagnostic output from revealing secrets.
/// Serialization and secure runtime/bootstrap transports still use the
/// original values; only human-readable object rendering is sanitized.
/// </summary>
internal static class SensitiveDiagnostic
{
    internal const string Redacted = "[redacted]";

    internal static string Secret(string? value) =>
        value is null ? "<none>" : value.Length == 0 ? "<empty>" : Redacted;

    internal static string Endpoint(Uri endpoint, bool redactPath = false)
    {
        if (!endpoint.IsAbsoluteUri)
        {
            return Redacted;
        }

        // SchemeAndServer excludes userinfo. Never retain query parameters or
        // fragments, either: access tokens are commonly carried in both.
        var authority = endpoint.GetComponents(
            UriComponents.SchemeAndServer,
            UriFormat.UriEscaped);
        if (redactPath)
        {
            return $"{authority}/{Redacted}";
        }

        var removedSensitiveComponents = !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment);
        return $"{authority}{endpoint.AbsolutePath}"
            + (removedSensitiveComponents ? $" {Redacted}" : string.Empty);
    }
}
