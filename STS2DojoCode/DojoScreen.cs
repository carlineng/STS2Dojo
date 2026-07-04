using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
using SizeFlags = Godot.Control.SizeFlags;

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
/// <c>UserDataPathProvider.IsRunningModded</c> — see CLAUDE.md §5h/§6. Each row
/// (<see cref="DojoRunRow"/>) expands in place to the full per-act floor map, so there is no drill-in to
/// the stock <c>NRunHistory</c> screen and no flag flip anywhere in the Dojo.
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
    internal static readonly Color RowColor = new("1A1F27F2");
    internal static readonly Color RowBorderColor = new("2A313B");
    internal static readonly Color MutedText = new("9AA3AE");
    internal static readonly Color FaintText = new("6B7480");

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
        // Guard against the cancel/back hotkey while some other submenu is stacked on top — only pop when
        // this screen is actually the top of the stack.
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
        DojoChip allCharacterChip = DojoUi.MakeChip("All", compact: true);
        // Match the taller character-icon circles so the row stays aligned.
        allCharacterChip.CustomMinimumSize = new Vector2(allCharacterChip.CustomMinimumSize.X, 52);
        AddFilterChip(characterGrid, _characterChips, allCharacterChip, selected: true,
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
        DojoChip chip = DojoUi.MakeCharacterChip(characterId);
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
            // the sidecar cache path and live content hash are resolved here for the same reason. Parsing
            // changed .run files is then CPU-only work, so it runs off-thread. Godot's synchronization
            // context resumes this method on the main thread for the UI work below.
            string? directory = DojoRunIndex.TryGetRealHistoryDirectory();
            string? cachePath = DojoRunIndex.TryGetCachePath();
            string? eligibilityContentHash = DojoRunIndex.TryGetEligibilityContentHash();
            DojoRunIndexResult result =
                await Task.Run(() => DojoRunIndex.LoadAll(directory, cachePath, eligibilityContentHash));
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
            DojoRunRow row = new DojoRunRow().Init(_visibleRuns[i]);
            _rowContainer.AddChild(row.Root);
        }
        _rowsBuilt = end;
    }
}

/// <summary>
/// One run's row in the Dojo browser, collapsed by default and expandable IN PLACE to the full per-act
/// floor map. This replaces the old "View All Combats" drill-in to the stock <c>NRunHistory</c> screen —
/// and with it the Dojo's last <c>UserDataPathProvider.IsRunningModded</c> flip (CLAUDE.md §5i/§6).
///
/// COLLAPSED: character badge, identity header, the per-act boss/elite highlight-pill strip, the death
/// quote, and a right-hand meta column whose last control is the expand toggle.
///
/// EXPANDED: a full-width identity header (with the seed, since the meta column is gone) plus one
/// horizontal strip of stock <see cref="NMapPointHistoryEntry"/> icons per act — the same widget the stock
/// run-history screen uses. Reusing it means the room icons, the per-floor hover tooltip, the
/// ineligible-combat grey-out and the click-to-replay wiring all come from <see cref="DojoFloorClickPatch"/>
/// for free. A wide act (the strip can be ~18 floors) is kept inside the row's width by wrapping each act's
/// strip in its own horizontal <see cref="ScrollContainer"/>, so the outer list's disabled horizontal
/// scroll is never regressed.
///
/// The expanded view is built lazily on first expand and freed on collapse, so those stock floor-icon nodes
/// are never pinned beyond what the player actually has open (CLAUDE.md §5i's "don't hold strong
/// NMapPointHistoryEntry references" rule). <c>SetPlayer</c> is required for the hover tooltip
/// (<c>NMapPointHistoryEntry.OnFocus</c> throws without it) and is called right after the strip enters the
/// tree: on the main thread <c>AddChild</c> runs the icons' <c>_Ready</c> synchronously, so their
/// %QuestIcon/texture references exist by then (this mirrors <c>NRunHistory.DisplayRun</c> → <c>SelectPlayer</c>).
///
/// NOT a Node subclass: mod C# classes that derive directly from a Godot built-in (rather than from a
/// game class like <c>NButton</c>/<c>NSubmenu</c>) get a broken script-dispatch bridge in the modded game —
/// every engine call into them throws, and the engine renders the swallowed exception as a literal
/// "&lt;null&gt;" native tooltip on hover (CLAUDE.md §5m). So this is a plain class owning a script-less
/// <see cref="PanelContainer"/> (<see cref="Root"/>); the toggle/pill closures keep the instance alive for
/// exactly as long as its root is in the tree.
/// </summary>
public sealed class DojoRunRow
{
    private const float ActLabelWidth = 108f;

