using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Opens the game's own in-combat deck viewer (<see cref="NDeckViewScreen"/> — the "D"-key screen) over
/// the Dojo screen, at the main menu, for an arbitrary historical deck: a run row's end-of-run deck or a
/// saved fight's captured deck. The stock screen normally lives inside a run's capstone container
/// (<c>NCapstoneContainer.Instance</c> is <c>NRun</c>-scoped, so it's null at the main menu); here it is
/// hosted by <see cref="NModalContainer"/> instead — the same run-independent host the Dojo's confirm/edit
/// modals already use, whose backstop replaces the capstone system's shared one. The only capstone
/// dependency inside the screen itself is the Back button's <c>NCardsViewScreen.OnReturnButtonPressed</c>
/// (→ <c>NCapstoneContainer.Instance.Close()</c>, an NRE at the menu), which
/// <see cref="DojoDeckViewClosePatch"/> redirects to <c>NModalContainer.Clear()</c> for Dojo-hosted
/// instances only. The card-detail drill-in needs no help: <c>ShowCardDetail</c> goes through
/// <c>NGame.Instance.GetInspectCardScreen()</c>, which the main-menu Compendium card library already uses.
///
/// The screen wants a live <see cref="Player"/> whose Deck pile holds the cards, so this builds the same
/// cheap, never-registered throwaway RunState/Player <see cref="DojoFloorEligibility"/> uses for preview
/// reconstruction, clears its starting deck, and loads the requested cards through
/// <c>RunState.LoadCard</c> + <c>AddInternal(silent: true)</c> — the §5a-safe sequence
/// <see cref="DojoLoadoutApplier"/> documents. §5m note: no mod class here derives a Godot built-in; the
/// instantiated screen is a pure game class, so there is no script-dispatch hazard.
/// </summary>
public static class DojoDeckViewer
{
    private static readonly AccessTools.FieldRef<NDeckViewScreen, Player> PlayerField =
        AccessTools.FieldRefAccess<NDeckViewScreen, Player>("_player");

    /// <summary>The screen instance currently hosted in the modal container, if any — how
    /// <see cref="DojoDeckViewClosePatch"/> distinguishes a Dojo-hosted viewer from the stock in-run
    /// capstone use of the same class.</summary>
    internal static NDeckViewScreen? HostedScreen { get; private set; }

    /// <summary>Opens the deck viewer for <paramref name="deck"/>. Returns false (with a user-presentable
    /// <paramref name="error"/>) instead of throwing — an unresolvable character or an unexpected failure
    /// should degrade to a status/log message, never crash the Dojo screen.</summary>
    public static bool TryOpen(ModelId? characterId, IEnumerable<SerializableCard> deck, out string? error)
    {
        try
        {
            CharacterModel? character = characterId != null
                ? ModelDb.GetByIdOrNull<CharacterModel>(characterId)
                : null;
            if (character == null)
            {
                error = "This deck's character is not available in the current game content.";
                return false;
            }

            NModalContainer? modalContainer = NModalContainer.Instance;
            if (modalContainer == null || modalContainer.OpenModal != null)
            {
                error = "The deck viewer could not be opened right now.";
                return false;
            }

            List<SerializableCard> cards = deck.Where(card => card?.Id != null).ToList();
            if (cards.Count == 0)
            {
                error = "This deck has no cards to show.";
                return false;
            }

            Player player = BuildDisplayPlayer(character, cards, out int unresolvable);
            if (player.Deck.IsEmpty)
            {
                error = "None of this deck's cards are available in the current game content.";
                return false;
            }
            if (unresolvable > 0)
            {
                MainFile.Logger.Info(
                    $"[STS2Dojo] Deck viewer: {unresolvable} card(s) could not be resolved and were skipped.");
            }

            // The deck-view scene is only preloaded for combat; at the main menu AssetCache falls back to
            // a synchronous ResourceLoader.Load (logging "Asset not cached"), which is fine here.
            var screen = PreloadManager.Cache
                .GetScene(SceneHelper.GetScenePath("screens/deck_view_screen"))
                .Instantiate<NDeckViewScreen>();
            // _player must be set before the screen enters the tree: _EnterTree resolves the Deck pile
            // from it and _Ready reads the character's card-frame material.
            PlayerField(screen) = player;

            HostedScreen = screen;
            modalContainer.Add(screen);
            error = null;
            return true;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not open the deck viewer: " + e);
            HostedScreen = null;
            error = "Could not open the deck viewer — see log.";
            return false;
        }
    }

    internal static void OnHostedScreenClosed() => HostedScreen = null;

    /// <summary>A display-only player holding exactly <paramref name="cards"/> in its Deck pile. Same
    /// throwaway pattern as DojoFloorEligibility: RunState.CreateForNewRun wires player.RunState directly,
    /// nothing is registered with RunManager, and the whole graph is garbage once the screen closes.
    /// Ascension/seed are irrelevant to display; the auto-populated starting deck is cleared first.</summary>
    private static Player BuildDisplayPlayer(
        CharacterModel character, List<SerializableCard> cards, out int unresolvable)
    {
        Player player = Player.CreateForNewRun(character, UnlockState.all, netId: 1uL);
        RunState runState = RunState.CreateForNewRun(
            new List<Player> { player },
            ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(),
            Array.Empty<ModifierModel>(),
            GameMode.Standard,
            ascensionLevel: 0,
            SeedHelper.GetRandomSeed());

        player.Deck.Clear(silent: true);
        unresolvable = 0;
        foreach (SerializableCard card in cards)
        {
            try
            {
                // RunState.LoadCard (not CardModel.FromSerializable directly) so CardModel.Owner is set —
                // the grid's holders and the card-detail drill-in read owner-dependent display state.
                CardModel loaded = runState.LoadCard(card, player);
                player.Deck.AddInternal(loaded, index: -1, silent: true);
            }
            catch (Exception)
            {
                // A card from an uninstalled mod / removed content: skip it, count it, show the rest.
                unresolvable++;
            }
        }
        player.Deck.InvokeCardAddFinished();
        return player;
    }
}

/// <summary>
/// Redirects <c>NCardsViewScreen.OnReturnButtonPressed</c> (the Back button, whose stock NBackButton
/// hotkeys also give us ESC/controller-B) to <c>NModalContainer.Clear()</c> when — and only when — the
/// screen is the Dojo-hosted deck viewer. The stock path calls <c>NCapstoneContainer.Instance.Close()</c>,
/// which NREs at the main menu where that instance doesn't exist; real in-run capstone screens (including
/// the game's own D-key deck view during a Dojo fight) are not the hosted instance and keep stock behavior.
/// </summary>
[HarmonyPatch(typeof(NCardsViewScreen), "OnReturnButtonPressed")]
public static class DojoDeckViewClosePatch
{
    public static bool Prefix(NCardsViewScreen __instance)
    {
        if (!ReferenceEquals(__instance, DojoDeckViewer.HostedScreen))
        {
            return true;
        }

        DojoDeckViewer.OnHostedScreenClosed();
        NModalContainer.Instance?.Clear();
        return false;
    }
}
