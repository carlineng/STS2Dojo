using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

/// <summary>
/// Resolves the English display name embedded in an event's LocString variable (e.g. "Glowwater
/// Potion") to a potion <see cref="ModelId"/>. Needed because a few event outcomes (Drowning Beacon,
/// Ranwid the Elder, Stone of All Time — see RunReconstructor's per-floor event handling) grant/remove
/// a potion via Player.AddPotionInternal/RemovePotionInternal directly, bypassing
/// PotionCmd.TryToProcure/Discard (the only calls that log a potion id to potion_choices/potion_used/
/// potion_discarded) — so the id isn't in any structured delta, only this display-name text.
/// </summary>
public interface IPotionNameResolver
{
    bool TryResolveDisplayName(string displayName, out ModelId potionId);
}

/// <summary>
/// Default resolver: a static table of base-game potion display names actually observed in the
/// runfiles/ corpus, each cross-checked against the decompiled Potion model class list (class name ->
/// id follows PascalCase -> SCREAMING_SNAKE_CASE, e.g. GlowwaterPotion -> POTION.GLOWWATER_POTION).
/// Deliberately excludes any name we couldn't confidently verify this way (e.g. "Clarity Extract" has
/// no obviously-matching class name) rather than guess — an unresolved name just means that specific
/// event grant/removal stays a silent gap, which CLAUDE.md §3 explicitly accepts for potions.
///
/// This is the same "interface + not-yet-live-wired default" shape as IDojoContentResolver (CLAUDE.md
/// §9 item 4): a future live implementation could enumerate ModelDb's potions and resolve through the
/// current locale's actual Title text instead, which would be correct for any locale and for mod-added
/// potions too. Until that exists, this static table already covers the common cases (Drowning Beacon's
/// Glowwater Potion grant is by far the most frequent in the corpus).
/// </summary>
public sealed class KnownPotionNames : IPotionNameResolver
{
    public static readonly KnownPotionNames Instance = new();

    private static readonly Dictionary<string, ModelId> ByDisplayName = new()
    {
        ["Attack Potion"] = ModelId.Deserialize("POTION.ATTACK_POTION"),
        ["Block Potion"] = ModelId.Deserialize("POTION.BLOCK_POTION"),
        ["Dexterity Potion"] = ModelId.Deserialize("POTION.DEXTERITY_POTION"),
        ["Duplicator"] = ModelId.Deserialize("POTION.DUPLICATOR"),
        ["Energy Potion"] = ModelId.Deserialize("POTION.ENERGY_POTION"),
        ["Explosive Ampoule"] = ModelId.Deserialize("POTION.EXPLOSIVE_AMPOULE"),
        ["Fire Potion"] = ModelId.Deserialize("POTION.FIRE_POTION"),
        ["Flex Potion"] = ModelId.Deserialize("POTION.FLEX_POTION"),
        ["Focus Potion"] = ModelId.Deserialize("POTION.FOCUS_POTION"),
        ["Foul Potion"] = ModelId.Deserialize("POTION.FOUL_POTION"),
        ["Gigantification Potion"] = ModelId.Deserialize("POTION.GIGANTIFICATION_POTION"),
        ["Glowwater Potion"] = ModelId.Deserialize("POTION.GLOWWATER_POTION"),
        ["Heart of Iron"] = ModelId.Deserialize("POTION.HEART_OF_IRON"),
        ["Liquid Bronze"] = ModelId.Deserialize("POTION.LIQUID_BRONZE"),
        ["Liquid Memories"] = ModelId.Deserialize("POTION.LIQUID_MEMORIES"),
        ["Lucky Tonic"] = ModelId.Deserialize("POTION.LUCKY_TONIC"),
        ["Potion-Shaped Rock"] = ModelId.Deserialize("POTION.POTION_SHAPED_ROCK"),
        ["Powdered Demise"] = ModelId.Deserialize("POTION.POWDERED_DEMISE"),
        ["Power Potion"] = ModelId.Deserialize("POTION.POWER_POTION"),
        ["Regen Potion"] = ModelId.Deserialize("POTION.REGEN_POTION"),
        ["Skill Potion"] = ModelId.Deserialize("POTION.SKILL_POTION"),
        ["Soldier's Stew"] = ModelId.Deserialize("POTION.SOLDIERS_STEW"),
        ["Speed Potion"] = ModelId.Deserialize("POTION.SPEED_POTION"),
        ["Strength Potion"] = ModelId.Deserialize("POTION.STRENGTH_POTION"),
        ["Touch of Insanity"] = ModelId.Deserialize("POTION.TOUCH_OF_INSANITY"),
        ["Vulnerable Potion"] = ModelId.Deserialize("POTION.VULNERABLE_POTION"),
        ["Weak Potion"] = ModelId.Deserialize("POTION.WEAK_POTION"),
    };

    public bool TryResolveDisplayName(string displayName, out ModelId potionId) =>
        ByDisplayName.TryGetValue(displayName, out potionId!);
}
