using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace STS2Dojo.STS2DojoCode.SeedSharing;

/// <summary>
/// The Replay Fight modal's output: optional overrides over a saved fight's captured parameters — a
/// different seed and/or edited relic/card <c>[SavedProperty]</c> state. <see cref="BuildCustomizedPayload"/>
/// produces a customized COPY of the payload; the library entry's payload is never mutated (it's cached in
/// the Saved Fights view and re-persisted verbatim by Edit — stamping adjustments onto it would silently
/// corrupt the saved fight).
///
/// Seed semantics: a changed seed drops the captured RNG counters (both <c>LoadFromSerializable</c> methods
/// throw on a seed mismatch by design), so the launch derives fresh counters from the new seed — still
/// fully deterministic per §12a (no wall-clock RNG, <c>UnlockState.all</c> pools), so every attempt of the
/// customized fight repeats identically; it just no longer matches the exporter's original draws.
///
/// Relic/card overlays are keyed like <see cref="DojoStateAdjustments"/> (relic id; card id + occurrence
/// index in deck order) and hold ONLY the edited properties (a <c>DojoStateSpec.BuildProps</c> blob);
/// <see cref="MergeProps"/> lays them over the entry's captured props so unrelated captured state — e.g.
/// a secondary counter the modal deliberately doesn't expose — survives the edit.
///
/// Pure data in/data out (no Godot/game-singleton dependency) so the §5b test harness compiles it
/// against DTO doubles.
/// </summary>
public sealed class SavedFightCustomization
{
    /// <summary>Replacement seed, already canonicalized by the caller. Null (or equal to the payload's
    /// captured seed) means "keep the captured seed AND its RNG counters".</summary>
    public string? SeedOverride { get; set; }

    private readonly Dictionary<ModelId, SavedProperties> _relicOverlays = new();
    private readonly Dictionary<(ModelId Id, int Occurrence), SavedProperties> _cardOverlays = new();

    public int StateEditCount => _relicOverlays.Count + _cardOverlays.Count;

    public void SetRelic(ModelId relicId, SavedProperties overlay) => _relicOverlays[relicId] = overlay;

    public void SetCard(ModelId cardId, int occurrence, SavedProperties overlay) =>
        _cardOverlays[(cardId, occurrence)] = overlay;

    /// <summary>True when launching this customization would differ from launching
    /// <paramref name="source"/> as-is.</summary>
    public bool ChangesAnything(SharedFightPayload source) =>
        StateEditCount > 0 || (SeedOverride != null && SeedOverride != source.Seed);

    /// <summary>Builds the payload to actually launch: a copy of <paramref name="source"/> with the seed
    /// override and relic/card state overlays applied. Unmodified deck/relic entries are shared by
    /// reference (the launch path only reads them); modified entries are cloned so the source payload
    /// stays byte-identical to what's on disk.</summary>
    public SharedFightPayload BuildCustomizedPayload(SharedFightPayload source)
    {
        bool seedChanged = SeedOverride != null && SeedOverride != source.Seed;

        var relics = new List<SerializableRelic>(source.Relics.Count);
        foreach (SerializableRelic relic in source.Relics)
        {
            if (relic?.Id != null && _relicOverlays.TryGetValue(relic.Id, out SavedProperties? relicOverlay))
            {
                relics.Add(new SerializableRelic
                {
                    Id = relic.Id,
                    Props = MergeProps(relic.Props, relicOverlay),
                    FloorAddedToDeck = relic.FloorAddedToDeck,
                });
            }
            else
            {
                relics.Add(relic!);
            }
        }

        var deck = new List<SerializableCard>(source.Deck.Count);
        var occurrences = new Dictionary<ModelId, int>();
        foreach (SerializableCard card in source.Deck)
        {
            int occurrence = 0;
            if (card?.Id != null)
            {
                occurrences.TryGetValue(card.Id, out occurrence);
                occurrences[card.Id] = occurrence + 1;
            }
            if (card?.Id != null && _cardOverlays.TryGetValue((card.Id, occurrence), out SavedProperties? cardOverlay))
            {
                deck.Add(new SerializableCard
                {
                    Id = card.Id,
                    CurrentUpgradeLevel = card.CurrentUpgradeLevel,
                    Enchantment = card.Enchantment,
                    Props = MergeProps(card.Props, cardOverlay),
                    FloorAddedToDeck = card.FloorAddedToDeck,
                });
            }
            else
            {
                deck.Add(card!);
            }
        }

        return new SharedFightPayload
        {
            SchemaVersion = source.SchemaVersion,
            GameBuildId = source.GameBuildId,
            ModVersion = source.ModVersion,
            Title = source.Title,
            Comment = source.Comment,
            Author = source.Author,
            CreatedUtc = source.CreatedUtc,
            CharacterId = source.CharacterId,
            Ascension = source.Ascension,
            EncounterId = source.EncounterId,
            Seed = seedChanged ? SeedOverride! : source.Seed,
            // A new seed invalidates the captured counters (seed-mismatch throw); fresh counters derive
            // deterministically from the new seed at launch. Same seed keeps the captured counters.
            RunRng = seedChanged ? null : source.RunRng,
            PlayerRng = seedChanged ? null : source.PlayerRng,
            Deck = deck,
            Relics = relics,
            Potions = new List<SerializablePotion>(source.Potions),
            MaxPotionSlots = source.MaxPotionSlots,
            CurrentHp = source.CurrentHp,
            MaxHp = source.MaxHp,
            Gold = source.Gold,
        };
    }

