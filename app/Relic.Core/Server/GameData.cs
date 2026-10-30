using System.Text.Json;

namespace Relic.Core.Server;

/// <summary>
/// The content catalogue behind the GM command screen (from config/gamedata.json): characters,
/// 4★ and 5★ weapons and artifact sets, each tagged with the first supported game version that carries
/// it — and the skins, a table of this class tagged the same way (<see cref="Costumes"/>).
/// Two jobs: <see cref="ForVersion"/> feeds the pickers only what the selected version knows
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

    /// <summary>A skin: the item id the server turns into a costume (<see cref="GmCommands.GiveCostume"/>),
    /// the character that wears it and the first game version that carries it.</summary>
    public sealed record CostumeEntry(int Id, string Character, string Name, string Since);

    /// <summary>
    /// The skins the command screen offers. The ids are those of AvatarCostumeExcelConfigData in the dumps
    /// the catalogue is built on; the character of each is written by hand, because the dumps from 2.8 on
    /// have that field's name obfuscated. Each row names the version it first exists in, like every other
    /// entry of the catalogue, and <see cref="ForVersion"/> filters the list by the same rule. The rows
    /// are listed as the skins came out: <see cref="CostumeOrder"/> reads the order of their versions
    /// off them.
    /// </summary>
    public static readonly IReadOnlyList<CostumeEntry> Costumes =
    [
        new(340000, "Barbara", "Summertime Sparkle", "1.6"),
        new(340001, "Jean", "Sea Breeze Dandelion", "1.6"),
        new(340002, "Ningguang", "Orchid's Evening Gown", "2.8"),
        new(340003, "Keqing", "Opulent Splendor", "2.8"),
        new(340004, "Diluc", "Red Dead of Night", "2.8"),
        new(340005, "Fischl", "Ein Immernachtstraum", "2.8"),
        new(340006, "Jean", "Gunnhildr's Legacy", "2.8"),
        new(340007, "Amber", "100% Outrider", "2.8"),
        new(340008, "Mona", "Pact of Stars and Moon", "2.8"),
        new(340009, "Rosaria", "To the Church's Free Spirit", "2.8"),
    ];

    /// <summary>The versions <see cref="Costumes"/> names, in the order its rows first name them — the
    /// order of the versions that brought skins, known whatever the catalogue file holds.</summary>
    private static readonly List<string> CostumeOrder = Costumes.Select(c => c.Since).Distinct().ToList();

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
    /// hand-edited catalogue can never silently empty the pickers. The skins (<see cref="Costumes"/>) go
    /// through the same test: this is the one place that decides what a version has. Their table names
    /// its own versions in order (<see cref="CostumeOrder"/>), so a version it names is ranked by that
    /// order — a catalogue file without a version order, or one that leaves the version out, would
    /// otherwise hand it every skin, each a button for an item that version does not have. A version the
    /// table does not name is ranked by the catalogue's order, like the pickers. The answer also says
    /// whether the catalogue describes the version at all (<c>covered</c>, <see cref="Covers"/>).
    /// </summary>
    public object ForVersion(string version)
    {
        string asked = (version ?? "").Trim();
        var skinOrder = CostumeOrder.Contains(asked) ? CostumeOrder : VersionOrder;
        return new
        {
            covered = Covers(asked),
            avatars = Avatars.Where(a => Available(VersionOrder, asked, a.Since)).ToList(),
            weapons = Weapons.Where(w => Available(VersionOrder, asked, w.Since)).ToList(),
            artifactSets = ArtifactSets.Where(s => Available(VersionOrder, asked, s.Since)).ToList(),
            costumes = Costumes.Where(c => Available(skinOrder, asked, c.Since)).ToList(),
        };
    }

    /// <summary>
    /// Does the catalogue describe <paramref name="version"/>: its version order names it, or the skins
    /// table does (<see cref="CostumeOrder"/>). A catalogue without a version order (the first schema, or no
    /// file) ties nothing to a version and describes every one, and so does a request that names none. The
    /// lists of <see cref="ForVersion"/> stay permissive whatever this answers; the command screen takes
    /// them only for a version the catalogue describes — for any other one they would be the content of the
    /// versions it does describe, offered under this version's name — and otherwise says the catalogue is
    /// not available for it and offers the numeric-id inputs.
    /// </summary>
    public bool Covers(string version)
    {
        string asked = (version ?? "").Trim();
        return asked.Length == 0 || VersionOrder.Count == 0 || VersionOrder.Contains(asked) || CostumeOrder.Contains(asked);
    }

    /// <summary>Has <paramref name="version"/> reached <paramref name="since"/> in <paramref name="order"/>?
    /// Yes as well whenever the order does not know one of the two.</summary>
    private static bool Available(List<string> order, string version, string since)
    {
        int target = order.IndexOf(version);
        if (target < 0) return true;
        int rank = order.IndexOf(since ?? "");
        return rank < 0 || rank <= target;
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