    private static readonly Color DeathQuoteColor = new("D08770");

    /// <summary>Per-summary, per-floor eligibility results for the collapsed pill strip. Rebuilding the
    /// visible rows (every filter/sort/search change) would otherwise re-run reconstructions for pills
    /// already checked. Keyed by summary identity via a <see cref="ConditionalWeakTable{TKey,TValue}"/>
    /// rather than a plain (path, floor) dictionary: when DojoRunIndex re-summarizes a changed file (mtime
    /// bump — e.g. Steam Cloud swapping a run file, which §5h shows really happens), the old summary and its
    /// cached verdicts simply fall away, so this can neither serve stale eligibility nor grow past the live
    /// run set. (The expanded floor map's per-floor verdicts come from <see cref="DojoFloorClickPatch"/>,
    /// which runs <see cref="DojoFloorEligibility"/> — itself snapshot-cached — at icon-create time.)</summary>
    private static readonly ConditionalWeakTable<DojoRunSummary, Dictionary<int, bool>> PillEligibilityCache = new();

    private DojoRunSummary _run = null!;
    private MarginContainer _bodyHost = null!;

    private RunHistory? _history;
    private bool _historyLoaded;
    private bool _expanded;
    private IReadOnlyList<MapPointHistoryEntry>? _flatFloors;

    /// <summary>The row's actual node — a script-less PanelContainer this class builds into. This is what
    /// NDojoScreen adds to the run list; freeing it (list rebuild) releases the whole row.</summary>
    public PanelContainer Root { get; } = new();

    /// <summary>Builds the row for <paramref name="run"/> (collapsed).</summary>
    public DojoRunRow Init(DojoRunSummary run)
    {
        _run = run;
        Root.AddThemeStyleboxOverride("panel",
            NDojoScreen.MakePanelStyle(NDojoScreen.RowColor, NDojoScreen.RowBorderColor, 10));
        Root.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        _bodyHost = new MarginContainer();
        _bodyHost.AddThemeConstantOverride("margin_left", 20);
        _bodyHost.AddThemeConstantOverride("margin_right", 20);
        _bodyHost.AddThemeConstantOverride("margin_top", 14);
        _bodyHost.AddThemeConstantOverride("margin_bottom", 14);
        _bodyHost.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Root.AddChild(_bodyHost);

        RenderCollapsed();
        return this;
    }

    /// <summary>The full parsed run, loaded on demand (weakly cached in the summary — see
    /// DojoRunSummary.RunSource) and held for this row's lifetime; null if the file has become unreadable
    /// since the index was built, in which case rows degrade to a header-only message.</summary>
    private RunHistory? History()
    {
        if (_historyLoaded)
        {
            return _history;
        }
        _historyLoaded = true;
        try
        {
            _history = _run.GetRun();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[STS2Dojo] Could not re-load run file '{_run.FilePath}': {e.Message}");
        }
        return _history;
    }

    private void ClearBody()
    {
        foreach (Node child in _bodyHost.GetChildren())
        {
            _bodyHost.RemoveChild(child);
            child.QueueFreeSafely();
        }
    }

    private void Toggle()
    {
        _expanded = !_expanded;
        if (_expanded)
        {
            RenderExpanded();
        }
        else
        {
            RenderCollapsed();
        }
    }

    private DojoChip MakeToggleChip(string text)
    {
        DojoChip chip = DojoUi.MakeChip(text, compact: true);
        chip.Released += _ => Toggle();
        return chip;
    }

    // ------------------------------------------------------------------ collapsed

    private void RenderCollapsed()
    {
        ClearBody();

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 18);
        columns.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _bodyHost.AddChild(columns);

        columns.AddChild(BuildCharacterBadge());

        var center = new VBoxContainer();
        center.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        center.AddThemeConstantOverride("separation", 8);
        columns.AddChild(center);
        center.AddChild(BuildIdentityHeader(inlineAscension: false, includeSeed: false));

        if (_run.Acts.Count > 0)
        {
            center.AddChild(BuildActStrip());
            Control? quote = BuildDeathQuote();
            if (quote != null)
            {
                center.AddChild(quote);
            }
        }
        else
        {
            center.AddChild(DojoUi.MakeLabel("Run file could not be read.", 15, NDojoScreen.FaintText));
        }

