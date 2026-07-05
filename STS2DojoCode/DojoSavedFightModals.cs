using System;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.sts2.Core.Nodes.TopBar;
using SizeFlags = Godot.Control.SizeFlags;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// A small code-built yes/no confirmation modal (the §12h "confirm before deleting a saved fight" ask).
/// Shown over <see cref="NDojoScreen"/> via <see cref="NModalContainer"/>, the same mechanism the Replay
/// Setup / Completion screens use.
///
/// §5m (CLAUDE.md) applies exactly as it does to <see cref="DojoCompletionScreen"/>/
/// <see cref="NDojoReplaySetupModal"/>: this must be a node (<c>NModalContainer.Add</c> hard-casts to
/// <see cref="IScreenContext"/>), its script dispatch may be broken in the modded game, so nothing relies
/// on lifecycle overrides here (built imperatively from <see cref="Open"/>) and the root's mouse filter is
/// Ignore with a script-less full-rect Stop child terminating every native-tooltip walk before it can
/// ascend into this node.
/// </summary>
public partial class DojoConfirmModal : NTopBarPortrait, IScreenContext
{
    private const float PanelWidth = 520f;

    private DojoChip? _confirmChip;

    public Control? DefaultFocusedControl => _confirmChip;

    /// <summary>Shows a confirm dialog. <paramref name="onConfirm"/> runs only if the player confirms; the
    /// modal always clears itself first, so the callback runs with no modal open (a follow-up modal, if any,
    /// can then be shown).</summary>
    public static void Open(string title, string message, string confirmText, Action onConfirm)
    {
        NModalContainer? modalContainer = NModalContainer.Instance;
        if (modalContainer == null || modalContainer.OpenModal != null)
        {
            MainFile.Logger.Error("[STS2Dojo] Cannot open confirm modal: no modal container or one is already open.");
            return;
        }

        var modal = new DojoConfirmModal();
        modal.BuildLayout(title, message, confirmText, onConfirm);
        modalContainer.Add(modal);
    }

    private void BuildLayout(string title, string message, string confirmText, Action onConfirm)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // §5m — keep this node out of every native-tooltip walk

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.MouseFilter = MouseFilterEnum.Stop;
        AddChild(center);

        var panel = new PanelContainer();
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(new Color("161A20F8"), StsColors.gold with { A = 0.6f }, 14);
        style.SetBorderWidthAll(2);
        panel.AddThemeStyleboxOverride("panel", style);
        panel.CustomMinimumSize = new Vector2(PanelWidth, 0);
        center.AddChild(panel);

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 28);
        pad.AddThemeConstantOverride("margin_right", 28);
        pad.AddThemeConstantOverride("margin_top", 24);
        pad.AddThemeConstantOverride("margin_bottom", 22);
        panel.AddChild(pad);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 16);
        pad.AddChild(column);

        column.AddChild(DojoUi.MakeLabel(title, 22, StsColors.cream));

        Label body = DojoUi.MakeLabel(message, 16, NDojoScreen.MutedText);
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.CustomMinimumSize = new Vector2(PanelWidth - 56, 0);
        column.AddChild(body);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        actions.Alignment = BoxContainer.AlignmentMode.End;
        actions.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        column.AddChild(actions);

        // DojoBackChip registers the cancel/back hotkeys so ESC closes the modal; as the last-pushed
        // binding it wins while the modal is up and unregisters on close (CLAUDE.md §5i).
        var cancel = new DojoBackChip();
        cancel.Configure("Cancel", compact: false);
        cancel.Released += _ => NModalContainer.Instance?.Clear();
        actions.AddChild(cancel);

        _confirmChip = DojoUi.MakeChip(confirmText, compact: false);
        _confirmChip.Released += _ =>
        {
            NModalContainer.Instance?.Clear();
            onConfirm();
        };
        actions.AddChild(_confirmChip);
    }
}

