namespace Relic.Core.Server;

/// <summary>
/// Maps the "Game commands" UI actions to the concrete hk4e "GM Talk" command strings the muipserver
/// understands (sent as cmd=1116 gmTalk, payload uid+msg).
/// Pure/deterministic — the network + signing happen in the Linux agent.
/// </summary>
public static class GmCommands
{
    // Common item ids
    public const int MoraId = 202;
    public const int PrimogemId = 201;
    public const int GenesisCrystalId = 203;
    public const int AdventureExpItemId = 102;
    public const int HeroWitId = 104003; // Hero's Wit — the character EXP book (20,000 EXP each)
    public const int ResinId = 106; // Original Resin

    /// <summary>The most Original Resin one command gives: the hard maximum of the server's resin row
    /// (ConstValueData 134), which is also the most a player can hold. The server refuses whole any add
    /// that would put the player above it, so a larger amount could never be granted.</summary>
    public const int ResinMax = 2000;

    /// <summary>Grant adventure EXP.</summary>
    public static string GrantExp(long amount) => $"item add {AdventureExpItemId} {Positive(amount)}";

    /// <summary>Add a character/avatar by id (e.g. 10000002 = Ayaka).</summary>
    public static string AddAvatar(int avatarId) => $"avatar add {avatarId}";

    /// <summary>Add a weapon by id at a level and ascension (promote) level.</summary>
    public static string AddWeapon(int weaponId, int level = 1, int promoteLevel = 0)
        => $"equip add {weaponId} {Clamp(level, 1, 90)} {Clamp(promoteLevel, 0, 6)}";

    /// <summary>Set adventure rank.</summary>
    public static string SetAdventureRank(int level) => $"player level {Clamp(level, 1, 60)}";

    /// <summary>Give Mora.</summary>
    public static string GiveMora(long amount) => GiveItem(MoraId, amount);

    /// <summary>Give Primogems (item 201 — the guide's own first example, `item add 201 10000`).</summary>
    public static string GivePrimogems(long amount) => GiveItem(PrimogemId, amount);

    /// <summary>Give Genesis Crystals (item 203).</summary>
    public static string GiveGenesisCrystals(long amount) => GiveItem(GenesisCrystalId, amount);

    /// <summary>Give Original Resin (item 106), never more than <see cref="ResinMax"/> in one command.
    /// The amount is added to what the player holds — the server refuses the add when the total would
    /// pass that same maximum — and the regeneration cap plays no part in it.</summary>
    public static string GiveResin(long amount) => GiveItem(ResinId, Math.Min(amount, ResinMax));

    /// <summary>
    /// Grant a character skin. Costumes are ITEMS (ids 340000+ in AvatarCostumeExcelConfigData) and
    /// the gameserver's item module calls addCostume when one lands ("addCostume failed. costume id"
    /// lives among the item-use strings) — so this is one `item add`, count 1. The skin appears in
    /// the character's dressing screen; 1.6 carries only 340000 (Barbara) and 340001 (Jean).
    /// </summary>
    public static string GiveCostume(int costumeItemId) => GiveItem(costumeItemId, 1);

    /// <summary>Give an arbitrary item/material.</summary>
    public static string GiveItem(int itemId, long amount) => $"item add {itemId} {Positive(amount)}";

    /// <summary>
    /// There is no whole-set command — an artifact set is 5 pieces (flower/plume/sands/goblet/circlet),
    /// each added at DISPLAY level <paramref name="level"/>. NOTE: "equip add" puts the piece straight
    /// ONTO the avatar the player is controlling in game, replacing whatever it wears in that slot.
    /// </summary>
    public static IReadOnlyList<string> GiveArtifactSet(IEnumerable<int> pieceIds, int level = 20)
        => pieceIds.Select(id => GiveArtifactPiece(id, level)).ToList();

    /// <summary>
    /// One artifact piece via "equip add". <paramref name="level"/> is the DISPLAY level (+0..+20).
    /// The game counts reliquary levels from 1 (+0 = wire 1, +20 = wire 21), so the wire value is
    /// display+1 — sending the display value raw gives +19 instead of +20.
    /// Weapons are unaffected: their display and wire levels coincide (1..90).
    /// </summary>
    public static string GiveArtifactPiece(int pieceId, int level = 20)
        => $"equip add {pieceId} {Clamp(level, 0, 20) + 1}";

    /// <summary>
    /// The same 5 pieces sent through "item add" instead: they land in the INVENTORY, unequipped,
    /// at +0 — the UI's opt-in mode for a set that must not overwrite what the in-game avatar
    /// wears. "item add" takes no level parameter (which is why equip-add is the UI default —
    /// it is the only path to leveled pieces); the player upgrades these in game.
    /// </summary>
    public static IReadOnlyList<string> GiveArtifactSetToInventory(IEnumerable<int> pieceIds)
        => pieceIds.Select(id => $"item add {id} 1").ToList();

    /// <summary>Ascend (set the promote level of) the avatar the player currently controls in game.
    /// 6 = fully ascended (level cap 90). Handler: setBreakLevel in the gameserver binary.</summary>
    public static string BreakCurrentAvatar(int promoteLevel) => $"break {Clamp(promoteLevel, 0, 6)}";

    /// <summary>Set the LEVEL of the avatar the player currently controls in game. Handler:
    /// setLevel, registered right beside setBreakLevel in the gameserver binary ("invalid level 0"
    /// is its parse error — level must be ≥ 1). Send AFTER <see cref="BreakCurrentAvatar"/>: the
    /// ascension raises the cap the level then climbs to.</summary>
    public static string SetCurrentAvatarLevel(int level) => $"level {Clamp(level, 1, 90)}";

    /// <summary>
    /// Unlock every constellation (C6) of the avatar the player currently controls in game. In the
    /// server's own vocabulary a constellation is an avatar "talent" — the handler is procTalent
    /// (forceUnlockAllTalent, "unlock all talents succeed" in the gameserver binary), reached by the
    /// GM guide's literal "talent unlock all". NOT the same thing as a skill/proud-skill upgrade,
    /// which the player-facing UI also calls a talent.
    /// </summary>
    public static string UnlockAllConstellations() => "talent unlock all";

    /// <summary>Hero's Wit EXP books, applied by the player from the character screen — the manual
    /// fallback for a server where "level" does not apply. 500 books = 10M EXP, enough for 1→90
    /// (leveling still costs Mora in game).</summary>
    public static string GiveHeroWit(int count) => GiveItem(HeroWitId, count);

    /// <summary>Infinite stamina for the player's session — "stamina infinite on/off" is a literal
    /// in the gameserver binary (changeStamina handler).</summary>
    public static string StaminaInfinite(bool on) => $"stamina infinite {(on ? "on" : "off")}";

    /// <summary>Invincibility (infinite HP) for the player's avatars — setWudi in the gameserver
    /// binary answers "avatar wudi on/off"; the command grammar is the GIO guide's
    /// "wudi global (avatar|monster) (on|off)".</summary>
    public static string WudiAvatar(bool on) => $"wudi global avatar {(on ? "on" : "off")}";

    private static long Positive(long n) => n < 1 ? 1 : n;
    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}
