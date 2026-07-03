using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using STS2Dojo.STS2DojoCode.Reconstruction;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// The custom Dojo run browser (replaces the first-pass stock-NRunHistory reuse as the Dojo's landing
/// screen): a left sidebar of live filters (character / ascension / victory / search / sort) over a
/// scrolling list of the REAL profile's single-player runs, each row showing the run's stats and a
/// per-act strip of its boss/elite fights as clickable pills that launch that fight directly through
/// <see cref="DojoReplayLauncher"/>.
///
/// Safety: the run list is fed by <see cref="DojoRunIndex"/>, which reads the real profile's history
/// directory directly (read-only, absolute paths) and never touches
/// <c>UserDataPathProvider.IsRunningModded</c> — see CLAUDE.md §5h/§6. Only the "View All Combats"
/// drill-in (stock <c>NRunHistory</c>) still uses <see cref="DojoRunBrowser"/>'s guarded flag flip.
///
/// Like <see cref="DojoCompletionScreen"/>, this is a procedurally-built node tree (the mod ships no
/// <c>.pck</c>): it derives from <see cref="NSubmenu"/> and is pushed onto the main menu's submenu
/// stack (<c>NSubmenuStack.Push</c> is public), but overrides <c>ConnectSignals</c> WITHOUT calling
/// base — base requires a scene-baked "BackButton" child this screen doesn't have; back navigation is
/// the sidebar's Back button (which also registers the cancel/back hotkeys, so ESC works).
/// </summary>
public partial class NDojoScreen : NSubmenu
{
    private const float SidebarWidth = 380f;
    private const int RowBatchSize = 12;
    private const float ScrollLoadMargin = 900f;

    private static readonly Color BackdropColor = new(0.03f, 0.035f, 0.045f, 0.88f);
    private static readonly Color SidebarColor = new("161A20EE");
    private static readonly Color RowColor = new("1A1F27F2");
    private static readonly Color RowBorderColor = new("2A313B");
    private static readonly Color MutedText = new("9AA3AE");
    private static readonly Color FaintText = new("6B7480");

    private static NDojoScreen? _instance;

    private readonly List<DojoChip> _characterChips = new();
    private readonly List<DojoChip> _ascensionChips = new();
    private readonly List<DojoChip> _victoryChips = new();
    private readonly List<DojoChip> _sortChips = new();

    private LineEdit _searchBox = null!;
    private ScrollContainer _scroll = null!;
    private VBoxContainer _rowContainer = null!;
    private Label _statusLabel = null!;
    private DojoBackChip _backChip = null!;
    private Godot.Timer _searchDebounce = null!;

    private ModelId? _filterCharacter;
    private int? _filterAscension;
    private DojoVictoryFilter _filterVictory = DojoVictoryFilter.Both;
    private DojoRunSortOrder _sortOrder = DojoRunSortOrder.Newest;

    private DojoRunIndexResult? _index;
    private List<DojoRunSummary> _visibleRuns = new();
    private int _rowsBuilt;
    private bool _loading;

    protected override Control? InitialFocusedControl => _searchBox;

    public static void Open(NGame game)
    {
        NMainMenu? mainMenu = game.MainMenu;
        if (mainMenu == null)
        {
            MainFile.Logger.Error("[STS2Dojo] Cannot open the Dojo screen: not currently on the main menu.");
            return;
        }

        NSubmenuStack stack = mainMenu.SubmenuStack;
        if (_instance == null || !GodotObject.IsInstanceValid(_instance) || _instance.GetParent() != stack)
        {
            _instance = new NDojoScreen();
            _instance.Visible = false;
            _instance.BuildLayout();
            stack.AddChildSafely(_instance);
        }

        stack.Push(_instance);
    }

    public override void _Ready()
    {
        ConnectSignals();
    }

    /// <summary>Deliberately does NOT call base.ConnectSignals(): the base implementation requires a
    /// scene-baked "BackButton" child (NBackButton) that a code-only screen doesn't have. The pieces of
    /// base behavior that matter are replicated here: the visibility-driven shown/hidden hooks, and —
    /// critically — enabling/disabling the back control with visibility. NHotkeyManager invokes only the
    /// LAST-pushed binding per hotkey, and stock submenus rely on "hidden submenu ⇒ back button Disabled
    /// ⇒ hotkeys unregistered" to keep exactly one cancel binding live; a back chip that stayed enabled
    /// while this screen sits hidden in the submenu stack would shadow every other ESC consumer.</summary>
    protected override void ConnectSignals()
    {
        // The chip registered its hotkeys in its own _Ready (children ready before parent); this screen
        // starts hidden, so immediately hand the cancel binding back until the screen is actually shown.
        _backChip.Disable();

        VisibilityChanged += () =>
        {
            if (Visible)
            {
                _backChip.Enable();
                OnSubmenuShown();
            }
            else
            {
                _lastFocusedControl = GetViewport()?.GuiGetFocusOwner();
                _backChip.Disable();
                OnSubmenuHidden();
            }
        };
    }

