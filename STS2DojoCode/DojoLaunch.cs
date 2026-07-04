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
using MegaCrit.Sts2.Core.Unlocks;
using STS2Dojo.STS2DojoCode.SeedSharing;

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
        NGame Game, CharacterModel Character, int AscensionLevel, ModelId EncounterId, Action<RunState> Mutate,
        DojoLaunchOptions Options);

    private static LaunchRequest? _lastRequest;

    /// <summary>RNG+loadout snapshot of the attempt currently (or most recently) being played, refreshed
    /// on every real launch INCLUDING Try Again relaunches — each attempt has its own seed, so the
    /// Completion screen's Export must always package the snapshot of the attempt that just concluded,
    /// never a stale earlier one (CLAUDE.md §12a entry points 1/3). Prepare-only captures (entry point 2)
    /// deliberately do NOT touch this.</summary>
    public static DojoFightSnapshot? LastSnapshot { get; private set; }

    /// <summary>Launches a fresh throwaway, non-saving run and jumps straight into the given encounter.
    /// <paramref name="mutate"/> replaces the player's deck/relics/HP/gold; it runs after the run's
    /// synthetic starting inventory + starting-relic <c>AfterObtained()</c> hooks have fired, but before
    /// the run's scene is created — see the class docs for why that ordering is load-bearing.
    /// <paramref name="options"/> (null = defaults) carries the §12 seed override + RNG-counter restore
    /// for imported shared fights; it is retained in the request so Try Again replays the same seed.</summary>
    public static async Task<RunState> LaunchThrowawayRun(
        NGame game, CharacterModel character, int ascensionLevel, ModelId encounterId, Action<RunState> mutate,
        DojoLaunchOptions? options = null)
    {
        options ??= DojoLaunchOptions.Default;
        _lastRequest = new LaunchRequest(game, character, ascensionLevel, encounterId, mutate, options);
        (RunState runState, _) = await LaunchInternal(
            game, character, ascensionLevel, encounterId, mutate, options, enterScene: true);
        return runState;
    }

    /// <summary>"Try again": relaunches the last <c>LaunchThrowawayRun</c> request. For a normal Dojo
    /// fight (no seed override) that means a fresh seed and fresh combat RNG per attempt (CLAUDE.md §3);
    /// for a seed-captured/imported fight the retained options replay the identical seed+counters every
    /// attempt (§12 decision 2026-07-04). Follows the same pattern as <c>Debug/FileDropHandler.cs</c>'s
    /// "load a different run without returning to the main menu": <c>RunManager.CleanUp()</c> then
    /// relaunch directly.</summary>
    public static async Task<RunState?> TryAgain()
    {
        if (_lastRequest is not { } request)
        {
            MainFile.Logger.Error("[STS2Dojo] No previous Dojo launch to retry.");
            return null;
        }

        (RunState runState, _) = await LaunchInternal(
            request.Game, request.Character, request.AscensionLevel, request.EncounterId, request.Mutate,
            request.Options, enterScene: true);
        return runState;
    }

    /// <summary>Runs the full launch sequence through the mutate callback and snapshot capture, then
    /// tears down WITHOUT ever creating the run scene or entering combat — §12a entry point 2, the
    /// Replay Setup modal's "Export" without playing. Sequence-identical to a real launch up to the
    /// capture point (asset preloads and <c>FinalizeStartingRelics</c> included — the §12a parity
    /// requirement, so an entry-point-2 export equals an entry-point-1 export of the same setup) except
    /// for the music stops and <c>RunManager.Launch()</c>, which touch no run/player/RNG state.
    /// Does not update <see cref="LastSnapshot"/> or the Try Again request.</summary>
    public static async Task<DojoFightSnapshot> PrepareSnapshot(
        NGame game, CharacterModel character, int ascensionLevel, ModelId encounterId, Action<RunState> mutate,
        DojoLaunchOptions? options = null)
    {
        if (RunManager.Instance.IsInProgress)
        {
            // A real launch tears down the in-progress run on purpose (Try Again); a snapshot capture
            // must never kill a live fight as a side effect.
            throw new InvalidOperationException("Cannot capture a fight snapshot while a run is in progress.");
        }

        (_, DojoFightSnapshot snapshot) = await LaunchInternal(
            game, character, ascensionLevel, encounterId, mutate, options ?? DojoLaunchOptions.Default,
            enterScene: false);
        return snapshot;
    }

    private static async Task<(RunState RunState, DojoFightSnapshot Snapshot)> LaunchInternal(
        NGame game, CharacterModel character, int ascensionLevel, ModelId encounterId, Action<RunState> mutate,
        DojoLaunchOptions options, bool enterScene)
    {
        if (enterScene)
        {
            // Match the base game's run-start behavior (main-menu/custom/daily flows all stop menu music
            // before launching) and also clear any lingering end-of-combat sting that might still be playing
            // when the player hits Try Again from the completion screen. Run music and global music are
            // managed by separate systems, so stop both to guarantee the new fight starts with a single
            // active track. Prepare-only captures never leave the current screen, so they leave music alone.
            NRun.Instance?.RunMusicController.StopMusic();
            NAudioManager.Instance?.StopMusic();
        }

        if (RunManager.Instance.IsInProgress)
        {
            RunManager.Instance.CleanUp();
        }

        // Fresh EncounterModel every attempt. Deliberately NOT calling encounter.DebugRandomizeRng() (the
        // pre-§12 behavior, copied from the built-in `fight` command): that seeds the encounter's private
        // monster-composition RNG from wall-clock time, which no shared-fight payload could ever reproduce.
        // Left null, GenerateMonstersWithSlots derives that RNG from the run seed instead
        // (EncounterModel.cs — runState.Rng.Seed + TotalFloor + hash(encounterId)), so composition is
        // seed-repeatable for captured fights while normal fights stay fresh per attempt via the fresh
        // per-attempt seed below. CLAUDE.md §12a determinism gap 1.
        EncounterModel encounter = ModelDb.GetById<EncounterModel>(encounterId).ToMutable();

        string seed = options.SeedOverride ?? SeedHelper.GetRandomSeed();

        // UnlockState.all, not GenerateUnlockStateFromProgress(): in-combat random card generation
        // (Discovery etc.) filters its candidate pool by the player's unlock progress, so two users with
        // the same seed but different unlocks would generate different cards — CLAUDE.md §12a determinism
        // gap 2. Fully-unlocked pools make every Dojo fight reproducible for every player; the throwaway
        // run never saves, so nothing leaks to real progress.
        UnlockState unlockState = UnlockState.all;
        RunState runState = RunState.CreateForNewRun(
            new List<Player> { Player.CreateForNewRun(character, unlockState, netId: 1uL) },
            ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(),
            Array.Empty<ModifierModel>(),
            GameMode.Standard,
            ascensionLevel,
            seed);

        RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: false);

        try
        {
            // Boss music fix: RunState.CreateForNewRun always starts CurrentActIndex at 0, and nothing else
            // ever corrects it here (EnterAct is the only other writer, and it's deliberately skipped above).
            // NRun._Ready() calls RunMusicController.UpdateMusic() the moment the scene is created below,
            // which loads the FMOD bank for runState.Act (= Acts[CurrentActIndex]) - if that's still Act 1
            // while launching an Act 2/3 fight, the boss's specific CustomBgm event
            // (CombatManager.StartCombatInternal -> PlayCustomMusic) isn't in any loaded bank and silently
            // fails to play. Elite/normal fights don't show the symptom because their music is just a
            // generic "Elite"/"Normal" progress parameter on whatever ambient track is already loaded, not a
            // specific per-boss event. Fix: point CurrentActIndex (and swap in the correct act variant - see
            // FixActForEncounter) at whichever act actually owns this encounter before the scene exists.
            // Inside this try block (not between it and SetUpNewSingleplayer above) because it does real
            // content lookups/RNG work that can throw - see the catch below for why that matters.
            FixActForEncounter(runState, encounter);

            // Kills the replays/latest.mcr write leak at the source (every write site in CombatReplayWriter /
            // CombatManager / RunManager.CleanUp checks IsEnabled first) — no Harmony patch needed for this one.
            RunManager.Instance.CombatReplayWriter.IsEnabled = false;

            DojoRunRegistry.MarkAsDojo(runState);

            DojoFightSnapshot snapshot;
            using (new NetLoadingHandle(RunManager.Instance.NetService))
            {
                await PreloadManager.LoadRunAssets(runState.Players.Select(p => p.Character));
                await PreloadManager.LoadActAssets(runState.Acts[0]);
                await RunManager.Instance.FinalizeStartingRelics();
                if (enterScene)
                {
                    // Fires RunStarted + rich presence only — no run/player/RNG state, so skipping it in
                    // prepare-only mode costs no capture parity.
                    RunManager.Instance.Launch();
                }

                mutate(runState);

                // Import path (§12e): reconcile the RNG streams to the exporter's exact captured counters.
                // Requires the run to have been constructed with the payload's seed string (SeedOverride
                // above) — both LoadFromSerializable methods throw on a seed mismatch by design.
                if (options.RunRngCounters != null)
                {
                    runState.Rng.LoadFromSerializable(options.RunRngCounters);
                }
                if (options.PlayerRngCounters != null)
                {
                    runState.Players[0].PlayerRng.LoadFromSerializable(options.PlayerRngCounters);
                }

                // The §12a capture point: post-mutate/post-restore, pre-scene, every combat stream still at
                // its pre-combat counter. Captured on EVERY launch so any concluded fight can be exported.
                snapshot = CaptureSnapshot(runState, character, ascensionLevel, encounterId, seed);

                if (enterScene)
                {
                    LastSnapshot = snapshot;
                    game.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
                }
            }

            if (!enterScene)
            {
                // Prepare-only (§12a entry point 2): the snapshot is what the caller wanted; tear the
                // never-entered run back down — same call the failure path below uses.
                RunManager.Instance.CleanUp();
                return (runState, snapshot);
            }

            await EnterEncounter(encounter);
            return (runState, snapshot);
        }
        catch
        {
            // FixActForEncounter, mutate(...) (e.g. RunReconstructor content resolution), or the asset/scene
            // setup above can throw with a live, never-entered RunState already installed via
            // SetUpNewSingleplayer. Left in place, RunManager.State stays occupied:
            // RunManager.SetUpNewSingleplayer throws "State is already set." for ANY subsequent run — Dojo
            // or a real player run — until something else happens to call CleanUp() first. Recover
            // immediately rather than leaving the session poisoned; the caller's own catch block still logs
            // the original exception.
            RunManager.Instance.CleanUp();
            throw;
        }
    }

    public static async Task EnterEncounter(EncounterModel encounter)
    {
        await RunManager.Instance.EnterRoomDebug(encounter.RoomType, MapPointType.Unassigned, encounter);
    }

    /// <summary>Reads the shareable-fight snapshot (CLAUDE.md §12b) off the live run state. Uses the same
    /// <c>ToSerializable()</c> DTO path the game's own quit/resume save uses, so relic/card Props (incl.
    /// Replay Setup adjustments already applied by the mutate callback) round-trip through the identical
    /// SavedProperties pipeline on import.</summary>
    private static DojoFightSnapshot CaptureSnapshot(
        RunState runState, CharacterModel character, int ascensionLevel, ModelId encounterId, string seed)
    {
        Player player = runState.Players[0];
        return new DojoFightSnapshot
        {
            Seed = seed,
            RunRng = runState.Rng.ToSerializable(),
            PlayerRng = player.PlayerRng.ToSerializable(),
            CharacterId = character.Id,
            Ascension = ascensionLevel,
            EncounterId = encounterId,
            Deck = player.Deck.Cards.Select(c => c.ToSerializable()).ToList(),
            Relics = player.Relics.Select(r => r.ToSerializable()).ToList(),
            Potions = player.Potions.Select(p => p.ToSerializable(player.GetPotionSlotIndex(p))).ToList(),
            MaxPotionSlots = player.MaxPotionCount,
            CurrentHp = player.Creature.CurrentHp,
            MaxHp = player.Creature.MaxHp,
            Gold = player.Gold,
        };
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
