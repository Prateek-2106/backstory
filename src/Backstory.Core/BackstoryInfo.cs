namespace Backstory.Core;

/// <summary>Build-level metadata. Placeholder until step 1 adds the domain.</summary>
public static class BackstoryInfo
{
    public const string ServiceName = "backstory";

    public static string Version =>
        typeof(BackstoryInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