    public override void OnSubmenuOpened()
    {
        TaskHelper.RunSafely(RefreshRunsAsync());
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), BackdropColor);
    }

    private void GoBack()
    {
        // Guard against the cancel/back hotkey while some other submenu (e.g. the stock NRunHistory
        // drill-in) is stacked on top — only pop when this screen is actually the top of the stack.
        if (_stack != null && _stack.Peek() == this)
        {
            _stack.Pop();
        }
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 40);
        margin.AddThemeConstantOverride("margin_right", 40);
        margin.AddThemeConstantOverride("margin_top", 30);
        margin.AddThemeConstantOverride("margin_bottom", 30);
        AddChild(margin);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 28);
        margin.AddChild(columns);

        columns.AddChild(BuildSidebar());
        columns.AddChild(BuildMainArea());
    }

    private Control BuildSidebar()
    {
        var sidebar = new PanelContainer();
        sidebar.AddThemeStyleboxOverride("panel", MakePanelStyle(SidebarColor, RowBorderColor, 12));
        sidebar.CustomMinimumSize = new Vector2(SidebarWidth, 0);
        sidebar.SizeFlagsVertical = SizeFlags.ExpandFill;

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 22);
        pad.AddThemeConstantOverride("margin_right", 22);
        pad.AddThemeConstantOverride("margin_top", 24);
        pad.AddThemeConstantOverride("margin_bottom", 24);
        sidebar.AddChild(pad);

        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 10);
        pad.AddChild(stack);

        stack.AddChild(DojoUi.MakeLabel("THE DOJO", 34, StsColors.gold));
        stack.AddChild(DojoUi.MakeLabel("Replay any past fight as practice", 15, MutedText));
        stack.AddChild(MakeSpacer(10));

        _searchBox = new LineEdit();
        _searchBox.PlaceholderText = "Search character, boss, seed...";
        _searchBox.ClearButtonEnabled = true;
        _searchBox.CustomMinimumSize = new Vector2(0, 46);
        _searchBox.AddThemeStyleboxOverride("normal", MakePanelStyle(new Color("10131A"), RowBorderColor, 8));
        _searchBox.AddThemeStyleboxOverride("focus", MakePanelStyle(new Color("10131A"), StsColors.gold, 8));
        _searchBox.AddThemeColorOverride("font_color", StsColors.cream);
        _searchBox.AddThemeColorOverride("font_placeholder_color", FaintText);
        _searchBox.AddThemeFontSizeOverride("font_size", 16);
        Font? uiFont = DojoUi.UiFont;
        if (uiFont != null)
        {
            _searchBox.AddThemeFontOverride("font", uiFont);
        }
        // Debounced: rebuilding the whole visible list (teardown + row construction) on every keystroke
        // of a fast typist is wasted work; a short one-shot timer batches them.
        _searchDebounce = new Godot.Timer();
        _searchDebounce.WaitTime = 0.18;
        _searchDebounce.OneShot = true;
        _searchDebounce.Timeout += RebuildList;
        AddChild(_searchDebounce);
        _searchBox.TextChanged += _ => _searchDebounce.Start();
        stack.AddChild(_searchBox);
        stack.AddChild(MakeSpacer(8));

        // Character: All + every playable character in the loaded content.
        stack.AddChild(DojoUi.MakeLabel("Character", 19, StsColors.cream));
        var characterGrid = new HFlowContainer();
        characterGrid.AddThemeConstantOverride("h_separation", 8);
        characterGrid.AddThemeConstantOverride("v_separation", 8);
        stack.AddChild(characterGrid);
        AddFilterChip(characterGrid, _characterChips, "All", selected: true,
            () => { _filterCharacter = null; RebuildList(); });
        foreach (CharacterModel character in SafeAllCharacters())
        {
            ModelId id = character.Id;
            AddCharacterFilterChip(characterGrid, _characterChips, id, selected: false,
                () => { _filterCharacter = id; RebuildList(); });
        }
        stack.AddChild(MakeSpacer(8));

        stack.AddChild(DojoUi.MakeLabel("Ascension", 19, StsColors.cream));
        var ascensionGrid = new HFlowContainer();
        ascensionGrid.AddThemeConstantOverride("h_separation", 8);
        ascensionGrid.AddThemeConstantOverride("v_separation", 8);
        stack.AddChild(ascensionGrid);
        AddFilterChip(ascensionGrid, _ascensionChips, "All", selected: true,
            () => { _filterAscension = null; RebuildList(); });
        for (int ascension = 0; ascension <= 10; ascension++)
        {
            int value = ascension;
            AddFilterChip(ascensionGrid, _ascensionChips, value.ToString(), selected: false,
                () => { _filterAscension = value; RebuildList(); });
        }
        stack.AddChild(MakeSpacer(8));

        stack.AddChild(DojoUi.MakeLabel("Victory", 19, StsColors.cream));
        var victoryRow = new HFlowContainer();
        victoryRow.AddThemeConstantOverride("h_separation", 8);
        stack.AddChild(victoryRow);
        AddFilterChip(victoryRow, _victoryChips, "Both", selected: true,
            () => { _filterVictory = DojoVictoryFilter.Both; RebuildList(); });
        AddFilterChip(victoryRow, _victoryChips, "Victory", selected: false,
            () => { _filterVictory = DojoVictoryFilter.Victory; RebuildList(); });
        AddFilterChip(victoryRow, _victoryChips, "Defeat", selected: false,
            () => { _filterVictory = DojoVictoryFilter.Defeat; RebuildList(); });
        stack.AddChild(MakeSpacer(12));

        var resetChip = DojoUi.MakeChip("Reset Filters", compact: false);
        resetChip.Released += _ => ResetFilters();
        stack.AddChild(resetChip);

        var filler = new Control();
        filler.SizeFlagsVertical = SizeFlags.ExpandFill;
        stack.AddChild(filler);

        _backChip = new DojoBackChip();
        _backChip.Configure("← Back to Menu", compact: false);
        _backChip.Released += _ => GoBack();
        stack.AddChild(_backChip);

        return sidebar;
    }

    private Control BuildMainArea()
    {
        var main = new VBoxContainer();
        main.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        main.SizeFlagsVertical = SizeFlags.ExpandFill;
        main.AddThemeConstantOverride("separation", 12);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        main.AddChild(header);

        header.AddChild(DojoUi.MakeLabel("Select a run", 26, StsColors.cream));
        var headerFiller = new Control();
        headerFiller.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(headerFiller);
        header.AddChild(DojoUi.MakeLabel("Sort", 16, MutedText));
        AddSortChip(header, "Newest", DojoRunSortOrder.Newest, selected: true);
        AddSortChip(header, "Oldest", DojoRunSortOrder.Oldest, selected: false);
        AddSortChip(header, "Floor", DojoRunSortOrder.Floor, selected: false);
        AddSortChip(header, "Ascension", DojoRunSortOrder.Ascension, selected: false);

        _statusLabel = DojoUi.MakeLabel("Loading runs...", 17, MutedText);
        main.AddChild(_statusLabel);

        _scroll = new ScrollContainer();
        _scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        main.AddChild(_scroll);

        _rowContainer = new VBoxContainer();
        _rowContainer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _rowContainer.AddThemeConstantOverride("separation", 14);
        _scroll.AddChild(_rowContainer);

        _scroll.GetVScrollBar().ValueChanged += _ => MaybeBuildMoreRows();

        return main;
    }

    private void AddFilterChip(Control parent, List<DojoChip> group, string text, bool selected, Action apply)
    {
        var chip = DojoUi.MakeChip(text, compact: true);
        AddFilterChip(parent, group, chip, selected, apply);
    }

    private void AddCharacterFilterChip(
        Control parent, List<DojoChip> group, ModelId characterId, bool selected, Action apply)
    {
        DojoChip chip = DojoUi.MakeCharacterChip(characterId, DojoDisplayNames.Character(characterId));
        AddFilterChip(parent, group, chip, selected, apply);
    }

    private void AddFilterChip(Control parent, List<DojoChip> group, DojoChip chip, bool selected, Action apply)
    {
        chip.Selected = selected;
        chip.Released += _ =>
        {
            foreach (DojoChip other in group)
            {
                other.Selected = other == chip;
            }
            apply();
        };
        group.Add(chip);
        parent.AddChild(chip);
    }

    private void AddSortChip(Control parent, string text, DojoRunSortOrder order, bool selected) =>
        AddFilterChip(parent, _sortChips, text, selected, () => { _sortOrder = order; RebuildList(); });

    private void ResetFilters()
    {
        _filterCharacter = null;
        _filterAscension = null;
        _filterVictory = DojoVictoryFilter.Both;
        _searchBox.Text = string.Empty;
        SelectFirst(_characterChips);
        SelectFirst(_ascensionChips);
        SelectFirst(_victoryChips);
        RebuildList();
    }

    private static void SelectFirst(List<DojoChip> group)
    {
        for (int i = 0; i < group.Count; i++)
        {
            group[i].Selected = i == 0;
        }
    }

    private static Control MakeSpacer(float height) =>
        new() { CustomMinimumSize = new Vector2(0, height) };

    internal static StyleBoxFlat MakePanelStyle(Color background, Color border, int cornerRadius)
    {
        var style = new StyleBoxFlat();
        style.BgColor = background;
        style.BorderColor = border;
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(cornerRadius);
        style.SetContentMarginAll(8);
        return style;
    }

    private static IEnumerable<CharacterModel> SafeAllCharacters()
    {
        try
        {
            return ModelDb.AllCharacters.ToList();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not enumerate characters for the Dojo sidebar: " + e);
            return [];
        }
    }

    // ------------------------------------------------------------------ data

    private async Task RefreshRunsAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            _statusLabel.Text = "Loading runs...";
            // The directory is resolved here on the main thread (it touches Godot's ProjectSettings);
            // parsing ~1000 .run files is then CPU-only work with no Godot/game-state access, so it runs
            // off-thread. Godot's synchronization context resumes this method on the main thread for the
            // UI work below.
            string? directory = DojoRunIndex.TryGetRealHistoryDirectory();
            DojoRunIndexResult result = await Task.Run(() => DojoRunIndex.LoadAll(directory));
            _index = result;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Failed to load the Dojo run index: " + e);
            _index = new DojoRunIndexResult { Runs = [], ExcludedCounts = [] };
        }
        finally
        {
            _loading = false;
        }

        if (IsInsideTree())
        {
            RebuildList();
        }
    }

    private void RebuildList()
    {
        if (_index == null)
        {
            return;
        }

        var filter = new DojoRunFilter(_filterCharacter, _filterAscension, _filterVictory, _searchBox.Text);
        _visibleRuns = DojoRunListQueries.Apply(_index.Runs, filter, _sortOrder, DojoDisplayNames.ForSearch);

        foreach (Node child in _rowContainer.GetChildren())
        {
            _rowContainer.RemoveChild(child);
            child.QueueFreeSafely();
        }
        _rowsBuilt = 0;
        _scroll.ScrollVertical = 0;

        UpdateStatusLabel();
        BuildMoreRows();
    }

    private void UpdateStatusLabel()
    {
        if (_index == null)
        {
            return;
        }

        if (_index.Runs.Count == 0)
        {
            _statusLabel.Text = _index.ExcludedTotal > 0
                ? $"No replayable runs found ({_index.ExcludedTotal} runs are hidden: multiplayer, modifiers, or unsupported)."
                : "No runs found in your profile's history yet - finish a run first!";
            return;
        }

        string text = _visibleRuns.Count == _index.Runs.Count
            ? $"{_index.Runs.Count} runs"
            : $"{_visibleRuns.Count} of {_index.Runs.Count} runs match";
        if (_index.ExcludedTotal > 0)
        {
            text += $"  ·  {_index.ExcludedTotal} hidden (multiplayer, modifiers, or unsupported)";
        }
        _statusLabel.Text = text;
    }

    private void MaybeBuildMoreRows()
    {
        if (_rowsBuilt >= _visibleRuns.Count)
        {
            return;
        }

        VScrollBar bar = _scroll.GetVScrollBar();
        if (bar.Value + bar.Page >= bar.MaxValue - ScrollLoadMargin)
        {
            BuildMoreRows();
        }
    }

    private void BuildMoreRows()
    {
        int end = Math.Min(_rowsBuilt + RowBatchSize, _visibleRuns.Count);
        for (int i = _rowsBuilt; i < end; i++)
        {
            _rowContainer.AddChild(BuildRunRow(_visibleRuns[i]));
        }
        _rowsBuilt = end;
    }

    // ------------------------------------------------------------------ run rows

    private Control BuildRunRow(DojoRunSummary run)
    {
        var row = new PanelContainer();
        row.AddThemeStyleboxOverride("panel", MakePanelStyle(RowColor, RowBorderColor, 10));
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 20);
        pad.AddThemeConstantOverride("margin_right", 20);
        pad.AddThemeConstantOverride("margin_top", 14);
        pad.AddThemeConstantOverride("margin_bottom", 14);
        row.AddChild(pad);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 18);
        pad.AddChild(columns);

        columns.AddChild(BuildCharacterBadge(run));

        var center = new VBoxContainer();
        center.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        center.AddThemeConstantOverride("separation", 8);
        columns.AddChild(center);
        center.AddChild(BuildRowHeader(run));

        // The full parsed run is loaded on demand (weakly cached — see DojoRunSummary.RunSource); the
        // act strip, death quote, and pill launches all need it. If the file has become unreadable since
        // the index was built, degrade to a header-only row instead of crashing the whole list.
        RunHistory? history = null;
        try
        {
            history = run.GetRun();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[STS2Dojo] Could not re-load run file '{run.FilePath}': {e.Message}");
        }

        if (history != null)
        {
            center.AddChild(BuildActStrip(run, history));
            Control? quote = BuildDeathQuote(run, history);
            if (quote != null)
            {
                center.AddChild(quote);
            }
        }
        else
        {
            center.AddChild(DojoUi.MakeLabel("Run file could not be read.", 15, FaintText));
        }

        columns.AddChild(BuildRowMeta(run));
        return row;
    }

    private Control BuildCharacterBadge(DojoRunSummary run)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        box.SizeFlagsVertical = SizeFlags.ShrinkBegin;

        box.AddChild(DojoUi.MakeCharacterToken(run.CharacterId, ResolveCharacterColor(run.CharacterId)));

        var ascension = DojoUi.MakeLabel($"A{run.Ascension}", 15, StsColors.gold);
        ascension.HorizontalAlignment = HorizontalAlignment.Center;
        ascension.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        box.AddChild(ascension);
        return box;
    }

    private static Color ResolveCharacterColor(ModelId characterId)
    {
        try
        {
            CharacterModel? model = ModelDb.GetByIdOrNull<CharacterModel>(characterId);
            if (model != null)
            {
                return model.NameColor;
            }
        }
        catch (Exception)
        {
            // fall through to the neutral default
        }
        return StsColors.gray;
    }

    private Control BuildRowHeader(DojoRunSummary run)
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 14);

        header.AddChild(DojoUi.MakeLabel(DojoDisplayNames.Character(run.CharacterId), 22, StsColors.cream));

        (string outcomeText, Color outcomeColor) = run.Win
            ? ("VICTORY", StsColors.green)
            : run.WasAbandoned
                ? ("ABANDONED", StsColors.orange)
                : ("DEFEAT", StsColors.red);
        header.AddChild(DojoUi.MakeLabel(outcomeText, 16, outcomeColor));

        header.AddChild(DojoUi.MakeLabel($"Floor {run.FloorsReached}", 17, MutedText));
        Color hpColor = run.EndHp <= 0 ? StsColors.red : MutedText;
        header.AddChild(DojoUi.MakeLabel($"HP {run.EndHp} / {run.EndMaxHp}", 17, hpColor));
        return header;
    }

    private Control BuildActStrip(DojoRunSummary run, RunHistory history)
    {
        var strip = new HBoxContainer();
        strip.AddThemeConstantOverride("separation", 26);
        strip.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        IReadOnlyList<MapPointHistoryEntry> floors = RunHistoryQueries.FlattenFloors(history);

        foreach (DojoActSummary act in run.Acts)
        {
            var actBox = new VBoxContainer();
            actBox.AddThemeConstantOverride("separation", 5);

            string actName = act.ActId != null
                ? DojoDisplayNames.Act(act.ActId).ToUpperInvariant()
                : $"ACT {act.ActIndex + 1}";
            actBox.AddChild(DojoUi.MakeLabel(actName, 13, FaintText));

            var fightColumn = new VBoxContainer();
            fightColumn.AddThemeConstantOverride("separation", 5);
            actBox.AddChild(fightColumn);

            var fights = act.DisplayFights
                .OrderBy(fight => fight.GlobalFloor)
                .ToList();
            if (fights.Count == 0)
            {
                fightColumn.AddChild(DojoUi.MakeLabel("Boss not reached", 14, FaintText));
            }
            foreach (DojoFightSummary fight in fights)
            {
                fightColumn.AddChild(BuildFightPill(run, history, fight, floors));
            }

            strip.AddChild(actBox);
        }

        return strip;
    }

    /// <summary>Per-summary, per-floor eligibility results. Rebuilding the visible rows (every
    /// filter/sort/search change) would otherwise re-run reconstructions for pills already checked.
    /// Keyed by summary identity via a ConditionalWeakTable rather than a plain (path, floor)
    /// dictionary: when DojoRunIndex re-summarizes a changed file (mtime bump — e.g. Steam Cloud
    /// swapping a run file, which §5h shows really happens), the old summary and its cached verdicts
    /// simply fall away, so this can neither serve stale eligibility nor grow past the live run set.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DojoRunSummary, Dictionary<int, bool>>
        PillEligibilityCache = new();

    private Control BuildFightPill(
        DojoRunSummary run, RunHistory history, DojoFightSummary fight,
        IReadOnlyList<MapPointHistoryEntry> floors)
    {
        string name = DojoDisplayNames.ForSearch(fight.DisplayId);

        Dictionary<int, bool> runCache = PillEligibilityCache.GetOrCreateValue(run);
        if (!runCache.TryGetValue(fight.GlobalFloor, out bool eligible))
        {
            eligible = fight.GlobalFloor >= 1 && fight.GlobalFloor <= floors.Count
                && DojoFloorEligibility.IsEligible(history, floors[fight.GlobalFloor - 1], fight.GlobalFloor);
            runCache[fight.GlobalFloor] = eligible;
        }

        var pill = new DojoFightPill();
        pill.Configure(name, fight.EncounterId, fight.RoomType, fight.WasDeathFight, eligible);
        if (eligible)
        {
            int floor = fight.GlobalFloor;
            pill.Released += _ => TaskHelper.RunSafely(DojoReplayConfirmation.ConfirmAndLaunch(
                history, floor, $"Replay {name}? (Floor {floor})"));
        }
        else
        {
            pill.Disable();
        }
        return pill;
    }

    private Control? BuildDeathQuote(DojoRunSummary run, RunHistory history)
    {
        if (run.Win)
        {
            return null;
        }

        try
        {
            GameOverType gameOverType = NRunHistory.GetGameOverType(history);
            string quote = NRunHistory.GetDeathQuote(history, run.CharacterId, gameOverType);
            // The quote comes back with the game's rich-text/glyph markup; this screen renders plain
            // labels, so strip any [tags] and show the bare sentence.
            quote = Regex.Replace(quote, @"\[.*?\]", string.Empty).Trim();
            if (quote.Length == 0)
            {
                return null;
            }
            return DojoUi.MakeLabel(quote, 15, new Color("D08770"));
        }
        catch (Exception e)
        {
            MainFile.Logger.Info("[STS2Dojo] Could not build death quote: " + e.Message);
            return null;
        }
    }

    private Control BuildRowMeta(DojoRunSummary run)
    {
        var meta = new VBoxContainer();
        meta.AddThemeConstantOverride("separation", 4);
        meta.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        meta.CustomMinimumSize = new Vector2(215, 0);

        DateTime local = DateTimeOffset.FromUnixTimeSeconds(run.StartTime).ToLocalTime().DateTime;
        meta.AddChild(RightLabel(local.ToString("MMM d, yyyy"), 17, StsColors.cream));
        meta.AddChild(RightLabel(local.ToString("h:mm tt"), 14, MutedText));

        string duration;
        try
        {
            duration = TimeFormatting.Format(run.RunTimeSeconds);
        }
        catch (Exception)
        {
            duration = TimeSpan.FromSeconds(run.RunTimeSeconds).ToString(@"h\:mm\:ss");
        }
        meta.AddChild(RightLabel($"{duration}   {run.DeckCount} cards   {run.RelicCount} relics", 14, MutedText));
        meta.AddChild(RightLabel($"Seed {run.Seed}", 13, FaintText));

        var filler = new Control();
        filler.CustomMinimumSize = new Vector2(0, 6);
        meta.AddChild(filler);

        var viewAll = DojoUi.MakeChip("View All Combats →", compact: true);
        viewAll.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        viewAll.Released += _ => OpenFullRunHistory(run);
        meta.AddChild(viewAll);

        return meta;
    }

    private static Label RightLabel(string text, int size, Color color)
    {
        Label label = DojoUi.MakeLabel(text, size, color);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    // ------------------------------------------------------------------ actions

    private static void OpenFullRunHistory(DojoRunSummary run)
    {
        NGame? game = NGame.Instance;
        if (game == null)
        {
            return;
        }

        // Pushes the stock NRunHistory on top of this screen (hiding it) and selects this run; backing
        // out pops back here. This is the one remaining path that flips IsRunningModded — all its
        // restore hooks and the §5h save safety net live in DojoRunBrowser.
        DojoRunBrowser.OpenAtRun(game, System.IO.Path.GetFileName(run.FilePath));
    }
}

