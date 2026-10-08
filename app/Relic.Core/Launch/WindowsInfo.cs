namespace Relic.Core.Launch;

public readonly record struct WindowsRelease(int Major, int Build, bool IsWindows11)
{
    public override string ToString() => $"Windows {(IsWindows11 ? 11 : Major)} (build {Build})";
}

public static class WindowsInfo
{
    /// <summary>
    /// True Windows release. .NET's Environment.OSVersion uses RtlGetVersion, so it reports the real
    /// build even without a supportedOS manifest. Win11 == 10.0 build >= 22000.
    /// </summary>
    public static WindowsRelease Detect()
    {
        var v = Environment.OSVersion.Version;
        bool win11 = v.Major >= 10 && v.Build >= 22000;
        return new WindowsRelease(v.Major, v.Build, win11);
    }
}
