using System.Globalization;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using GoblinTweaks.Tweaks;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Makes the shield on your character count in the HP of the player window (Parameters): the number can
/// become HP plus shield, in the blue of the shield, and/or the shield can be written under it. Part of
/// Goblin Battle Text, which owns the options.
/// </summary>
/// <remarks>
/// The game only gives the shield as a percentage of your maximum HP, so the amount is that percentage of the
/// maximum, rounded. The number of the window is found by its text, so a changed layout stops the display
/// instead of crashing.
/// </remarks>
internal sealed unsafe class ShieldDisplay(Func<GoblinBattleText.BattleTextShieldMode> mode, Func<bool> sample, Func<Vector4?> outline, Action<Exception> fail)
{
    private const string AddonName = "_ParameterWidget";
    private const int MaxDepth = 6;

    /// <summary>The turquoise of the shield on the HP bar.</summary>
    public static readonly Vector4 Color = new(0.20f, 0.85f, 0.92f, 1f);

    private static readonly Vector4 ShieldColor = Color;

    private AtkTextNode* _hpNode;
    private ByteColor _originalColor;
    private bool _recolored;
    private string? _written;
    private TextNode? _below;
    private readonly Dictionary<nint, (ByteColor Edge, TextFlags Flags)> _outlined = [];
    private readonly List<nint> _texts = [];
    private bool _layoutWarningShown;

