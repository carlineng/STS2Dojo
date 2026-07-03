using System;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Dojo.STS2DojoCode;

internal static class DojoNativeTooltips
{
    private const string TooltipDelaySetting = "gui/timers/tooltip_delay_sec";
    private const double SuppressedTooltipDelaySeconds = 3600d;

    private static Variant _previousTooltipDelay;
    private static bool _hasPreviousTooltipDelay;
    private static int _nativeTooltipSuppressionDepth;

    internal static Control NullLikeCustomTooltip(string? forText) =>
        null!;

    internal static void PushNativeTooltipSuppression()
    {
        if (_nativeTooltipSuppressionDepth++ > 0)
        {
            return;
        }

        _previousTooltipDelay = ProjectSettings.GetSetting(TooltipDelaySetting, Variant.From(0.5d));
        _hasPreviousTooltipDelay = true;
        ProjectSettings.SetSetting(TooltipDelaySetting, SuppressedTooltipDelaySeconds);
        ClearNullLikePopups();
    }

    internal static void PopNativeTooltipSuppression()
    {
        if (_nativeTooltipSuppressionDepth == 0 || --_nativeTooltipSuppressionDepth > 0)
        {
            return;
        }

        if (_hasPreviousTooltipDelay)
        {
            ProjectSettings.SetSetting(TooltipDelaySetting, _previousTooltipDelay);
            _previousTooltipDelay = default;
            _hasPreviousTooltipDelay = false;
        }
    }

    internal static void ClearRecursively(Node node)
    {
        if (node is Control control)
        {
            SuppressTooltipText(control);
        }

        foreach (Node child in node.GetChildren())
        {
            ClearRecursively(child);
        }
    }

    internal static void SuppressHoveredTooltip(Control root)
    {
        SuppressTooltipText(root);

        Control? hovered = FindHoveredControl(root, root.GetGlobalMousePosition());
        for (Control? current = hovered; current != null; current = current.GetParent() as Control)
        {
            SuppressTooltipText(current);
        }
    }

    internal static void ClearNullLikePopups()
    {
        Window? root = NGame.Instance?.GetTree()?.Root;
        if (root == null)
        {
            return;
        }

        ClearNullLikePopups(root);
    }

    private static void ClearNullLikePopups(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            ClearNullLikePopups(child);
        }

        if (!IsSuppressibleTooltipLabel(node))
        {
            return;
        }

        Node? tooltipContainer = TryFindNativeTooltipContainer(node);
        if (tooltipContainer != null)
        {
            tooltipContainer.QueueFreeSafely();
        }
        else if (node is CanvasItem canvasItem)
        {
            canvasItem.Hide();
        }
    }

    private static bool IsSuppressibleTooltipLabel(Node node)
    {
        return node is Label label && IsNullLikeText(label.Text)
            || node is RichTextLabel richTextLabel && IsNullLikeText(richTextLabel.Text);
    }

    private static void SuppressTooltipText(Control control)
    {
        if (ShouldSuppressTooltipText(control.TooltipText))
        {
            control.TooltipText = string.Empty;
        }
    }

    private static bool ShouldSuppressTooltipText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return IsNullLikeText(text);
    }

    private static bool IsNullLikeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        return trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("<null>", StringComparison.OrdinalIgnoreCase);
    }

    private static Control? FindHoveredControl(Node node, Vector2 globalPosition)
    {
        Godot.Collections.Array<Node> children = node.GetChildren();
        for (int i = children.Count - 1; i >= 0; i--)
        {
            Node child = children[i];
            if (child is CanvasItem canvasItem && !canvasItem.IsVisibleInTree())
            {
                continue;
            }

            Control? hoveredChild = FindHoveredControl(child, globalPosition);
            if (hoveredChild != null)
            {
                return hoveredChild;
            }
        }

        if (node is not Control control
            || control.MouseFilter == Control.MouseFilterEnum.Ignore
            || !control.IsVisibleInTree()
            || !control.GetGlobalRect().HasPoint(globalPosition))
        {
            return null;
        }

        return control;
    }

    private static Node? TryFindNativeTooltipContainer(Node node)
    {
        Node? current = node;
        for (int i = 0; i < 8 && current != null; i++)
        {
            if (IsTooltipLikeContainer(current))
            {
                return current;
            }

            current = current.GetParent();
        }

        return null;
    }

    private static bool IsTooltipLikeContainer(Node node)
    {
        string nodeName = node.Name.ToString();
        string typeName = node.GetType().Name;
        return node is Popup
            || node is PopupPanel
            || nodeName.Contains("Tooltip", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Tooltip", StringComparison.OrdinalIgnoreCase);
    }
}
