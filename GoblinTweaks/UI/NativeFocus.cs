using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GoblinTweaks.UI;

/// <summary>Makes the game forget nodes of ours before they are freed, so no click reaches them afterwards.</summary>
/// <remarks>
/// The game remembers nodes in several places: the node a window keeps as focused (a button clicked in it), which it
/// focuses again on the next click in that window (HandleInput → SetFocus(window->FocusNode)); its list of focus
/// entries; the node with the keyboard focus; the last clicked nodes; the node under the mouse; and each window's list
/// of collision nodes, where it finds what a click hit. Any of them left on a freed node crashed the game on a later
/// click (HandleFocus → a virtual call through the freed component, shown as AtkUnitManager.AddonRequestUpdateById).
/// Called before a window of ours rebuilds part of itself, when it closes (also on plugin update, reload or language
/// change), and before a node of ours is taken off a window of the game.
/// </remarks>
internal static unsafe class NativeFocus
{
    // Far above any window of ours: stops the walk if a node list were ever broken.
    private const int MaxNodes = 20000;

    /// <summary>Before part of an open window of ours is freed: its own focus is taken off the game's way.</summary>
    public static void Release(AtkUnitBase* addon)
    {
        if (addon == null) return;
        Clear(addon, NodesUnder(addon->RootNode), ownWindow: true, closing: false);
        RefreshCollisionsNextFrame(addon);
    }

    /// <summary>
    /// When a window of ours closes. Nothing is asked of the game here: a closing window may already be gone from the
    /// game's list of windows, and telling it about the focus then makes it look the window up by id and crash on not
    /// finding it. The references are simply cleared.
    /// </summary>
    public static void Forget(AtkUnitBase* addon)
    {
        if (addon == null) return;
        Clear(addon, NodesUnder(addon->RootNode), ownWindow: true, closing: true);
    }

    /// <summary>Before a node of ours (a button) is taken off a window of the game that stays open.</summary>
    /// <remarks>Only what points at that node is cleared: the window's own focus is left as the game had it.</remarks>
    public static void ReleaseNode(AtkUnitBase* window, AtkResNode* node)
    {
        if (window == null || node == null) return;
        Clear(window, NodesUnder(node), ownWindow: false, closing: false);
        RefreshCollisionsNextFrame(window);
    }

    /// <summary>
    /// <see cref="ReleaseNode(AtkUnitBase*, AtkResNode*)"/> for a window of the game remembered from when the node
    /// was put on it: only if that window is still there (one closed meanwhile is freed, and its id may be another's).
    /// </summary>
    public static void ReleaseNode(nint window, ushort windowId, AtkResNode* node)
    {
        var units = RaptureAtkUnitManager.Instance();
        if (window == 0 || units == null || (nint)units->GetAddonById(windowId) != window) return;
        ReleaseNode((AtkUnitBase*)window, node);
    }

    /// <summary>Makes the window's list of collision nodes again from the nodes it has now.</summary>
    public static void RefreshCollisions(AtkUnitBase* addon)
    {
        if (addon != null) addon->UpdateCollisionNodeList(false);
    }