    // ------------------------------------------------------------------ props helpers

    /// <summary>Reads an int [SavedProperty] out of a captured props blob (how the modal seeds each state
    /// row with the fight's captured value). False when the blob is null or lacks the property — the
    /// caller falls back to the spec default, which is what the live model would hold in that case.</summary>
    public static bool TryGetInt(SavedProperties? props, string name, out int value)
    {
        if (props?.ints != null)
        {
            foreach (SavedProperties.SavedProperty<int> entry in props.ints)
            {
                if (entry.name == name)
                {
                    value = entry.value;
                    return true;
                }
            }
        }
        value = 0;
        return false;
    }

    public static bool TryGetBool(SavedProperties? props, string name, out bool value)
    {
        if (props?.bools != null)
        {
            foreach (SavedProperties.SavedProperty<bool> entry in props.bools)
            {
                if (entry.name == name)
                {
                    value = entry.value;
                    return true;
                }
            }
        }
        value = false;
        return false;
    }

    /// <summary>Lays <paramref name="overlay"/>'s int/bool entries over a copy of
    /// <paramref name="captured"/>: same-name entries are replaced, new ones appended, and every other
    /// captured list (strings, model ids, cards, …) is carried across untouched. Overlays only ever
    /// carry ints/bools — every editable <see cref="DojoStateSpec"/> builds one of those two.</summary>
    public static SavedProperties MergeProps(SavedProperties? captured, SavedProperties overlay)
    {
        var merged = new SavedProperties
        {
            ints = captured?.ints == null ? null : new List<SavedProperties.SavedProperty<int>>(captured.ints),
            bools = captured?.bools == null ? null : new List<SavedProperties.SavedProperty<bool>>(captured.bools),
            strings = captured?.strings,
            intArrays = captured?.intArrays,
            modelIds = captured?.modelIds,
            cards = captured?.cards,
            cardArrays = captured?.cardArrays,
        };

        if (overlay.ints != null)
        {
            merged.ints ??= new List<SavedProperties.SavedProperty<int>>();
            foreach (SavedProperties.SavedProperty<int> entry in overlay.ints)
            {
                ReplaceOrAdd(merged.ints, entry);
            }
        }
        if (overlay.bools != null)
        {
            merged.bools ??= new List<SavedProperties.SavedProperty<bool>>();
            foreach (SavedProperties.SavedProperty<bool> entry in overlay.bools)
            {
                ReplaceOrAdd(merged.bools, entry);
            }
        }
        return merged;
    }

    private static void ReplaceOrAdd<T>(
        List<SavedProperties.SavedProperty<T>> list, SavedProperties.SavedProperty<T> entry)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].name == entry.name)
            {
                list[i] = entry;
                return;
            }
        }
        list.Add(entry);
    }
}