/// <summary>Shared helpers for the Dojo screen's code-built widgets: the game UI font (every
/// code-created label needs an explicit font override — MegaLabel._Ready throws without one, and plain
/// Labels would otherwise fall back to Godot's default font) and label/chip factories.</summary>
internal static class DojoUi
{
    private static Font? _uiFont;
    private static bool _fontLookupAttempted;

    /// <summary>The game's regular UI font, extracted once from a popup button label (the same template
    /// node DojoCompletionScreen borrows). Null if the lookup fails — labels then inherit whatever the
    /// project theme provides.</summary>
    internal static Font? UiFont
    {
        get
        {
            if (_fontLookupAttempted)
            {
                return _uiFont;
            }

            _fontLookupAttempted = true;
            try
            {
                NGenericPopup? popup = NGenericPopup.Create();
                if (popup != null)
                {
                    var template = popup.GetNode<MegaLabel>("VerticalPopup/YesButton/%Label");
                    _uiFont = template.GetThemeFont("font");
                    popup.QueueFreeSafely();
                }
            }
            catch (Exception e)
            {
                MainFile.Logger.Error("[STS2Dojo] Could not extract the game UI font: " + e);
            }
            return _uiFont;
        }
    }

    internal static Label MakeLabel(string text, int fontSize, Color color)
    {
        var label = new Label();
        label.Text = text;
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        Font? font = UiFont;
        if (font != null)
        {
            label.AddThemeFontOverride("font", font);
        }
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }

