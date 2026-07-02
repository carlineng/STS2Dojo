using Godot;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// A Godot <c>Node.Duplicate()</c> gotcha that both <c>DojoMainMenuPatch</c> and <c>DojoCompletionScreen</c>
/// hit: scene-unique-name lookups (<c>GetNode("%Name")</c>) resolve relative to a node's <c>Owner</c>, and
/// <c>Duplicate()</c> does NOT repoint a duplicated subtree's internal <c>Owner</c> references at the new
/// duplicate — they keep pointing at whatever node originally owned them (typically the template instance
/// the duplicate was pulled from). If that template is then freed (as both callers here do, once they've
/// extracted what they needed), every <c>%Name</c> lookup inside the duplicate's own <c>_Ready()</c> either
/// silently fails (<c>GetNodeOrNull</c>) or throws (<c>GetNode</c>), aborting whatever ran after it.
///
/// Confirmed in-game as the root cause of three symptoms on the Dojo completion screen's duplicated
/// <c>NPopupYesNoButton</c>s: labels stuck on the template's text, a permanently-visible controller-hotkey
/// icon (its "hide if no controller" logic never runs), and clicks producing a press sound but no action
/// (release handling crashes on a field only <c>%Name</c>-resolvable state populates).
///
/// Fix: re-point <c>Owner</c> at the duplicate's own root for every descendant, BEFORE the duplicate enters
/// the scene tree (unique names register against <c>Owner</c> as part of a node entering the tree, so this
/// has to happen ahead of that, not after).
/// </summary>
public static class DojoNodeDuplication
{
    /// <summary>Re-points every descendant's <c>Owner</c> at <paramref name="root"/> itself — matching what
    /// scene instantiation does (every node in a <c>.tscn</c> is owned by that scene's root, regardless of
    /// depth) — so <c>%Name</c> lookups anywhere in the subtree resolve once it enters the tree.</summary>
    public static void ReownRecursively(Node root) => ReownRecursively(root, root);

    private static void ReownRecursively(Node node, Node owner)
    {
        foreach (Node child in node.GetChildren())
        {
            child.Owner = owner;
            ReownRecursively(child, owner);
        }
    }
}
