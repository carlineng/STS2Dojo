using System;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Nodes;
using STS2Dojo.STS2DojoCode.Reconstruction;

namespace STS2Dojo.STS2DojoCode.SeedSharing;

/// <summary>
/// Launches an imported/saved shared fight (§12e step 4 → Start): same throwaway-run pipeline as a
/// run-history replay, but the loadout comes straight off the payload (no reconstruction) and
/// <see cref="SharedFightPayload.ToLaunchOptions"/> pins the captured seed + RNG counters, so the
/// importer gets the exporter's exact opening hand/intents — and Try Again replays it identically
/// (§12 decision 2026-07-04).
/// </summary>
public static class SharedFightLauncher
{
    /// <summary>The full §12e refusal chain for a decoded payload, in gate order: §12c compatibility
    /// (build/schema) first, then §6 content resolve (character/encounter/cards/relics/potions against
    /// the live ModelDb). Null = importable. Structural validity was already enforced by the codec.</summary>
    public static string? GetImportRefusal(SharedFightPayload payload)
    {
        string? gateRefusal = SharedFightGate.GetRefusal(payload, SharedFightExporter.CurrentGameBuildId);
        if (gateRefusal != null)
        {
            return gateRefusal;
        }

        DojoContentEligibilityResult eligibility = DojoContentEligibility.Validate(
            LiveDojoContentResolver.Instance,
            payload.EncounterId,
            characterId: payload.CharacterId,
            cardIds: payload.Deck.Select(c => c.Id).OfType<ModelId>(),
            relicIds: payload.Relics.Select(r => r.Id).OfType<ModelId>(),
            potionIds: payload.Potions.Select(p => p.Id).OfType<ModelId>());
        if (!eligibility.IsEligible)
        {
            return "This fight needs content that isn't loaded here: " +
                   string.Join(", ", eligibility.MissingContent.Select(m => $"{m.Kind} {m.Id}")) + ".";
        }

        return null;
    }

    public static async Task Launch(SharedFightPayload payload)
    {
        try
        {
            NGame? game = NGame.Instance;
            if (game == null)
            {
                MainFile.Logger.Error("[STS2Dojo] NGame.Instance is null; cannot launch a shared fight.");
                return;
            }

            // Callers gate before offering Start, but re-check here so a stale library entry (content
            // unloaded since the list was built) refuses cleanly instead of crashing mid-launch.
            string? refusal = GetImportRefusal(payload);
            if (refusal != null)
            {
                MainFile.Logger.Error("[STS2Dojo] Shared fight refused at launch: " + refusal);
                return;
            }

            CharacterModel character = ModelDb.GetById<CharacterModel>(payload.CharacterId!);

            await DojoLaunch.LaunchThrowawayRun(
                game, character, payload.Ascension, payload.EncounterId!,
                mutate: runState =>
                {
                    DojoLoadoutApplier.Apply(
                        runState,
                        runState.Players[0],
                        payload.Deck,
                        payload.Relics,
                        payload.Potions,
                        payload.MaxPotionSlots,
                        payload.Gold,
                        payload.MaxHp,
                        payload.CurrentHp);

                    MainFile.Logger.Info(
                        $"[STS2Dojo] Shared fight '{payload.Title}': '{payload.EncounterId!.Entry}' " +
                        $"seed={payload.Seed} deck={payload.Deck.Count} relics={payload.Relics.Count} " +
                        $"hp={payload.CurrentHp}/{payload.MaxHp} gold={payload.Gold} ascension={payload.Ascension}.");
                },
                payload.ToLaunchOptions());
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Shared fight launch failed: " + e);
        }
    }
}