    internal static float MeasureTextWidth(string text, int fontSize)
    {
        Font? font = UiFont;
        if (font != null)
        {
            return font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
        }
        return text.Length * fontSize * 0.62f;
    }

    internal static DojoChip MakeChip(string text, bool compact)
    {
        var chip = new DojoChip();
        chip.Configure(text, compact);
        return chip;
    }

    internal static DojoChip MakeCharacterChip(ModelId characterId, string tooltip)
    {
        var chip = new DojoChip();
        Control icon = MakeCharacterIcon(characterId, 32f);
        chip.Configure(string.Empty, compact: true, icon);
        chip.TooltipText = tooltip;
        return chip;
    }

    internal static Control MakeCharacterToken(ModelId characterId, Color color)
    {
        var token = new PanelContainer();
        token.CustomMinimumSize = new Vector2(58, 58);
        token.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        token.MouseFilter = Control.MouseFilterEnum.Ignore;

        StyleBoxFlat style = NDojoScreen.MakePanelStyle(color with { A = 0.30f }, color, 29);
        style.SetBorderWidthAll(3);
        style.SetContentMarginAll(5);
        token.AddThemeStyleboxOverride("panel", style);

        Control icon = MakeCharacterIcon(characterId, 48f);
        icon.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        icon.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        token.AddChild(icon);
        return token;
    }

