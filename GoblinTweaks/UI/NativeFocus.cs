using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GoblinTweaks.UI;

/// <summary>Takes the game's input focus off a window of ours before nodes of it are freed.</summary>
/// <remarks>
/// The game keeps the node last clicked (a button) as the focus of its window, and on the next click tells it that it
/// lost the focus. If that node was freed meanwhile, the game reads freed memory and crashes
/// (AtkInputManager.SetFocus → HandleFocus → AtkUnitManager.AddonRequestUpdateById). KamiToolKit clears the focus
/// of a node it frees only when the focus is that very node, and the game focuses a node inside a button: so it is
/// cleared here, the game's own way, for the whole window. The window also keeps the node its cursor was on, which
/// the game goes back to later: that one is forgotten too.
/// </remarks>
internal static unsafe class NativeFocus
{
    public static void Release(AtkUnitBase* addon)
    {
        // A window that is not shown has no focus to lose.
        var stage = AtkStage.Instance();
        if (addon == null || stage == null || !addon->IsVisible) return;

        var input = stage->AtkInputManager;
        if (input != null)
        {
            foreach (ref readonly var entry in input->FocusList)
            {
                if (entry.AtkEventListener != (AtkEventListener*)addon || entry.AtkEventTarget == null) continue;

                input->SetFocus(null, addon, 0);
                break;
            }
        }

        // The node the window keeps for its cursor, which it goes back to when it gets the focus again (a Yes/No of
        // the game closing in front of it): freed, accepting a party invite crashed the game. The window picks another.
        addon->CursorTarget = null;

        // The node under the mouse goes too when it is one of this window: it may be freed with the rest.
        var collision = stage->AtkCollisionManager;
        var units     = RaptureAtkUnitManager.Instance();
        if (collision != null && units != null && collision->IntersectingCollisionNode != null
            && units->GetAddonByNode((AtkResNode*)collision->IntersectingCollisionNode) == addon)
            collision->IntersectingCollisionNode = null;
    }
}
