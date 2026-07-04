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
using MegaCrit.Sts2.Core.Nodes.Audio;
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
        // Match the base game's run-start behavior (main-menu/custom/daily flows all stop menu music before
        // launching) and also clear any lingering end-of-combat sting that might still be playing when the
        // player hits Try Again from the completion screen. Run music and global music are managed by
        // separate systems, so stop both to guarantee the new fight starts with a single active track.
        NRun.Instance?.RunMusicController.StopMusic();
        NAudioManager.Instance?.StopMusic();

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

        // Boss music fix: RunState.CreateForNewRun always starts CurrentActIndex at 0, and nothing else
        // ever corrects it here (EnterAct is the only other writer, and it's deliberately skipped above).
        // NRun._Ready() calls RunMusicController.UpdateMusic() the moment the scene is created below, which
        // loads the FMOD bank for runState.Act (= Acts[CurrentActIndex]) - if that's still Act 1 while
        // launching an Act 2/3 fight, the boss's specific CustomBgm event (CombatManager.StartCombatInternal
        // -> PlayCustomMusic) isn't in any loaded bank and silently fails to play. Elite/normal fights don't
        // show the symptom because their music is just a generic "Elite"/"Normal" progress parameter on
        // whatever ambient track is already loaded, not a specific per-boss event. Fix: point CurrentActIndex
        // (and swap in the correct act variant - see FixActForEncounter) at whichever act actually owns this
        // encounter before the scene exists.
        FixActForEncounter(runState, encounter);

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

    /// <summary>Finds which registered <see cref="ActModel"/> variant actually owns
    /// <paramref name="encounter"/> and installs it into the run state at its natural slot index.
    ///
    /// Some act slots have more than one live variant - <c>ModelDb.ActsByIndex[1]</c> holds both
    /// <c>Underdocks</c> and <c>Hive</c> as Act 2 - but <c>RunState.CreateForNewRun</c> (see the caller)
    /// always seeds <c>Acts</c> from <c>ActModel.GetDefaultList()</c>, i.e. whichever variant per slot has
    /// <c>IsDefault == true</c>. If the historical fight being replayed used the OTHER variant, that
    /// variant's <c>AllEncounters</c> never appears in <c>runState.Acts</c> at all, so searching only
    /// <c>runState.Acts</c> (as an earlier version of this fix did) silently finds no match for exactly
    /// those fights and leaves <c>CurrentActIndex</c> wrong. Searching every registered variant via
    /// <c>ModelDb.ActsByIndex</c> and swapping the correct one in via <c>RunState.SetActDebug</c>
    /// (replaces <c>Acts[CurrentActIndex]</c>, so <c>CurrentActIndex</c> is set first) fixes it regardless
    /// of which variant the original run rolled. <c>AllEncounters</c> is a deterministic, RNG-free content
    /// list, so it's safe to query before the act has generated any rooms.
    ///
    /// <c>candidate.ToMutable()</c> resets the clone's <c>_rooms</c> to an empty <c>RoomSet()</c>
    /// (<c>ActModel.DeepCloneFields</c>) - every OTHER act already had <c>RunManager.GenerateRooms()</c>
    /// run over it inside <c>SetUpNewSingleplayer</c> (before this method runs), which is what populates
    /// <c>_rooms.Boss</c>/<c>Ancient</c>/etc.; this fresh clone never gets that call. Leaving it empty
    /// previously made the launch throw the first time anything downstream touched a
    /// <c>_rooms</c>-derived property (e.g. <c>BossEncounter</c>) on this act - caught by the try/catch in
    /// <see cref="LaunchInternal"/>, so the failure was silent from the player's perspective (no fight, no
    /// error, just back to the Dojo screen). Calling <c>GenerateRooms</c> here (same three args
    /// <c>RunManager.GenerateRooms()</c> passes) mirrors what already happened to every other act and
    /// leaves this one just as populated; its actual random picks don't matter since the Dojo forces a
    /// specific, already-resolved <paramref name="encounter"/> into combat regardless of what the act
    /// itself would have rolled.</summary>
    private static void FixActForEncounter(RunState runState, EncounterModel encounter)
    {
        IReadOnlyList<IReadOnlyList<ActModel>> actsByIndex = ModelDb.ActsByIndex;
        for (int i = 0; i < actsByIndex.Count; i++)
        {
            foreach (ActModel candidate in actsByIndex[i])
            {
                if (candidate.AllEncounters.Any(e => e.Id == encounter.Id))
                {
                    ActModel mutableAct = candidate.ToMutable();
                    mutableAct.GenerateRooms(runState.Rng.UpFront, runState.UnlockState, runState.Players.Count > 1);
                    runState.CurrentActIndex = i;
                    runState.SetActDebug(mutableAct);
                    return;
                }
            }
        }
    }
}