    private static Control MakeCharacterIcon(ModelId characterId, float size)
    {
        Vector2 minimumSize = new(size, size);
        Texture2D? texture = ResolveCharacterIconTexture(characterId);
        if (texture != null)
        {
            var rect = new TextureRect();
            rect.Texture = texture;
            rect.CustomMinimumSize = minimumSize;
            rect.ExpandMode = (TextureRect.ExpandModeEnum)1;
            rect.StretchMode = (TextureRect.StretchModeEnum)4;
            rect.MouseFilter = Control.MouseFilterEnum.Ignore;
            return rect;
        }

        string fallbackText = DojoDisplayNames.Character(characterId) is { Length: > 0 } name ? name[..1] : "?";
        var fallback = MakeLabel(fallbackText, Mathf.RoundToInt(size * 0.56f), StsColors.cream);
        fallback.CustomMinimumSize = minimumSize;
        fallback.HorizontalAlignment = HorizontalAlignment.Center;
        fallback.VerticalAlignment = VerticalAlignment.Center;
        return fallback;
    }

    private static Texture2D? ResolveCharacterIconTexture(ModelId characterId)
    {
        try
        {
            return ModelDb.GetByIdOrNull<CharacterModel>(characterId)?.IconTexture;
        }
        catch (Exception e)
        {
            MainFile.Logger.Info("[STS2Dojo] Could not resolve character icon for " + characterId + ": " + e.Message);
            return null;
        }
    }
}