        columns.AddChild(BuildRowMeta());
    }

    private Control BuildCharacterBadge()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        box.SizeFlagsVertical = SizeFlags.ShrinkBegin;

        box.AddChild(DojoUi.MakeCharacterToken(_run.CharacterId, ResolveCharacterColor(_run.CharacterId)));

        var ascension = DojoUi.MakeLabel($"A{_run.Ascension}", 15, StsColors.gold);
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
                // Dim the avatar-ring color a notch — the character name colors (e.g. The Silent's
                // green) read as too bright/neon as a solid circle outline at this size.
                return model.NameColor.Darkened(0.18f);
            }
        }
        catch (Exception)
        {
            // fall through to the neutral default
        }
        return StsColors.gray;
    }

    /// <summary>The identity line shared by both states: character, VICTORY/DEFEAT/ABANDONED, floor, HP.
    /// The expanded state additionally prefixes the ascension (there's no badge column there) and appends
    /// the seed (the meta column that normally carries it is gone).</summary>
    private HBoxContainer BuildIdentityHeader(bool inlineAscension, bool includeSeed)
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 14);

        if (inlineAscension)
        {
            header.AddChild(DojoUi.MakeLabel($"A{_run.Ascension}", 16, StsColors.gold));
        }

        header.AddChild(DojoUi.MakeLabel(DojoDisplayNames.Character(_run.CharacterId), 22, StsColors.cream));

        (string outcomeText, Color outcomeColor) = _run.Win
            ? ("VICTORY", StsColors.green)
            : _run.WasAbandoned
                ? ("ABANDONED", StsColors.orange)
                : ("DEFEAT", StsColors.red);
        header.AddChild(DojoUi.MakeLabel(outcomeText, 16, outcomeColor));

        header.AddChild(DojoUi.MakeLabel($"Floor {_run.FloorsReached}", 17, NDojoScreen.MutedText));
        Color hpColor = _run.EndHp <= 0 ? StsColors.red : NDojoScreen.MutedText;
        header.AddChild(DojoUi.MakeLabel($"HP {_run.EndHp} / {_run.EndMaxHp}", 17, hpColor));

        if (includeSeed)
        {
            header.AddChild(DojoUi.MakeLabel($"Seed {_run.Seed}", 15, NDojoScreen.FaintText));
        }
        return header;
    }

    private Control BuildActStrip()
    {
        var strip = new HBoxContainer();
        strip.AddThemeConstantOverride("separation", 26);
        strip.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        foreach (DojoActSummary act in _run.Acts)
        {
            var actBox = new VBoxContainer();
            actBox.AddThemeConstantOverride("separation", 5);

            string actName = act.ActId != null
                ? DojoDisplayNames.Act(act.ActId).ToUpperInvariant()
                : $"ACT {act.ActIndex + 1}";
            actBox.AddChild(DojoUi.MakeLabel(actName, 13, NDojoScreen.FaintText));

            var fightColumn = new VBoxContainer();
            fightColumn.AddThemeConstantOverride("separation", 5);
            actBox.AddChild(fightColumn);

            var fights = act.DisplayFights
                .OrderBy(fight => fight.GlobalFloor)
                .ToList();
            if (fights.Count == 0)
            {
                fightColumn.AddChild(DojoUi.MakeLabel("Boss not reached", 14, NDojoScreen.FaintText));
            }
            foreach (DojoFightSummary fight in fights)
            {
                fightColumn.AddChild(BuildFightPill(fight));
            }

            strip.AddChild(actBox);
        }

        return strip;
    }

    private Control BuildFightPill(DojoFightSummary fight)
    {
        string name = DojoDisplayNames.ForSearch(fight.DisplayId);

        bool eligible = IsFightPillEligible(fight);

        var pill = new DojoFightPill();
        pill.Configure(name, fight.EncounterId, fight.RoomType, fight.WasDeathFight, eligible);
        if (eligible)
        {
            int floor = fight.GlobalFloor;
            pill.Released += _ => TaskHelper.RunSafely(ConfirmAndLaunch(floor, name));
        }
        else
        {
            pill.Disable();
        }
        return pill;
    }

    private bool IsFightPillEligible(DojoFightSummary fight)
    {
        if (_run.CachedFightEligibility.TryGetValue(fight.GlobalFloor, out bool cached))
        {
            return cached;
        }

        Dictionary<int, bool> runCache = PillEligibilityCache.GetOrCreateValue(_run);
        if (runCache.TryGetValue(fight.GlobalFloor, out bool eligible))
        {
            return eligible;
        }

        RunHistory? history = History();
        IReadOnlyList<MapPointHistoryEntry>? floors = FlatFloors(history);
        eligible = history != null
            && floors != null
            && fight.GlobalFloor >= 1
            && fight.GlobalFloor <= floors.Count
            && DojoFloorEligibility.IsEligible(history, floors[fight.GlobalFloor - 1], fight.GlobalFloor);
        runCache[fight.GlobalFloor] = eligible;
        DojoRunIndex.RememberFightEligibility(_run, fight.GlobalFloor, eligible);
        return eligible;
    }

    private async Task ConfirmAndLaunch(int floor, string name)
    {
        RunHistory? history = History();
        if (history == null)
        {
            return;
        }

        await DojoReplayConfirmation.ConfirmAndLaunch(history, floor, $"Replay {name}? (Floor {floor})");
    }

    private IReadOnlyList<MapPointHistoryEntry>? FlatFloors(RunHistory? history)
    {
        if (history == null)
        {
            return null;
        }

        return _flatFloors ??= RunHistoryQueries.FlattenFloors(history);
    }

    private Control? BuildDeathQuote()
    {
        if (_run.Win)
        {
            return null;
        }

        try
        {
            GameOverType gameOverType = GetGameOverType(_run);
            var quoteRun = new RunHistory
            {
                Win = _run.Win,
                WasAbandoned = _run.WasAbandoned,
                Seed = _run.Seed,
                KilledByEncounter = _run.KilledByEncounterId ?? ModelId.none,
                KilledByEvent = _run.KilledByEventId ?? ModelId.none
            };
            string quote = NRunHistory.GetDeathQuote(quoteRun, _run.CharacterId, gameOverType);
            // The quote comes back with the game's rich-text/glyph markup; this screen renders plain
            // labels, so strip any [tags] and show the bare sentence.
            quote = Regex.Replace(quote, @"\[.*?\]", string.Empty).Trim();
            if (quote.Length == 0)
            {
                return null;
            }
            return DojoUi.MakeLabel(quote, 15, DeathQuoteColor);
        }
        catch (Exception e)
        {
            MainFile.Logger.Info("[STS2Dojo] Could not build death quote: " + e.Message);
            return null;
        }
    }

    private static GameOverType GetGameOverType(DojoRunSummary run)
    {
        if (run.Win)
        {
            return GameOverType.FalseVictory;
        }
        if (run.WasAbandoned)
        {
            return GameOverType.AbandonedRun;
        }
        if (run.KilledByEncounterId != null && run.KilledByEncounterId != ModelId.none)
        {
            return GameOverType.CombatDeath;
        }
        if (run.KilledByEventId != null && run.KilledByEventId != ModelId.none)
        {
            return GameOverType.EventDeath;
        }
        return GameOverType.None;
    }

    private Control BuildRowMeta()
    {
        var meta = new VBoxContainer();
        meta.AddThemeConstantOverride("separation", 4);
        meta.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        meta.CustomMinimumSize = new Vector2(215, 0);

        DateTime local = DateTimeOffset.FromUnixTimeSeconds(_run.StartTime).ToLocalTime().DateTime;
        meta.AddChild(RightLabel(local.ToString("MMM d, yyyy"), 17, StsColors.cream));
        meta.AddChild(RightLabel(local.ToString("h:mm tt"), 14, NDojoScreen.MutedText));

        string duration;
        try
        {
            duration = TimeFormatting.Format(_run.RunTimeSeconds);
        }
        catch (Exception)
        {
            duration = TimeSpan.FromSeconds(_run.RunTimeSeconds).ToString(@"h\:mm\:ss");
        }
        meta.AddChild(RightLabel($"{duration}   {_run.DeckCount} cards   {_run.RelicCount} relics", 14, NDojoScreen.MutedText));
        meta.AddChild(RightLabel($"Seed {_run.Seed}", 13, NDojoScreen.FaintText));

        var filler = new Control();
        filler.CustomMinimumSize = new Vector2(0, 6);
        meta.AddChild(filler);

        DojoChip toggle = MakeToggleChip("View All Combats");
        toggle.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        meta.AddChild(toggle);

        return meta;
    }

    private static Label RightLabel(string text, int size, Color color)
    {
        Label label = DojoUi.MakeLabel(text, size, color);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    // ------------------------------------------------------------------ expanded floor map

    private void RenderExpanded()
    {
        ClearBody();

        var column = new VBoxContainer();
        column.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        column.AddThemeConstantOverride("separation", 12);
        _bodyHost.AddChild(column);

        HBoxContainer header = BuildIdentityHeader(inlineAscension: true, includeSeed: true);
        var spacer = new Control();
        spacer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(spacer);
        header.AddChild(MakeToggleChip("Hide Combats"));
        column.AddChild(header);

        RunHistory? history = History();
        if (history == null)
        {
            column.AddChild(DojoUi.MakeLabel("Run file could not be read.", 15, NDojoScreen.FaintText));
            return;
        }

        var entries = new List<NMapPointHistoryEntry>();
        Control floorMap = BuildFloorMap(history, entries);
        // Adding the map (built off-tree above) to the in-tree column runs every icon's _Ready
        // synchronously on the main thread, so SetPlayer — which the hover tooltip requires and which reads
        // nodes populated in _Ready — is safe immediately after (mirrors NRunHistory.DisplayRun).
        column.AddChild(floorMap);

        RunHistoryPlayer player = history.Players[0];
        foreach (NMapPointHistoryEntry entry in entries)
        {
            entry.SetPlayer(player);
        }
    }

    private Control BuildFloorMap(RunHistory history, List<NMapPointHistoryEntry> entries)
    {
        var map = new VBoxContainer();
        map.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        map.AddThemeConstantOverride("separation", 8);

        int baseFloor = 1;
        for (int actIndex = 0; actIndex < history.MapPointHistory.Count; actIndex++)
        {
            List<MapPointHistoryEntry> floors = history.MapPointHistory[actIndex];

            var actRow = new HBoxContainer();
            actRow.AddThemeConstantOverride("separation", 12);
            actRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            actRow.AddChild(BuildActLabel(history, actIndex));

            var strip = new HBoxContainer();
            strip.AddThemeConstantOverride("separation", 6);
            strip.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            for (int i = 0; i < floors.Count; i++)
            {
                NMapPointHistoryEntry entry = DojoFloorClickPatch.CreateDojoEntry(history, floors[i], baseFloor + i);
                entries.Add(entry);
                strip.AddChild(entry);
            }
            baseFloor += floors.Count;

            // A single act can be ~18 floors wide — wrap the strip in its own horizontal scroll so it never
            // forces the row (and thus the outer, horizontal-scroll-disabled list) wider than the viewport.
            // Vertical scroll disabled so a vertical mouse-wheel keeps scrolling the outer run list.
            var scroll = new ScrollContainer();
            scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            scroll.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Auto;
            scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
            scroll.AddChild(strip);
            actRow.AddChild(scroll);

            map.AddChild(actRow);
        }

        return map;
    }

    private static Control BuildActLabel(RunHistory history, int actIndex)
    {
        string actName = actIndex < history.Acts.Count
            ? DojoDisplayNames.Act(history.Acts[actIndex]).ToUpperInvariant()
            : $"ACT {actIndex + 1}";
        Label label = DojoUi.MakeLabel(actName, 13, NDojoScreen.FaintText);
        label.CustomMinimumSize = new Vector2(ActLabelWidth, 0);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return label;
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

    internal static DojoChip MakeCharacterChip(ModelId characterId)
    {
        var chip = new DojoChip();
        Control icon = MakeCharacterIcon(characterId, 32f);
        chip.Configure(string.Empty, compact: true, icon);
        return chip;
    }

    internal static Control MakeCharacterToken(ModelId characterId, Color color)
    {
        var token = new PanelContainer();
        token.CustomMinimumSize = new Vector2(64, 64);
        token.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        token.MouseFilter = Control.MouseFilterEnum.Ignore;

        StyleBoxFlat style = NDojoScreen.MakePanelStyle(color with { A = 0.30f }, color, 32);
        style.SetBorderWidthAll(3);
        style.SetContentMarginAll(8);
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
            _icon.OffsetLeft = iconOnly ? 11 : 8;
            _icon.OffsetTop = iconOnly ? 11 : 8;
            _icon.OffsetRight = iconOnly ? -11 : -8;
            _icon.OffsetBottom = iconOnly ? -11 : -8;
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
            ? 52
            : DojoUi.MeasureTextWidth(text, fontSize) + (compact ? 30 : 38);
        float height = iconOnly ? 52 : (compact ? 36 : 48);
        CustomMinimumSize = new Vector2(width, height);
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
/// normally provides this). Hotkey presses are visibility-gated by NClickableControl itself, so another
/// submenu stacked on top doesn't double-pop.</summary>
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
