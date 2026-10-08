using Relic.Core.Util;

namespace Relic.Core.Install;

/// <summary>Creates .lnk shortcuts via WScript.Shell (through PowerShell, avoiding COM interop).</summary>
public static class Shortcut
{
    public static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private static readonly string AppIcon = Path.Combine(AppContext.BaseDirectory, "assets", "relic.ico");

    /// <summary>Relic's own brand icon, shipped next to the exe; null when a build did not copy it.</summary>
    public static string? AppIconPath => File.Exists(AppIcon) ? AppIcon : null;

    /// <summary>Create a desktop shortcut named <paramref name="name"/> pointing at target + args.</summary>
    public static string CreateOnDesktop(string name, string targetPath, string? arguments = null,
        string? workingDir = null, string? iconPath = null)
        => Create(Path.Combine(DesktopDir, name + ".lnk"), targetPath, arguments, workingDir, iconPath);

    public static string Create(string lnkPath, string targetPath, string? arguments = null,
        string? workingDir = null, string? iconPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        workingDir ??= Path.GetDirectoryName(targetPath);

        // Build a PowerShell script; single-quote values and escape embedded quotes.
        string script =
            $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{Esc(lnkPath)}');" +
            $"$s.TargetPath='{Esc(targetPath)}';" +
            (arguments is not null ? $"$s.Arguments='{Esc(arguments)}';" : "") +
            (workingDir is not null ? $"$s.WorkingDirectory='{Esc(workingDir)}';" : "") +
            (iconPath is not null ? $"$s.IconLocation='{Esc(iconPath)}';" : "") +
            "$s.Save()";

        var r = Proc.Run("powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script);
        if (!r.Ok || !File.Exists(lnkPath))
            throw new InvalidOperationException($"shortcut creation failed ({r.Code}): {r.Err.Trim()}");
        return lnkPath;
    }

    /// <summary>
    /// Delete every .lnk in <paramref name="dirs"/> (desktop + the user's Start Menu by default)
    /// whose target is <paramref name="targetExe"/>, and return how many went. Matching by TARGET
    /// rather than by file name is what makes the uninstall complete: the per-version shortcuts are
    /// named after the version ("Game 1.6"), and a shortcut whose registration was already lost from
    /// state.json would otherwise survive forever.
    /// </summary>
    public static int DeletePointingTo(string targetExe, params string[] dirs)
    {
        if (dirs.Length == 0)
            dirs = new[]
            {
                DesktopDir,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            };

        string list = string.Join(",", dirs.Select(d => $"'{Esc(d)}'"));
        string script =
            $"$t='{Esc(targetExe)}';$sh=New-Object -ComObject WScript.Shell;$n=0;" +
            $"foreach($d in @({list})){{" +
            "if(-not (Test-Path -LiteralPath $d)){continue}" +
            "foreach($f in Get-ChildItem -LiteralPath $d -Filter *.lnk -Recurse -Force -ErrorAction SilentlyContinue){" +
            "try{$s=$sh.CreateShortcut($f.FullName);if($s.TargetPath -ieq $t){Remove-Item -LiteralPath $f.FullName -Force;$n++}}catch{}" +
            "}}" +
            "Write-Output $n";

        var r = Proc.Run("powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script);
        return int.TryParse(r.Out.Trim(), out int n) ? n : 0;
    }

    /// <summary>
    /// Delete the .lnk files whose TargetPath is <paramref name="targetExe"/> AND whose Arguments equal
    /// <paramref name="arguments"/> (e.g. "--play 1.6"), on the desktop + Start Menu by default. Used by
    /// per-version removal: every per-version shortcut and the installer's own "Relic" shortcuts point
    /// at the same Relic.exe, so matching by target alone would take the others with it.
    /// </summary>
    public static int DeleteMatching(string targetExe, string arguments, params string[] dirs)
    {
        if (dirs.Length == 0)
            dirs = new[]
            {
                DesktopDir,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            };

        string list = string.Join(",", dirs.Select(d => $"'{Esc(d)}'"));
        string script =
            $"$t='{Esc(targetExe)}';$a='{Esc(arguments.Trim())}';$sh=New-Object -ComObject WScript.Shell;$n=0;" +
            $"foreach($d in @({list})){{" +
            "if(-not (Test-Path -LiteralPath $d)){continue}" +
            "foreach($f in Get-ChildItem -LiteralPath $d -Filter *.lnk -Recurse -Force -ErrorAction SilentlyContinue){" +
            "try{$s=$sh.CreateShortcut($f.FullName);if(($s.TargetPath -ieq $t) -and (([string]$s.Arguments).Trim() -ieq $a)){Remove-Item -LiteralPath $f.FullName -Force;$n++}}catch{}" +
            "}}" +
            "Write-Output $n";

        var r = Proc.Run("powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script);
        return int.TryParse(r.Out.Trim(), out int n) ? n : 0;
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