/// <summary>
/// A small rounded selectable chip button (sidebar filters, sort row, "View All Combats"). Drawn with
/// StyleBoxFlat in _Draw — no scene assets. Selected chips get a gold border and brighter text.
/// </summary>
public partial class DojoChip : NButton
{
    private static readonly Color ChipBg = new("222933");
    private static readonly Color ChipBgHover = new("2C3542");
    private static readonly Color ChipBgSelected = new("3A3524");
    private static readonly Color ChipBorder = new("343D49");

    private Label? _label;
    private Control? _icon;
    private bool _selected;
    private bool _hovered;

    protected override string[] Hotkeys => Array.Empty<string>();

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            RefreshVisuals();
        }
    }

    public void Configure(string text, bool compact, Control? icon = null)
    {
        int fontSize = compact ? 15 : 18;
        bool iconOnly = icon != null && text.Length == 0;
        if (icon != null)
        {
            _icon = icon;
            _icon.MouseFilter = MouseFilterEnum.Ignore;
            _icon.SetAnchorsPreset(LayoutPreset.FullRect);
            _icon.OffsetLeft = iconOnly ? 5 : 8;
            _icon.OffsetTop = iconOnly ? 4 : 8;
            _icon.OffsetRight = iconOnly ? -5 : -8;
            _icon.OffsetBottom = iconOnly ? -4 : -8;
            AddChild(_icon);
        }
        if (text.Length > 0)
        {
            _label = DojoUi.MakeLabel(text, fontSize, StsColors.cream);
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Center;
            _label.SetAnchorsPreset(LayoutPreset.FullRect);
            _label.OffsetLeft = compact ? 14 : 18;
            _label.OffsetRight = compact ? -14 : -18;
            AddChild(_label);
        }

        float width = iconOnly
            ? 46
            : DojoUi.MeasureTextWidth(text, fontSize) + (compact ? 30 : 38);
        CustomMinimumSize = new Vector2(width, compact ? 36 : 48);
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        ConnectSignals();
        RefreshVisuals();
    }

    public override void _Draw()
    {
        Color bg = _selected ? ChipBgSelected : _hovered ? ChipBgHover : ChipBg;
        Color border = _selected ? StsColors.gold : ChipBorder;
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(bg, border, (int)(Size.Y / 2f));
        style.SetBorderWidthAll(_selected ? 2 : 1);
        style.Draw(GetCanvasItem(), new Rect2(Vector2.Zero, Size));
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        _hovered = true;
        RefreshVisuals();
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        _hovered = false;
        RefreshVisuals();
    }

    private void RefreshVisuals()
    {
        if (_label != null)
        {
            _label.AddThemeColorOverride("font_color",
                _selected ? StsColors.gold : IsEnabled ? StsColors.cream : StsColors.gray);
        }
        if (_icon is CanvasItem iconCanvas)
        {
            iconCanvas.Modulate = IsEnabled
                ? (_selected ? Colors.White : new Color(0.78f, 0.82f, 0.86f))
                : StsColors.gray;
        }
        QueueRedraw();
    }
}

