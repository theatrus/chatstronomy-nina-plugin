namespace Chatstronomy.NINA.Settings;

internal enum RuntimeSourceMode
{
    Direct,
    AdvancedApi,
}

internal static class RuntimeSourceModePolicy
{
    public const string AdvancedApiDeprecationNotice =
        "Advanced API mode is deprecated and retained only for profiles that already use it. "
        + "Switch to Direct mode; after switching, Advanced API cannot be re-enabled in this plugin.";

    public static bool IsDeprecated(RuntimeSourceMode mode) =>
        mode == RuntimeSourceMode.AdvancedApi;

    public static bool CanTransition(RuntimeSourceMode current, RuntimeSourceMode requested) =>
        requested == RuntimeSourceMode.Direct || current == requested;

    public static string AddDeprecationNotice(RuntimeSourceMode mode, string status) =>
        IsDeprecated(mode)
            ? $"{AdvancedApiDeprecationNotice} {status}"
            : status;
}
