using System.Text.Json;
using Relic.Core.Util;

namespace Relic.Core.Setup;

/// <summary>A program Quick Start downloads into Relic's tools folder, pinned whole: its version, the https address
/// of that version's file, the file's size and sha256. An entry that lacks one of them is not used at all
/// (<see cref="Usable"/>).</summary>
public sealed record ToolEntry(string Version, string Url, long Size, string Sha256)
{
    public bool Usable => (Version ?? "").Trim().Length > 0 && SetupTools.IsHttps(Url) && Size > 0 && SetupTools.IsSha256(Sha256);
}

/// <summary>Docker Desktop's installer, which is always the current release: checked by its Authenticode signer
/// (<see cref="Signer"/>), never by a hash that would age, and by a size within half of <see cref="Size"/>.
/// <see cref="DiskBytes"/> = what Docker Desktop and its installer take on the disk; the minimums are Docker's own
/// requirements (Windows 10 / 11 builds, the WSL version, the memory). Usable only whole.</summary>
public sealed record DockerEntry(string Url, string Signer, string LicenceUrl, long Size, long DiskBytes,
    int MinWin10Build, int MinWin11Build, string MinWsl, long MinRamBytes)
{
    public bool Usable => SetupTools.IsHttps(Url) && (Signer ?? "").Trim().Length > 0 && SetupTools.IsHttps(LicenceUrl)
        && Size > 0 && DiskBytes > 0 && MinWin10Build > 0 && MinWin11Build > 0 && SetupTools.ParseVersion(MinWsl) is not null
        && MinRamBytes > 0;
}

/// <summary>The shipped <c>config/setup-tools.json</c>. An entry the file lacks, or one of the wrong shape, is
/// null; one that is there but not whole is kept with <c>Usable</c> false, so a caller can say "this build cannot
/// add it". <see cref="PublicIpServices"/> = the address services, all https, or none.</summary>
public sealed class SetupToolsCatalog
{
    public ToolEntry? Python { get; init; }
    public ToolEntry? SevenZip { get; init; }
    public DockerEntry? Docker { get; init; }
    public IReadOnlyList<string> PublicIpServices { get; init; } = Array.Empty<string>();

    public bool PythonUsable => Python?.Usable == true;
    public bool SevenZipUsable => SevenZip?.Usable == true;
    public bool DockerUsable => Docker?.Usable == true;

    public static SetupToolsCatalog Empty { get; } = new();
}

/// <summary>
/// Relic's tools folder (<c>%LOCALAPPDATA%\Relic\tools</c>: Python's embeddable package in <c>python\</c>,
/// 7-Zip's <c>7zr.exe</c>, partial downloads in <c>.download\</c>) and the catalogue that pins what goes there.
/// Nothing in that folder is on PATH or registered anywhere: the agent on this PC finds Relic's Python through
/// <see cref="Relic.Core.Server.LocalAgent.FindPython"/> and the extractor through its <c>GIO_7Z</c> setting.
/// </summary>
public static class SetupTools
{
    /// <summary>Spikes only: a temp folder in place of the real tools folder.</summary>
    public static string? ToolsDirOverride { get; set; }

    public static string ToolsDir => ToolsDirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relic", "tools");
    public static string PythonDir => Path.Combine(ToolsDir, "python");
    public static string PythonExe => Path.Combine(PythonDir, "python.exe");
    public static string PythonExeW => Path.Combine(PythonDir, "pythonw.exe");
    public static string SevenZipExe => Path.Combine(ToolsDir, "7zr.exe");
    public static string DownloadDir => Path.Combine(ToolsDir, ".download");

    public static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "config", "setup-tools.json");

    /// <summary>The catalogue at <paramref name="path"/> (the shipped one by default); an empty one when the file is
    /// missing or cannot be read (logged). Never throws.</summary>
    public static SetupToolsCatalog Load(string? path = null)
    {
        string p = string.IsNullOrWhiteSpace(path) ? ShippedPath : path!;
        try { return File.Exists(p) ? Parse(File.ReadAllText(p)) : SetupToolsCatalog.Empty; }
        catch (Exception ex)
        {
            Log.Info($"setup tools catalogue {p} cannot be read ({ex.Message}) -- Quick Start adds no tool");
            return SetupToolsCatalog.Empty;
        }
    }

    /// <summary>The catalogue in <paramref name="json"/>. Throws only on text that is no JSON.</summary>
    public static SetupToolsCatalog Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return SetupToolsCatalog.Empty;
        ToolEntry? Tool(string name) =>
            root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object
                ? new ToolEntry(Str(e, "version"), Str(e, "url"), Long(e, "size"), Str(e, "sha256"))
                : null;
        DockerEntry? docker = null;
        if (root.TryGetProperty("docker", out var d) && d.ValueKind == JsonValueKind.Object)
            docker = new DockerEntry(Str(d, "url"), Str(d, "signer"), Str(d, "licenceUrl"), Long(d, "size"), Long(d, "diskBytes"),
                (int)Math.Clamp(Long(d, "minWin10Build"), 0, int.MaxValue), (int)Math.Clamp(Long(d, "minWin11Build"), 0, int.MaxValue),
                Str(d, "minWsl"), Long(d, "minRamBytes"));
        var services = new List<string>();
        bool servicesOk = root.TryGetProperty("publicIpServices", out var s) && s.ValueKind == JsonValueKind.Array;
        if (servicesOk)
            foreach (var u in s.EnumerateArray())
            {
                string url = u.ValueKind == JsonValueKind.String ? (u.GetString() ?? "").Trim() : "";
                if (!IsHttps(url)) { servicesOk = false; break; }
                services.Add(url);
            }
        return new SetupToolsCatalog
        {
            Python = Tool("python"),
            SevenZip = Tool("sevenZip"),
            Docker = docker,
            PublicIpServices = servicesOk ? services : Array.Empty<string>(),
        };
    }

    /// <summary>An absolute https address with a host.</summary>
    public static bool IsHttps(string? url) =>
        Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host.Length > 0;

    /// <summary>64 hex digits.</summary>
    public static bool IsSha256(string? s) =>
        (s ?? "").Length == 64 && (s ?? "").All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>A version written as two to four dot-separated numbers ("2.1.5", "2.3.24.0"); null otherwise.</summary>
    public static Version? ParseVersion(string? s)
    {
        string t = (s ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(t, @"^[0-9]+(\.[0-9]+){1,3}$")) return null;
        return Version.TryParse(t, out var v) ? v : null;
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "").Trim() : "";

    private static long Long(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long n) ? n : 0;
}