/// <summary>The sidebar Back chip: a DojoChip that also registers the game's cancel/back hotkeys, so
/// ESC / controller-B close the Dojo screen like any stock submenu (whose scene-baked NBackButton
/// normally provides this). Hotkey presses are visibility-gated by NClickableControl itself, so a
/// stacked NRunHistory on top doesn't double-pop.</summary>
public partial class DojoBackChip : DojoChip
{
    protected override string[] Hotkeys =>
    [
        MegaInput.cancel.ToString(),
        MegaInput.pauseAndBack.ToString(),
        MegaInput.back.ToString()
    ];
}

/// <summary>
/// A boss/elite fight pill on a run row. The icon comes from the same run-history map art as the stock
/// history screen; the pill border is green for cleared fights and red for the fight that killed the player.
/// Ineligible fights (content that no longer resolves — see DojoFloorEligibility) render dimmed with no
/// click handler.
/// </summary>
public partial class DojoFightPill : NButton
{
    private const int FightFontSize = 15;
    private const float PillHeight = 34f;
    private const float IconSize = 23f;
    private const float IconLeft = 11f;
    private const float LabelLeft = 42f;
    private const float LabelRight = 14f;

    private static readonly Color PillBg = new("242B35");
    private static readonly Color PillBgHover = new("30394A");
    private static readonly Color PillBorder = new("39424F");
    private static readonly Color ClearedMarker = new("58C776");
    private static readonly Color DeathMarker = new("E05555");

