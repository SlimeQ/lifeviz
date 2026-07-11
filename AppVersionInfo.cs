using System.Reflection;
using System.Text.RegularExpressions;

namespace lifeviz;

internal static class AppVersionInfo
{
    private static readonly Assembly ApplicationAssembly = typeof(AppVersionInfo).Assembly;
    private static readonly Regex CommitPattern = new("(?<![0-9a-f])[0-9a-f]{7,40}(?![0-9a-f])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string InformationalVersion { get; } =
        ApplicationAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Trim()
        ?? string.Empty;

    public static string FileVersion { get; } =
        ApplicationAssembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version?.Trim()
        ?? string.Empty;

    public static string DisplayVersion { get; } = FormatDisplayVersion(InformationalVersion, FileVersion);

    public static string WindowTitle { get; } = $"LifeViz {DisplayVersion}";

    public static string DiagnosticVersion { get; } = string.IsNullOrWhiteSpace(InformationalVersion)
        ? DisplayVersion
        : $"{DisplayVersion} ({InformationalVersion})";

    internal static string FormatDisplayVersion(string? informationalVersion, string? fileVersion)
    {
        if (TryFormatReleaseVersion(informationalVersion, out string releaseVersion))
        {
            return releaseVersion;
        }

        if (TryFormatReleaseVersion(fileVersion, out releaseVersion))
        {
            return releaseVersion;
        }

        string? commit = ExtractShortCommit(informationalVersion);
        return commit == null ? "dev" : $"dev ({commit})";
    }

    private static bool TryFormatReleaseVersion(string? value, out string displayVersion)
    {
        displayVersion = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string core = value.Trim();
        int metadataIndex = core.IndexOf('+');
        if (metadataIndex >= 0)
        {
            core = core[..metadataIndex];
        }

        string prerelease = string.Empty;
        int prereleaseIndex = core.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            prerelease = core[prereleaseIndex..];
            if (prerelease.Length == 1)
            {
                return false;
            }
            core = core[..prereleaseIndex];
        }

        if (core.StartsWith('v'))
        {
            core = core[1..];
        }

        string[] components = core.Split('.');
        if (components.Length is < 3 or > 4 ||
            !int.TryParse(components[0], out int major) ||
            !int.TryParse(components[1], out int minor) ||
            !int.TryParse(components[2], out int patch) ||
            (components.Length == 4 && !int.TryParse(components[3], out _)))
        {
            return false;
        }

        if (major == 0 && minor == 0 && patch == 0)
        {
            return false;
        }

        int revision = components.Length == 4 ? int.Parse(components[3]) : 0;
        string numericVersion = revision > 0
            ? $"v{major}.{minor}.{patch}.{revision}"
            : $"v{major}.{minor}.{patch}";
        displayVersion = numericVersion + prerelease;
        return true;
    }

    private static string? ExtractShortCommit(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return null;
        }

        Match match = CommitPattern.Match(informationalVersion);
        return match.Success ? match.Value[..Math.Min(7, match.Value.Length)].ToLowerInvariant() : null;
    }
}
