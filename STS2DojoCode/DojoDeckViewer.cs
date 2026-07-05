using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
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
/// instances only. The card-detail drill-in (<c>ShowCardDetail</c> → <c>NGame.Instance.GetInspectCardScreen()</c>,
/// which the main-menu Compendium card library also uses) resolves fine, but the enlarged card is parented to
/// NGame's <c>%InspectionContainer</c>, which sits *below* the <c>ModalContainer</c> hosting this viewer — so
/// it would open underneath the deck. <see cref="RaiseInspectionContainer"/> lifts that container above the
/// modal container while the viewer is open (restored on close).
///
/// Because there is no <c>NRun</c> here, the game's persistent top bar (which normally covers the top of the
/// deck-view screen) does not exist, exposing the card grid's top fade-to-black vignette over the darkened
/// Dojo screen as a top-edge distortion. <see cref="AddTopBarBackdrop"/> re-draws the top bar's stone slate
/// over that strip to restore the in-run appearance.
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

    /// <summary>The stone/slate strip the run top bar (<c>NTopBar.BgImage</c>) draws at the top of the
    /// screen. See <see cref="AddTopBarBackdrop"/> for why the Dojo-hosted viewer needs it explicitly.</summary>
    private const string TopBarSlateTexturePath =
        "res://images/atlases/ui_atlas.sprites/top_bar/top_bar.tres";

    /// <summary>NGame's persistent <c>%InspectionContainer</c>, temporarily raised above the modal
    /// container while a Dojo viewer is open (see <see cref="RaiseInspectionContainer"/>), plus the sibling
    /// index to restore it to on close. Static because the viewer is single-instance and the container
    /// outlives the screen.</summary>
    private static Node? _raisedInspectionContainer;
    private static int _inspectionContainerOriginalIndex;

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
            AddTopBarBackdrop(screen);
            RaiseInspectionContainer();
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

    internal static void OnHostedScreenClosed()
    {
        HostedScreen = null;
        RestoreInspectionContainer();
    }

    /// <summary>Raises NGame's <c>%InspectionContainer</c> above its <c>ModalContainer</c> sibling for as
    /// long as the Dojo viewer is open. The card-detail drill-in (<c>NCardsViewScreen.ShowCardDetail</c> →
    /// <c>NGame.GetInspectCardScreen().Open(...)</c>) adds the magnified card to the InspectionContainer,
    /// which in NGame's scene sits *below* the ModalContainer that hosts this viewer — so the enlarged card
    /// opened over the deck would render underneath it and be invisible. In a real run the deck view lives
    /// in the run-scoped capstone container (below InspectionContainer), so the inspect screen is naturally
    /// on top; hosting in the modal container inverts that. Reordering the sibling (rather than reparenting
    /// the singleton inspect screen) is the least invasive fix. The container is persistent (NGame outlives
    /// scene swaps), so <see cref="RestoreInspectionContainer"/> must put it back on close, or in-run modal/
    /// inspection layering would stay altered. Best-effort: a failure here just leaves the pre-existing
    /// (card-hidden) behavior rather than blocking the viewer.</summary>
    private static void RaiseInspectionContainer()
    {
        try
        {
            // GetInspectCardScreen() lazily creates the screen and parents it to %InspectionContainer;
            // its parent is exactly the container we need to raise. Harmless if the screen already existed.
            Node? container = NGame.Instance?.GetInspectCardScreen().GetParent();
            Node? ngame = container?.GetParent();
            if (container == null || ngame == null)
            {
                return;
            }

            _raisedInspectionContainer = container;
            _inspectionContainerOriginalIndex = container.GetIndex();
            ngame.MoveChild(container, -1); // -1 => last sibling => drawn above ModalContainer
        }
        catch (Exception e)
        {
            _raisedInspectionContainer = null;
            MainFile.Logger.Info("[STS2Dojo] Deck viewer: could not raise inspection container: " + e);
        }
    }

    private static void RestoreInspectionContainer()
    {
        Node? container = _raisedInspectionContainer;
        _raisedInspectionContainer = null;
        if (container == null || !GodotObject.IsInstanceValid(container))
        {
            return;
        }

        try
        {
            container.GetParent()?.MoveChild(container, _inspectionContainerOriginalIndex);
        }
        catch (Exception e)
        {
            MainFile.Logger.Info("[STS2Dojo] Deck viewer: could not restore inspection container: " + e);
        }
    }

    /// <summary>Draws the stone/slate top-bar strip over the top of the deck viewer, replicating
    /// <c>NTopBar.BgImage</c>. The stock <c>deck_view_screen.tscn</c> is a transparent Control, and its
    /// card grid (<c>card_grid.tscn</c>) carries a <c>BorderGradient</c> that fades the top and bottom card
    /// rows to opaque black — the "cards fade off the edges" vignette. In a real run that top fade is hidden
    /// under the game's persistent, opaque top bar (which sits over exactly this ~100px strip, above the
    /// 80px grid inset from <c>NCardGrid.InsetForTopBar</c>). At the Dojo main menu there is no NRun, so no
    /// top bar exists, and the exposed fade over the darkened Dojo screen reads as the reported top-edge
    /// distortion. Adding the same slate texture as the screen's last child (so it draws above the grid,
    /// like the real top bar) restores the run's appearance: a blank stone strip, with the intended gentle
    /// fade where cards slide in beneath it. Failure to load the texture degrades to the prior (ugly but
    /// functional) look rather than blocking the viewer, so it is caught and logged, not rethrown.</summary>
    private static void AddTopBarBackdrop(NDeckViewScreen screen)
    {
        try
        {
            var slate = new TextureRect
            {
                Name = "DojoDeckViewTopBarBg",
                Texture = PreloadManager.Cache.GetTexture2D(TopBarSlateTexturePath),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                // Nothing interactive lives under this strip; let scroll-wheel/drag pass through to the grid.
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            // Full-width, pinned to the top, ~100px tall + 1% overscan — matching NTopBar.BgImage.
            slate.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
            slate.OffsetBottom = 100f;
            slate.Scale = new Vector2(1.01f, 1.01f);
            // Last child => drawn above CardGrid, occluding the murky top fade exactly as the run top bar does.
            screen.AddChild(slate);
        }
        catch (Exception e)
        {
            MainFile.Logger.Info("[STS2Dojo] Deck viewer: could not add top-bar backdrop: " + e);
        }
    }

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