    private Label _label = null!;
    private bool _death;
    private bool _eligible;
    private bool _hovered;

    protected override string[] Hotkeys => Array.Empty<string>();

    public void Configure(string text, ModelId encounterId, RoomType roomType, bool wasDeathFight, bool eligible)
    {
        _death = wasDeathFight;
        _eligible = eligible;

        AddEncounterIcon(encounterId, roomType);

        Color textColor = eligible ? StsColors.cream : StsColors.gray;
        _label = DojoUi.MakeLabel(text, FightFontSize, textColor);
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.SetAnchorsPreset(LayoutPreset.FullRect);
        _label.OffsetLeft = LabelLeft;
        _label.OffsetRight = -LabelRight;
        AddChild(_label);

        CustomMinimumSize = new Vector2(
            Mathf.Ceil(DojoUi.MeasureTextWidth(text, FightFontSize) + LabelLeft + LabelRight),
            PillHeight);
        FocusMode = eligible ? FocusModeEnum.All : FocusModeEnum.None;
        MouseFilter = MouseFilterEnum.Stop;

        if (!eligible)
        {
            Modulate = StsColors.disabledTopBarButton;
            TooltipText = string.Empty;
        }
    }

    private void AddEncounterIcon(ModelId encounterId, RoomType roomType)
    {
        MapPointType mapPointType = roomType switch
        {
            RoomType.Boss => MapPointType.Boss,
            RoomType.Elite => MapPointType.Elite,
            _ => MapPointType.Monster
        };
        ModelId? iconModelId = roomType == RoomType.Boss ? encounterId : null;

        string? iconPath = ImageHelper.GetRoomIconPath(mapPointType, roomType, iconModelId);
        if (iconPath != null)
        {
            Texture2D icon = PreloadManager.Cache.GetCompressedTexture2D(iconPath);
            AddChild(MakeIconRect(icon, Colors.White));
        }
    }

    private static TextureRect MakeIconRect(Texture2D texture, Color modulate)
    {
        var rect = new TextureRect();
        rect.Texture = texture;
        rect.ExpandMode = (TextureRect.ExpandModeEnum)1;
        rect.StretchMode = (TextureRect.StretchModeEnum)4;
        rect.MouseFilter = MouseFilterEnum.Ignore;
        rect.Position = new Vector2(IconLeft, (PillHeight - IconSize) / 2f);
        rect.Size = new Vector2(IconSize, IconSize);
        rect.Modulate = modulate;
        return rect;
    }

    public override void _Ready()
    {
        ConnectSignals();
    }

    public override void _Draw()
    {
        Color bg = _eligible && _hovered ? PillBgHover : PillBg;
        Color border = _eligible ? (_death ? DeathMarker : ClearedMarker) : PillBorder;
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(bg, border, (int)(Size.Y / 2f));
        style.SetBorderWidthAll(_eligible ? 2 : 1);
        style.Draw(GetCanvasItem(), new Rect2(Vector2.Zero, Size));
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        if (_eligible)
        {
            _hovered = true;
            QueueRedraw();
        }
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        _hovered = false;
        QueueRedraw();
    }
}
