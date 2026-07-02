using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Shared launch sequence for the Dojo's throwaway NON-SAVING runs. Replicates <c>NGame.StartRun</c>
/// manually (instead of calling the <c>NGame.StartNewSingleplayerRun</c> convenience wrapper) so that
/// <see cref="LaunchThrowawayRun"/>'s caller-supplied <c>mutate</c> callback can replace the player's
/// deck/relics/HP/gold BEFORE <c>NRun</c>'s scene is created. This matters because <c>NRelicInventory</c>
/// snapshots <c>player.Relics</c> the moment the scene is ready (marking those relics visible); anything
/// added afterward via <c>Player.AddRelicInternal</c> renders invisible (alpha=0) until an explicit
/// "acquired" fade-in animation plays, which nothing in the mod calls. See CLAUDE.md §5a point 6.
///
/// <c>EnterAct(0)</c> (map/act entry) is deliberately never called — <c>RunManager.EnterRoomDebug</c> only
/// requires <c>RunManager.State != null</c> (set by <c>SetUpNewSingleplayer</c>) and short-circuits
/// map/act lookups when given a concrete <c>EncounterModel</c>, exactly like the built-in <c>fight</c>
/// console command already does with no act ever entered.
/// </summary>
public static class DojoLaunch
{
    private sealed record LaunchRequest(
        NGame Game, CharacterModel Character, int AscensionLevel, ModelId EncounterId, Action<RunState> Mutate);

    private static LaunchRequest? _lastRequest;

    /// <summary>Launches a fresh throwaway, non-saving run and jumps straight into the given encounter.
    /// <paramref name="mutate"/> replaces the player's deck/relics/HP/gold; it runs after the run's
    /// synthetic starting inventory + starting-relic <c>AfterObtained()</c> hooks have fired, but before
    /// the run's scene is created — see the class docs for why that ordering is load-bearing.</summary>
    public static async Task<RunState> LaunchThrowawayRun(
        NGame game, CharacterModel character, int ascensionLevel, ModelId encounterId, Action<RunState> mutate)
    {
        _lastRequest = new LaunchRequest(game, character, ascensionLevel, encounterId, mutate);
        return await LaunchInternal(game, character, ascensionLevel, encounterId, mutate);
    }

    /// <summary>"Try again": relaunches the last <c>LaunchThrowawayRun</c> request with a fresh encounter
    /// RNG seed and fresh combat RNG (CLAUDE.md §3 — exact RNG replay is impossible; fresh is the
    /// deliberate design). Follows the same pattern as <c>Debug/FileDropHandler.cs</c>'s "load a different
    /// run without returning to the main menu": <c>RunManager.CleanUp()</c> then relaunch directly.</summary>
    public static async Task<RunState?> TryAgain()
    {
        if (_lastRequest is not { } request)
        {
            MainFile.Logger.Error("[STS2Dojo] No previous Dojo launch to retry.");
            return null;
        }

        return await LaunchInternal(
            request.Game, request.Character, request.AscensionLevel, request.EncounterId, request.Mutate);
    }

    private static async Task<RunState> LaunchInternal(
        NGame game, CharacterModel character, int ascensionLevel, ModelId encounterId, Action<RunState> mutate)
    {
        if (RunManager.Instance.IsInProgress)
        {
            RunManager.Instance.CleanUp();
        }

        // Fresh EncounterModel + fresh RNG every attempt, including on TryAgain() — CLAUDE.md §3.
        EncounterModel encounter = ModelDb.GetById<EncounterModel>(encounterId).ToMutable();
        encounter.DebugRandomizeRng();

        string seed = SeedHelper.GetRandomSeed();
        UnlockState unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
        RunState runState = RunState.CreateForNewRun(
            new List<Player> { Player.CreateForNewRun(character, unlockState, netId: 1uL) },
            ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(),
            Array.Empty<ModifierModel>(),
            GameMode.Standard,
            ascensionLevel,
            seed);

        RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: false);

        // Kills the replays/latest.mcr write leak at the source (every write site in CombatReplayWriter /
        // CombatManager / RunManager.CleanUp checks IsEnabled first) — no Harmony patch needed for this one.
        RunManager.Instance.CombatReplayWriter.IsEnabled = false;

        DojoRunRegistry.MarkAsDojo(runState);

        try
        {
            using (new NetLoadingHandle(RunManager.Instance.NetService))
            {
                await PreloadManager.LoadRunAssets(runState.Players.Select(p => p.Character));
                await PreloadManager.LoadActAssets(runState.Acts[0]);
                await RunManager.Instance.FinalizeStartingRelics();
                RunManager.Instance.Launch();

                mutate(runState);

                game.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
            }

            await EnterEncounter(encounter);
            return runState;
        }
        catch
        {
            // mutate(...) (e.g. RunReconstructor content resolution) or the asset/scene setup above can
            // throw with a live, never-entered RunState already installed via SetUpNewSingleplayer. Left
            // in place, RunManager.State stays occupied: RunManager.SetUpNewSingleplayer throws
            // "State is already set." for ANY subsequent run — Dojo or a real player run — until
            // something else happens to call CleanUp() first. Recover immediately rather than leaving the
            // session poisoned; the caller's own catch block still logs the original exception.
            RunManager.Instance.CleanUp();
            throw;
        }
    }

    public static async Task EnterEncounter(EncounterModel encounter)
    {
        await RunManager.Instance.EnterRoomDebug(encounter.RoomType, MapPointType.Unassigned, encounter);
    }
}