    public void Start()
    {
        _layoutWarningShown = false;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, OnPreDraw);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    public void Stop()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, AddonName, OnPreDraw);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
        Restore(CurrentHp());
        RestoreOutline();
        DisposeBelow();
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        // The window is about to be destroyed: our node must leave it first, and its text needs no restoring.
        DisposeBelow();
        _outlined.Clear();
        _hpNode = null;
        _recolored = false;
        _written = null;
    }

    private void OnPreDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            Update((AtkUnitBase*)args.Addon.Address);
        }
        catch (Exception ex)
        {
            fail(ex);
        }
    }

    private void Update(AtkUnitBase* addon)
    {
        if (addon == null || !addon->IsVisible) return;

        ApplyOutline(addon);

        var data = ReadPlayer(out var hp, out var maxHp, out var shield);

        // While the settings window shows its samples, a shield of a tenth of your HP stands in for a real one.
        if (data && shield <= 0 && sample())
            shield = maxHp / 10;
        if (!data || shield <= 0 || mode() == GoblinBattleText.BattleTextShieldMode.Off)
        {
            Restore(data ? hp : -1);
            HideBelow();
            return;
        }

        var node = FindHpNode(addon, hp, hp + shield);
        if (node == null)
        {
            if (!_layoutWarningShown)
            {
                Svc.Log.Warning("{addon}: the HP number was not found. The shield display is off until the window changes.", AddonName);
                _layoutWarningShown = true;
            }
            return;
        }

        if (mode() == GoblinBattleText.BattleTextShieldMode.AddToHp)
        {
            var text = (hp + shield).ToString(CultureInfo.InvariantCulture);
            if (_hpNode != node || NumberOf(node) != hp + shield)
                node->SetText(text);
            _written = text;

            if (!_recolored)
            {
                _originalColor = node->TextColor;
                _recolored = true;
            }
            node->TextColor = new ByteColor
            {
                R = (byte)(ShieldColor.X * 255f),
                G = (byte)(ShieldColor.Y * 255f),
                B = (byte)(ShieldColor.Z * 255f),
                A = 255,
            };
        }
        else
        {
            Restore(hp);
        }

        _hpNode = node;

        if (mode() == GoblinBattleText.BattleTextShieldMode.Below) ShowBelow(addon, node, shield);
        else HideBelow();
    }

    /// <summary>Health, maximum health and the shield, in HP, of your character. False when there is no character.</summary>
    private static bool ReadPlayer(out int hp, out int maxHp, out int shield)
    {
        hp = maxHp = shield = 0;
        var player = (Character*)Control.GetLocalPlayer();
        if (player == null) return false;

        hp = (int)player->CharacterData.Health;
        maxHp = (int)player->CharacterData.MaxHealth;
        shield = (int)Math.Round(player->CharacterData.ShieldValue / 100d * maxHp);
        return maxHp > 0;
    }

    /// <summary>The text node of the window that shows your HP: the leftmost one whose number is your HP (or the one we wrote).</summary>
    private AtkTextNode* FindHpNode(AtkUnitBase* addon, int hp, int shown)
    {
        if (_hpNode != null && IsHp(_hpNode, hp, shown)) return _hpNode;

        AtkTextNode* best = null;
        Search(&addon->UldManager, hp, shown, ref best, 0);
        return best;
    }

    private static void Search(AtkUldManager* uld, int hp, int shown, ref AtkTextNode* best, int depth)
    {
        if (depth > MaxDepth || uld == null || uld->NodeList == null) return;

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null) continue;

            if (node->Type == NodeType.Text)
            {
                var text = (AtkTextNode*)node;
                if (node->IsVisible() && IsHp(text, hp, shown) && (best == null || node->ScreenX < ((AtkResNode*)best)->ScreenX))
                    best = text;
            }
            else if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null) Search(&component->UldManager, hp, shown, ref best, depth + 1);
            }
        }
    }

    private static bool IsHp(AtkTextNode* node, int hp, int shown)
    {
        var number = NumberOf(node);
        return number == hp || number == shown;
    }

    /// <summary>The number a text node shows, or -1 if it shows something else.</summary>
    private static int NumberOf(AtkTextNode* node)
    {
        var text = node->NodeText.ToString().Replace(",", string.Empty).Replace(".", string.Empty).Trim();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
    }

    private static int CurrentHp() => ReadPlayer(out var hp, out _, out _) ? hp : -1;

    /// <summary>Puts the HP number back the way the game wrote it: its text and its color.</summary>
    private void Restore(int hp)
    {
        if (_hpNode == null || !_recolored && _written == null) return;

        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(AddonName).Address;
        if (addon != null && NumberOf(_hpNode) is var shown && shown >= 0 && hp >= 0 && _written is not null && shown != hp)
            _hpNode->SetText(hp.ToString(CultureInfo.InvariantCulture));

        if (_recolored && addon != null)
            _hpNode->TextColor = _originalColor;

        _recolored = false;
        _written = null;
    }

    private void ShowBelow(AtkUnitBase* addon, AtkTextNode* hpNode, int shield)
    {
        if (_below == null)
        {
            _below = new TextNode { TextColor = ShieldColor };
            _below.AttachNode(addon);
        }

        if (outline() is { } edge)
        {
            _below.TextOutlineColor = edge;
            _below.AddTextFlags(TextFlags.Edge);
        }
        else
        {
            _below.RemoveTextFlags(TextFlags.Edge);
        }

        var res   = (AtkResNode*)hpNode;
        var scale = addon->Scale <= 0f ? 1f : addon->Scale;

        // Same font and size as the HP number, with its right end where the HP number ends.
        var boxLeft  = res->ScreenX;
        var boxWidth = res->Width * res->ScaleX;
        ushort textW = 0, textH = 0;
        hpNode->GetTextDrawSize(&textW, &textH);

        var alignment = hpNode->AlignmentType;
        var end = alignment switch
        {
            AlignmentType.TopRight or AlignmentType.Right or AlignmentType.BottomRight => boxLeft + boxWidth,
            AlignmentType.Top or AlignmentType.Center or AlignmentType.Bottom          => boxLeft + (boxWidth + textW * res->ScaleX) / 2f,
            _                                                                          => boxLeft + textW * res->ScaleX,
        };

        // A little closer to the number, and to the left, so its last digit lines up with the HP's.
        end -= 4f * scale;

        var width = Math.Max(boxWidth, 80f);
        _below.IsVisible     = true;
        _below.String        = shield.ToString(CultureInfo.InvariantCulture);
        _below.FontType      = hpNode->FontType;
        _below.FontSize      = hpNode->FontSize;
        _below.AlignmentType = AlignmentType.TopRight;
        _below.Size          = new Vector2(width, hpNode->FontSize + 4f);
        _below.Position      = new Vector2((end - width - addon->X) / scale, (res->ScreenY - addon->Y) / scale + res->Height * res->ScaleY - 8f);
    }

    // ── Outline ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gives the numbers of the window (HP, MP) and the labels beside them (HP, MP) an outline of the chosen color,
    /// or takes it off. The labels are the short texts on the same line as a number, to its left.
    /// </summary>
    private void ApplyOutline(AtkUnitBase* addon)
    {
        if (outline() is not { } color)
        {
            RestoreOutline();
            return;
        }

        _texts.Clear();
        CollectTexts(&addon->UldManager, 0);

        var edge = new ByteColor { R = (byte)(color.X * 255f), G = (byte)(color.Y * 255f), B = (byte)(color.Z * 255f), A = (byte)(color.W * 255f) };
        foreach (var address in _texts)
        {
            var text = (AtkTextNode*)address;
            if (!IsNumberOrLabel(text)) continue;

            if (!_outlined.ContainsKey(address))
                _outlined[address] = (text->EdgeColor, text->TextFlags);

            text->TextFlags |= TextFlags.Edge;
            text->EdgeColor = edge;
        }
    }

    private bool IsNumberOrLabel(AtkTextNode* node)
    {
        if (NumberOf(node) >= 0) return true;

        var label = node->NodeText.ToString().Trim();
        if (label.Length == 0 || label.Length > 4) return false;

        var res = (AtkResNode*)node;
        foreach (var address in _texts)
        {
            var other = (AtkTextNode*)address;
            var near  = (AtkResNode*)other;
            if (other != node && NumberOf(other) >= 0 && MathF.Abs(near->ScreenY - res->ScreenY) <= 10f && res->ScreenX < near->ScreenX)
                return true;
        }

        return false;
    }

    private void CollectTexts(AtkUldManager* uld, int depth)
    {
        if (depth > MaxDepth || uld == null || uld->NodeList == null) return;

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null) continue;

            if (node->Type == NodeType.Text)
            {
                if (node->IsVisible()) _texts.Add((nint)node);
            }
            else if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null) CollectTexts(&component->UldManager, depth + 1);
            }
        }
    }

    private void RestoreOutline()
    {
        if (_outlined.Count == 0) return;

        if (Svc.GameGui.GetAddonByName(AddonName).Address != 0)
        {
            foreach (var (address, original) in _outlined)
            {
                var text = (AtkTextNode*)address;
                text->EdgeColor = original.Edge;
                text->TextFlags = original.Flags;
            }
        }

        _outlined.Clear();
    }

    private void HideBelow()
    {
        if (_below != null) _below.IsVisible = false;
    }

    private void DisposeBelow()
    {
        _below?.Dispose();
        _below = null;
    }
}
