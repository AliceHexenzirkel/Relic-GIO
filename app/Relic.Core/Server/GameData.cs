using System.Text.Json;

namespace Relic.Core.Server;

/// <summary>
/// The content catalogue behind the GM command screen (from config/gamedata.json): characters,
/// 5★ weapons and 5★ artifact sets, each tagged with the first supported game version that carries
/// it. Two jobs: <see cref="ForVersion"/> feeds the pickers only what the selected version knows
/// about, and <see cref="ResolveAvatar"/>/<see cref="ResolveWeapon"/> turn a typed name or alias
/// into an id. Unknown names pass through unchanged (a raw id is always accepted), and artifact
/// SETS still go out as raw "pieceId level" input — the server has no whole-set command.
/// </summary>
public sealed class GameData
{
    public List<string> VersionOrder { get; set; } = new();
    public List<AvatarEntry> Avatars { get; set; } = new();
    public List<WeaponEntry> Weapons { get; set; } = new();
    public List<ArtifactSetEntry> ArtifactSets { get; set; } = new();

    public sealed class AvatarEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<string> Aliases { get; set; } = new();
        public int Rarity { get; set; }
        public string Element { get; set; } = "";
        public string Weapon { get; set; } = "";
        public string Since { get; set; } = "";
        public string Splash { get; set; } = "";
    }

    public sealed class WeaponEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<string> Aliases { get; set; } = new();
        public int Rarity { get; set; }
        public string Type { get; set; } = "";
        public string Since { get; set; } = "";
        public string Icon { get; set; } = "";
    }

    public sealed class ArtifactSetEntry
    {
        public int SetId { get; set; }
        public string Name { get; set; } = "";
        public List<string> Aliases { get; set; } = new();
        public int Rarity { get; set; }
        public string Since { get; set; } = "";
        public List<PieceEntry> Pieces { get; set; } = new();
    }

    public sealed class PieceEntry
    {
        public string Slot { get; set; } = "";
        public int Id { get; set; }
        public string Icon { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static GameData? _cached;
    private Dictionary<string, int>? _avatarLut;
    private Dictionary<string, int>? _weaponLut;

    /// <summary>The catalogue behind the command screen. <paramref name="path"/> is for the spike,
    /// which runs from its own output folder and would otherwise find no file at all and silently
    /// test an empty catalogue; an explicit path also bypasses the process-wide cache.</summary>
    public static GameData Load(string? path = null)
    {
        if (path is not null)
        {
            try { return Parse(File.ReadAllText(path)); }
            catch { return new GameData(); }
        }
        if (_cached is not null) return _cached;
        try
        {
            string? found = ResolvePath();
            _cached = found is not null ? Parse(File.ReadAllText(found)) : new GameData();
        }
        catch { _cached = new GameData(); }
        return _cached;
    }

    private static string? ResolvePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relic", "config", "gamedata.json"),
            Path.Combine(AppContext.BaseDirectory, "config", "gamedata.json"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Read one section at a time instead of binding the document in one go, so a section the file
    /// gets wrong costs only that section. Two shapes have to survive: the first schema shipped
    /// "avatars"/"weapons" as flat {name: id} objects, and an installed copy under %LOCALAPPDATA%
    /// outranks the one next to the exe — so a user who updates the app can still be on that file.
    /// </summary>
    private static GameData Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return new GameData();

        var data = new GameData
        {
            VersionOrder = Section<List<string>>(root, "versionOrder") ?? new(),
            Avatars = Section<List<AvatarEntry>>(root, "avatars") ?? new(),
            Weapons = Section<List<WeaponEntry>>(root, "weapons") ?? new(),
            ArtifactSets = Section<List<ArtifactSetEntry>>(root, "artifactSets") ?? new(),
        };
        foreach (var (name, aliases, id) in LegacyPairs(root, "avatars"))
            data.Avatars.Add(new AvatarEntry { Id = id, Name = name, Aliases = aliases, Rarity = 5 });
        foreach (var (name, aliases, id) in LegacyPairs(root, "weapons"))
            data.Weapons.Add(new WeaponEntry { Id = id, Name = name, Aliases = aliases, Rarity = 5 });
        return data;
    }

    private static T? Section<T>(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Array) return default;
        try { return el.Deserialize<T>(JsonOpts); }
        catch (JsonException) { return default; }
    }

    /// <summary>
    /// One entry per id out of a legacy {name: id} map, or nothing at all when the property is
    /// already the current array shape. The old file listed several spellings of the same id as
    /// separate keys, so the longest spelling becomes the display name and the rest stay searchable
    /// as aliases — otherwise the picker would show the same character once per spelling.
    /// </summary>
    private static IEnumerable<(string Name, List<string> Aliases, int Id)> LegacyPairs(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var map) || map.ValueKind != JsonValueKind.Object)
            yield break;
        var byId = new Dictionary<int, List<string>>();
        foreach (var p in map.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var id)) continue;
            if (!byId.TryGetValue(id, out var names)) byId[id] = names = new List<string>();
            names.Add(p.Name);
        }
        foreach (var (id, names) in byId)
        {
            names.Sort((a, b) => b.Length.CompareTo(a.Length));
            yield return (names[0], names.Skip(1).ToList(), id);
        }
    }

    /// <summary>
    /// Everything the given game version can actually grant: an entry counts as available once its
    /// "since" version has been reached. Deliberately permissive — an unknown version, an empty
    /// versionOrder or an entry with an unrecognised "since" all fall through to "include it", so a
    /// hand-edited catalogue can never silently empty the pickers.
    /// </summary>
    public object ForVersion(string version)
    {
        int target = VersionOrder.IndexOf((version ?? "").Trim());
        bool Available(string since)
        {
            if (target < 0) return true;
            int rank = VersionOrder.IndexOf(since ?? "");
            return rank < 0 || rank <= target;
        }

        return new
        {
            avatars = Avatars.Where(a => Available(a.Since)).ToList(),
            weapons = Weapons.Where(w => Available(w.Since)).ToList(),
            artifactSets = ArtifactSets.Where(s => Available(s.Since)).ToList(),
        };
    }

    /// <summary>Resolve a name or numeric id to an avatar id string (returns input unchanged if unknown).</summary>
    public string ResolveAvatar(string nameOrId) =>
        Resolve(_avatarLut ??= BuildLut(Avatars.Select(a => (a.Id, a.Name, a.Aliases))), nameOrId);

    public string ResolveWeapon(string nameOrId) =>
        Resolve(_weaponLut ??= BuildLut(Weapons.Select(w => (w.Id, w.Name, w.Aliases))), nameOrId);

    /// <summary>
    /// Name and aliases → id, across ALL versions: the typed box is the power user's escape hatch,
    /// so it must not be version-gated the way the pickers are.
    /// </summary>
    private static Dictionary<string, int> BuildLut(IEnumerable<(int Id, string Name, List<string> Aliases)> rows)
    {
        var lut = new Dictionary<string, int>();
        foreach (var (id, name, aliases) in rows)
            // "aliases": null in a hand-edited catalogue must not take the whole lookup down.
            foreach (var key in (aliases ?? new List<string>()).Prepend(name))
                if (!string.IsNullOrWhiteSpace(key))
                    lut.TryAdd(key.Trim().ToLowerInvariant(), id);
        return lut;
    }

    private static string Resolve(Dictionary<string, int> map, string input)
    {
        input = input.Trim();
        if (input.Length == 0 || int.TryParse(input, out _)) return input; // already an id (or empty)
        return map.TryGetValue(input.ToLowerInvariant(), out var id) ? id.ToString() : input;
    }
}