/// <summary>
/// The §12h edit-metadata modal for a saved fight: two text fields (title + description) over Cancel/Save.
/// On Save, hands the trimmed values back through <c>onSave(title, description)</c>. Same §5m-safe node
/// structure as <see cref="DojoConfirmModal"/>.
/// </summary>
public partial class DojoEditFightModal : NTopBarPortrait, IScreenContext
{
    private const float PanelWidth = 620f;

    private LineEdit _titleBox = null!;
    private LineEdit _descriptionBox = null!;
    private DojoChip? _saveChip;

    public Control? DefaultFocusedControl => _saveChip;

    public static void Open(string title, string description, Action<string, string> onSave)
    {
        NModalContainer? modalContainer = NModalContainer.Instance;
        if (modalContainer == null || modalContainer.OpenModal != null)
        {
            MainFile.Logger.Error("[STS2Dojo] Cannot open edit modal: no modal container or one is already open.");
            return;
        }

        var modal = new DojoEditFightModal();
        modal.BuildLayout(title, description, onSave);
        modalContainer.Add(modal);
    }

    private void BuildLayout(string title, string description, Action<string, string> onSave)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // §5m

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.MouseFilter = MouseFilterEnum.Stop;
        AddChild(center);

        var panel = new PanelContainer();
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(new Color("161A20F8"), StsColors.gold with { A = 0.6f }, 14);
        style.SetBorderWidthAll(2);
        panel.AddThemeStyleboxOverride("panel", style);
        panel.CustomMinimumSize = new Vector2(PanelWidth, 0);
        center.AddChild(panel);

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 28);
        pad.AddThemeConstantOverride("margin_right", 28);
        pad.AddThemeConstantOverride("margin_top", 24);
        pad.AddThemeConstantOverride("margin_bottom", 22);
        panel.AddChild(pad);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);
        pad.AddChild(column);

        column.AddChild(DojoUi.MakeLabel("Edit Fight", 22, StsColors.cream));

        column.AddChild(DojoUi.MakeLabel("TITLE", 12, NDojoScreen.FaintText));
        _titleBox = MakeTextBox(title);
        column.AddChild(_titleBox);

        column.AddChild(DojoUi.MakeLabel("DESCRIPTION", 12, NDojoScreen.FaintText));
        _descriptionBox = MakeTextBox(description);
        column.AddChild(_descriptionBox);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        actions.Alignment = BoxContainer.AlignmentMode.End;
        actions.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        column.AddChild(actions);

        var cancel = new DojoBackChip();
        cancel.Configure("Cancel", compact: false);
        cancel.Released += _ => NModalContainer.Instance?.Clear();
        actions.AddChild(cancel);

        void Save()
        {
            string newTitle = _titleBox.Text?.Trim() ?? string.Empty;
            string newDescription = _descriptionBox.Text?.Trim() ?? string.Empty;
            NModalContainer.Instance?.Clear();
            onSave(newTitle, newDescription);
        }

        _saveChip = DojoUi.MakeChip("Save", compact: false);
        _saveChip.Released += _ => Save();
        actions.AddChild(_saveChip);

        // Enter in either field saves — same affordance as the paste box's submit.
        _titleBox.TextSubmitted += _ => Save();
        _descriptionBox.TextSubmitted += _ => Save();
    }

    private static LineEdit MakeTextBox(string text)
    {
        var box = new LineEdit();
        box.Text = text;
        box.CustomMinimumSize = new Vector2(PanelWidth - 56, 46);
        box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        box.AddThemeStyleboxOverride("normal",
            NDojoScreen.MakePanelStyle(new Color("10131A"), NDojoScreen.RowBorderColor, 8));
        box.AddThemeStyleboxOverride("focus",
            NDojoScreen.MakePanelStyle(new Color("10131A"), StsColors.gold, 8));
        box.AddThemeColorOverride("font_color", StsColors.cream);
        box.AddThemeFontSizeOverride("font_size", 16);
        if (DojoUi.UiFont is { } font)
        {
            box.AddThemeFontOverride("font", font);
        }
        return box;
    }
}
