using System.Text.Json;
using Relic.Core.Util;

namespace Relic.Core.Setup;

/// <summary>The memory a version's server takes once it has booted — without its pathfinding server
/// (<see cref="Ram"/>) and with it (<see cref="RamPathfinding"/>; the same number for a package that keeps that
/// server off) — and the disk Docker's images of its stack take (<see cref="Images"/>; null = not known).</summary>
public sealed record ServerNeedRow(long Ram, long RamPathfinding, long? Images);

/// <summary>The shipped <c>config/server-needs.json</c>: a row per server version, and the memory the game
/// client of each game version takes while it plays (<see cref="Clients"/>). A version without a row has no
/// memory verdict.</summary>
public sealed class ServerNeedsCatalog
{
    public IReadOnlyDictionary<string, ServerNeedRow> Versions { get; init; } = new Dictionary<string, ServerNeedRow>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, long> Clients { get; init; } = new Dictionary<string, long>(StringComparer.Ordinal);

    public static ServerNeedsCatalog Empty { get; } = new();

    /// <summary>The row of a server version, null when the file has none (or none that could be read).</summary>
    public ServerNeedRow? For(string? version) => Versions.TryGetValue((version ?? "").Trim(), out var r) ? r : null;

    /// <summary>The memory of a game version's client, null when the file names none.</summary>
    public long? Client(string? version) => Clients.TryGetValue((version ?? "").Trim(), out var b) ? b : null;
}

/// <summary>Reads <c>config/server-needs.json</c> — data read by id (adding a version adds its row).</summary>
public static class ServerNeeds
{
    public static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "config", "server-needs.json");

    /// <summary>The file at <paramref name="path"/> (the shipped one by default); an empty catalogue when it is
    /// missing or cannot be read (logged). Never throws.</summary>
    public static ServerNeedsCatalog Load(string? path = null)
    {
        string p = string.IsNullOrWhiteSpace(path) ? ShippedPath : path!;
        try { return File.Exists(p) ? Parse(File.ReadAllText(p)) : ServerNeedsCatalog.Empty; }
        catch (Exception ex)
        {
            Log.Info($"server needs {p} cannot be read ({ex.Message}) -- no memory verdict");
            return ServerNeedsCatalog.Empty;
        }
    }

    /// <summary>
    /// The catalogue in <paramref name="json"/>: <c>{"versions": {"&lt;id&gt;": {"ram", "ramPathfinding", "images"}},
    /// "clients": {"&lt;game id&gt;": bytes}}</c>. A row is taken only whole: <c>ram</c> a positive JSON integer,
    /// <c>ramPathfinding</c> one at least as large, <c>images</c> null or an integer ≥ 0; a client a positive
    /// integer. Anything else is left out. Throws only on text that is no JSON.
    /// </summary>
    public static ServerNeedsCatalog Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var versions = new Dictionary<string, ServerNeedRow>(StringComparer.Ordinal);
        var clients = new Dictionary<string, long>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) return ServerNeedsCatalog.Empty;
        if (root.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Object)
            foreach (var v in vs.EnumerateObject())
            {
                if (v.Value.ValueKind != JsonValueKind.Object) continue;
                long? ram = Int(v.Value, "ram"), pf = Int(v.Value, "ramPathfinding");
                if (ram is not > 0 || pf is null || pf < ram) continue;
                long? images = null;
                if (v.Value.TryGetProperty("images", out var im))
                {
                    if (im.ValueKind == JsonValueKind.Number && im.TryGetInt64(out long n) && n >= 0) images = n;
                    else if (im.ValueKind != JsonValueKind.Null) continue;
                }
                versions[v.Name] = new ServerNeedRow(ram.Value, pf.Value, images);
            }
        if (root.TryGetProperty("clients", out var cs) && cs.ValueKind == JsonValueKind.Object)
            foreach (var c in cs.EnumerateObject())
                if (c.Value.ValueKind == JsonValueKind.Number && c.Value.TryGetInt64(out long b) && b > 0) clients[c.Name] = b;
        return new ServerNeedsCatalog { Versions = versions, Clients = clients };
    }

    private static long? Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long n) ? n : null;
}