    /// <param name="ours">The nodes about to be freed; pointers held by the game are only compared with them, never followed.</param>
    /// <param name="ownWindow">The whole window is ours, so everything it remembers can go.</param>
    private static void Clear(AtkUnitBase* window, HashSet<nint> ours, bool ownWindow, bool closing)
    {
        var stage = AtkStage.Instance();
        if (stage == null) return;

        var input = stage->AtkInputManager;
        if (input != null)
        {
            var focused = false;
            foreach (ref var entry in input->FocusList)
            {
                if (entry.AtkEventListener == (AtkEventListener*)window)
                {
                    if (!ownWindow)
                        focused |= ours.Contains((nint)entry.AtkEventTarget);
                    else if (closing)
                    {
                        entry.AtkEventListener = null;
                        entry.AtkEventTarget   = null;
                    }
                    else focused |= entry.AtkEventTarget != null;

                    continue;
                }

                // A focus kept by something other than the window (a component of ours, or another window) on a
                // node of ours: the game would tell that node it lost the focus after it is freed.
                if (ours.Contains((nint)entry.AtkEventListener))
                {
                    entry.AtkEventListener = null;
                    entry.AtkEventTarget   = null;
                }
                else if (ours.Contains((nint)entry.AtkEventTarget))
                {
                    entry.AtkEventTarget = null;
                }
            }

            // The window's focus is taken off the game's way, while the node is still alive to be told.
            if (focused && !closing) input->SetFocus(null, window, 0);

            // The node with the keyboard focus, which the game tells it lost the focus on the next click. Cleared
            // after SetFocus, which may leave it set.
            if (ours.Contains((nint)input->FocusedNode))
                input->FocusedNode = null;

            // The nodes of the last clicks, kept to tell a double click.
            foreach (ref var clicked in input->SavedMouseClicksCollisionNodes)
            {
                if (ours.Contains((nint)clicked.Value))
                    clicked = (AtkCollisionNode*)null;
            }
        }

        // The node the window keeps for its cursor, which it goes back to when it gets the focus again (a Yes/No of
        // the game closing in front of it): freed, accepting a party invite crashed the game. The window picks another.
        if (ownWindow || ours.Contains((nint)window->CursorTarget))
            window->CursorTarget = null;

        // The node the window remembers as focused: the next click anywhere in the window focuses it again.
        if (ours.Contains((nint)window->FocusNode)) window->FocusNode = null;
        if (ours.Contains((nint)window->ComponentFocusNode)) window->ComponentFocusNode = null;
        foreach (ref var focusable in window->AdditionalFocusableNodes)
        {
            if (ours.Contains((nint)focusable.Value))
                focusable = (AtkResNode*)null;
        }

        // The node under the mouse.
        var collision = stage->AtkCollisionManager;
        if (collision != null && ours.Contains((nint)collision->IntersectingCollisionNode))
            collision->IntersectingCollisionNode = null;
    }

    /// <summary>A node and every node and component under it (not its siblings), walked through nodes still alive.</summary>
    private static HashSet<nint> NodesUnder(AtkResNode* start)
    {
        var nodes   = new HashSet<nint>();
        var pending = new Stack<nint>();
        if (start == null) return nodes;

        nodes.Add((nint)start);
        PushInside(start, nodes, pending);

        while (pending.Count > 0 && nodes.Count < MaxNodes)
        {
            var node = (AtkResNode*)pending.Pop();
            if (!nodes.Add((nint)node)) continue;

            if (node->PrevSiblingNode != null) pending.Push((nint)node->PrevSiblingNode);
            if (node->NextSiblingNode != null) pending.Push((nint)node->NextSiblingNode);
            PushInside(node, nodes, pending);
        }

        return nodes;
    }

    private static void PushInside(AtkResNode* node, HashSet<nint> nodes, Stack<nint> pending)
    {
        if (node->ChildNode != null) pending.Push((nint)node->ChildNode);

        if ((ushort)node->Type < 1000) return;
        var component = ((AtkComponentNode*)node)->Component;
        if (component != null && nodes.Add((nint)component) && component->UldManager.RootNode != null)
            pending.Push((nint)component->UldManager.RootNode);
    }

    // ── Collision lists made again next frame ─────────────────────────────────
    // After a rebuild the list still held the freed nodes, and a click over one of them reached it. It is made again
    // at the start of the next frame, before the game reads any click, once the window has its new nodes.

    private static readonly Dictionary<nint, ushort> PendingRefresh = [];

    private static void RefreshCollisionsNextFrame(AtkUnitBase* addon)
    {
        var first = PendingRefresh.Count == 0;
        PendingRefresh[(nint)addon] = addon->Id;
        if (first) Svc.Framework.RunOnTick(RefreshPending);
    }

    private static void RefreshPending()
    {
        var units = RaptureAtkUnitManager.Instance();
        foreach (var (pointer, id) in PendingRefresh)
        {
            // Only a window that is still there: one closed meanwhile is freed, and its id may be another's now.
            if (units != null && (nint)units->GetAddonById(id) == pointer)
                RefreshCollisions((AtkUnitBase*)pointer);
        }

        PendingRefresh.Clear();
    }
}
