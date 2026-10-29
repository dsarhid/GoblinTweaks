using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.Native;
using GoblinTweaks.UI;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Makes the icons of the hotbars round: each icon, its frame and its cooldown are clipped to a circle with native
/// clipping mask nodes, and a thin ring can be drawn around it. The size of the circle is an option. The icon can also
/// look like a sphere, and the charge counter is moved out of the circle.
/// </summary>
/// <remarks>
/// <para>
/// No hooks or signatures, and no game node is moved. A clipping mask clips the siblings before it, so one mask is
/// linked right after the image (or the first images) of each list of siblings to clip: the icon, first node of its
/// component, the frame, the cooldown. The square background of the slot and the game's frame are only made
/// transparent.
/// </para>
/// <para>
/// Only the inside of each icon component gets new nodes. The game reads the node lists of the slot (drag and drop) and
/// of the hotbar by position, so a node added there could send it to the wrong node; the icon caches its own nodes.
/// The marching-ants border of combos and procs is left square, so it stays visible.
/// </para>
/// <para>
/// Native nodes can not bend a texture, so the sphere is drawn by the plugin: each icon is read back through Dalamud
/// (mods included), bent and shaded on a worker thread, and shown in an image right over the game's icon, clipped by
/// the same mask and with the same darkening the game gives it.
/// </para>
/// </remarks>
[Tweak(TweakCategory.Interface)]
public sealed unsafe class GoblinHotbarIcons : Tweak<GoblinHotbarIcons.Options>
{
    public sealed class Options
    {
        /// <summary>Diameter of the circle, in percent of the icon.</summary>
        public int CircleSize { get; set; } = 100;

        /// <summary>A thin dark ring around every icon.</summary>
        public bool Border { get; set; } = true;

        /// <summary>Moves the charge counter out of the circle, so it is not cut.</summary>
        public bool ChargesOutside { get; set; } = true;

        /// <summary>Bends and shades the icon like a sphere.</summary>
        public bool Sphere { get; set; } = true;

        /// <summary>How strong the sphere is, in percent.</summary>
        public int SphereStrength { get; set; } = 60;
    }

    internal const int MinCircleSize = 50;
    internal const int MaxCircleSize = 100;
    internal const int MinSphereStrength = 10;
    internal const int MaxSphereStrength = 100;

    /// <summary>The keyboard hotbars, the pet hotbar and the cross hotbars.</summary>
    private static readonly string[] AddonNames =
    [
        "_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04", "_ActionBar05",
        "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09", "_ActionBarEx",
        "_ActionCross", "_ActionDoubleCrossL", "_ActionDoubleCrossR",
    ];

    private const uint NodeIdBase = 0x4743_0000;

    /// <summary>Layers smaller than this (little markers in a corner) are not clipped.</summary>
    private const float MinLayerSize = 20f;

    /// <summary>Images of the slot at least this big are its square background.</summary>
    private const float MinBackgroundSize = 32f;

    /// <summary>The square background of a slot, made transparent while the slot shows an icon.</summary>
    private sealed class Background(nint node)
    {
        public nint Node { get; } = node;
        public byte Alpha { get; set; }
        public bool Hidden { get; set; }
    }

    /// <summary>What was added to one slot.</summary>
    private sealed class SlotState(nint iconComponent, nint iconNode, nint iconImage, ushort size)
    {
        public nint IconComponent { get; } = iconComponent;
        public nint IconNode { get; } = iconNode;
        public nint IconImage { get; } = iconImage;
        public ushort Size { get; } = size;
        public List<NativeInsertedNode> Masks { get; } = [];
        public NativeInsertedNode? Ring { get; set; }

        /// <summary>The masks of the icon and of the sphere; the others (frame, cooldown) get a slightly smaller circle.</summary>
        public NativeInsertedNode? IconMask { get; set; }
        public NativeInsertedNode? SphereMask { get; set; }
        public List<Background> Backgrounds { get; } = [];

        /// <summary>The bent icon, and the icon it shows now (0 = none yet).</summary>
        public NativeInsertedNode? Sphere { get; set; }
        public uint SphereIcon { get; set; }
        public ushort SphereSize { get; set; }

        /// <summary>The charge counter, moved out of the circle, and where it was.</summary>
        public nint Charge { get; set; }
        public Vector2 ChargeOrigin { get; set; }
    }

    /// <summary>A bent icon: being made, ready, or failed (then the flat icon stays).</summary>
    private sealed class SphereTexture
    {
        public NativeTexturePart? Part { get; set; }
        public bool Failed { get; set; }
    }

    /// <summary>A bent icon made on a worker thread, for the framework thread to turn into a texture.</summary>
    private readonly record struct SphereResult(uint Icon, int Generation, int Size, byte[]? Pixels);

    /// <summary>What was done to one hotbar, to follow the icons and to undo it.</summary>
    private sealed class BarState(string name)
    {
        public string Name { get; } = name;
        public List<SlotState> Slots { get; } = [];

        /// <summary>The managers that hold masks, with whether they already said they had one.</summary>
        public Dictionary<nint, bool> Managers { get; } = [];
    }

    private readonly Dictionary<nint, BarState> _bars = [];
    private readonly Dictionary<ushort, (NativeTexturePart Mask, NativeTexturePart Ring)> _textures = [];
    private GoblinHotbarSettingsAddon? _settingsAddon;

    private readonly Dictionary<uint, SphereTexture> _spheres = [];
    private readonly ConcurrentQueue<SphereResult> _sphereResults = new();

    /// <summary>Changes whenever the bent icons are thrown away: work started before is ignored when it comes back.</summary>
    private int _sphereGeneration;

    /// <summary>The hotbars already reported in the log since the tweak was enabled.</summary>
    private readonly HashSet<string> _logged = [];

    /// <summary>Set if a hotbar could not be undone: its nodes may still be drawn, so the textures are never freed.</summary>
    private bool _texturesInUse;

    internal Options Config => Settings;

    /// <summary>Localized text of this tweak, for its settings window.</summary>
    internal string Text(string key) => T(key);

    internal void SaveConfig() => SaveSettings();

    protected internal override void Enable()
    {
        _logged.Clear();
        _settingsAddon = NewSettingsAddon();

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonNames, OnPreDraw);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonNames, OnPreFinalize);
    }

    protected internal override void Disable()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, AddonNames, OnPreDraw);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonNames, OnPreFinalize);

        RemoveAll();

        _settingsAddon?.Dispose();
        _settingsAddon = null;
    }

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, ToggleHelpWindow);

    public override IReadOnlyList<TweakButton> Buttons =>
    [
        new(Loc.Get("Window.Settings"), TMain("Settings.Help"), ToggleSettings),
    ];

    protected override IReadOnlyList<string> CommandNames => ["/ghi", "/ghotbaricon"];

    protected override void OnCommand() => ToggleSettings();

    private void ToggleSettings()
    {
        if (_settingsAddon is not { } window) return;
        if (window.IsOpen) window.Close(); else window.Open();
    }

    private GoblinHotbarSettingsAddon NewSettingsAddon() => new()
    {
        InternalName    = "GtkHotbarIconsSettings",
        Title           = $"{T("Name")} — {Loc.GetIn(UiLanguage, "Window.Settings")}",
        Size            = new Vector2(520f, 560f),
        RespectCloseAll = false,
        Tweak           = this,
    };

    protected override void OnLanguageChanged()
    {
        if (State == TweakState.Enabled) Rebuild(ref _settingsAddon, NewSettingsAddon);
    }

    // The hotbars are built again with the new options on their next frame.
    protected override void OnSettingsChanged() => RemoveAll();

    private void OnPreDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null)
                return;

            TakeSphereResults();

            if (!_bars.TryGetValue((nint)addon, out var bar))
                bar = Apply(addon, args.AddonName);

            if (bar is not null)
                Follow(bar);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        try
        {
            Remove((AtkUnitBase*)args.Addon.Address);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    /// <summary>Clips every slot of a hotbar. Null, and nothing done, while its icons are not laid out yet.</summary>
    private BarState? Apply(AtkUnitBase* addon, string name)
    {
        var slots = ((AddonActionBarBase*)addon)->ActionBarSlotVector.AsSpan();
        if (slots.Length == 0)
            return null;

        // Every icon must have its size: the circle is measured on it.
        foreach (ref var slot in slots)
        {
            var icon = Icon(ref slot);
            if (icon != null && icon->IconImage->AtkResNode.Width == 0)
                return null;
        }

        // Tracked from the start, so that whatever is done is undone even if something fails halfway.
        var bar = new BarState(name);
        _bars[(nint)addon] = bar;

        var nodeId = NodeIdBase;
        foreach (ref var slot in slots)
        {
            var icon = Icon(ref slot);
            if (icon == null)
                continue;

            var image = (AtkResNode*)icon->IconImage;
            var size = (ushort)Math.Max(8, MathF.Round(MathF.Min(image->Width, image->Height) * Settings.CircleSize / 100f));
            if (TexturesFor(size) is not { } textures)
                continue;

            var state = new SlotState((nint)icon, (nint)slot.Icon, (nint)image, size);
            bar.Slots.Add(state);

            // A mask right after each image that opens a list of siblings: the icon, the frame, the cooldown. One
            // each: a mask does not reach an image with another node between them.
            var manager = &icon->UldManager;
            var targets = Layers(manager, icon);
            targets.Add((nint)image);

            foreach (var target in LeadingImages(manager, targets))
            {
                var mask = AddMask(bar, state, manager, (AtkResNode*)target, textures.Mask, ref nodeId);
                if (target == (nint)image)
                    state.IconMask = mask;
            }

            if (state.IconMask is { } iconMask)
            {
                // The bent icon right over the game's, with a mask of its own. Hidden until its texture is ready.
                var last = iconMask;
                if (Settings.Sphere && NativeInsertedNode.Image(iconMask.Node, textures.Ring.PartsList, nodeId++) is { } sphere)
                {
                    sphere.SetShown(false, 255);
                    state.Sphere = sphere;
                    state.SphereMask = AddMask(bar, state, manager, sphere.Node, textures.Mask, ref nodeId);
                    last = state.SphereMask ?? iconMask;
                }

                // The ring right after them: over the icon, under its cooldown and numbers.
                if (Settings.Border)
                    state.Ring = NativeInsertedNode.Image(last.Node, textures.Ring.PartsList, nodeId++);
            }

            // The charge counter (True North 2/2...) is moved out of the circle.
            if (Settings.ChargesOutside && slot.ChargeIcon != null && slot.ChargeIcon->AtkResNode.Type == NodeType.Image)
            {
                var charge = (AtkResNode*)slot.ChargeIcon;
                state.Charge = (nint)charge;
                state.ChargeOrigin = new Vector2(charge->X, charge->Y);
            }

            // The game's frame: its gloss shows at the top and bottom of the circle. The sphere has its own light.
            foreach (var address in Nodes(manager))
            {
                var node = (AtkResNode*)address;
                if (IsFrame(node, icon) && CanHide(node, (AtkResNode*)slot.Icon, image))
                    state.Backgrounds.Add(new Background(address));
            }

            // The square behind the icon would show at the corners of the circle.
            if (slot.ComponentDragDrop != null)
            {
                foreach (var address in Nodes(&slot.ComponentDragDrop->AtkComponentBase.UldManager))
                {
                    var node = (AtkResNode*)address;
                    if (node->Type != NodeType.Image || node->Width < MinBackgroundSize || node->Height < MinBackgroundSize)
                        continue;
                    // Only the slot's own images, never one of the icon's.
                    if (IsInside(node, (AtkResNode*)slot.Icon))
                        continue;

                    state.Backgrounds.Add(new Background(address));
                }
            }
        }

        UpdateDrawLists(bar);

        if (_logged.Add(name))
            Svc.Log.Debug("GoblinHotbarIcons: {bar} has {slots} slots; {icons} icons clipped with {masks} masks, {rings} rings, {spheres} spheres, {charges} charge counters.",
                name, slots.Length, bar.Slots.Count(slot => slot.Masks.Count > 0), bar.Slots.Sum(slot => slot.Masks.Count),
                bar.Slots.Count(slot => slot.Ring is not null), bar.Slots.Count(slot => slot.Sphere is not null),
                bar.Slots.Count(slot => slot.Charge != 0));

        return bar;
    }

    /// <summary>
    /// The icon component of a slot. Checked by what it holds, not by the type it reports: its icon must be one of its
    /// own image nodes. The pointer is only followed once it is known to be one of them.
    /// </summary>
    private static AtkComponentIcon* Icon(ref ActionBarSlot slot)
    {
        var node = slot.Icon;
        if (node == null || node->Component == null)
            return null;

        var icon = (AtkComponentIcon*)node->Component;
        var image = (AtkResNode*)icon->IconImage;
        if (image == null || !Nodes(&icon->UldManager).Contains((nint)image) || image->Type != NodeType.Image)
            return null;

        return icon;
    }

    /// <summary>The nodes of a manager: its draw list, or every node it owns if the draw list is empty.</summary>
    private static List<nint> Nodes(AtkUldManager* manager)
    {
        var nodes = new List<nint>();

        if (manager->NodeList != null && manager->NodeListCount > 0)
        {
            for (var i = 0; i < manager->NodeListCount; i++)
            {
                if (manager->NodeList[i] != null)
                    nodes.Add((nint)manager->NodeList[i]);
            }
        }
        else if (manager->Objects != null && manager->Objects->NodeList != null)
        {
            for (var i = 0; i < manager->Objects->NodeCount && i < 1000; i++)
            {
                if (manager->Objects->NodeList[i] != null)
                    nodes.Add((nint)manager->Objects->NodeList[i]);
            }
        }

        return nodes;
    }

    /// <summary>
    /// The images of an icon to clip besides the icon itself: big enough, and not the marching-ants border.
    /// </summary>
    private static HashSet<nint> Layers(AtkUldManager* manager, AtkComponentIcon* icon)
    {
        var layers = new HashSet<nint>();
        foreach (var address in Nodes(manager))
        {
            var node = (AtkResNode*)address;
            if (node->Type != NodeType.Image || node->Width < MinLayerSize || node->Height < MinLayerSize)
                continue;
            if (IsInside(node, icon->ComboBorder))
                continue;

            layers.Add(address);
        }

        return layers;
    }

    /// <summary>Links a mask right after a node and notes that its manager now has masks.</summary>
    private static NativeInsertedNode? AddMask(BarState bar, SlotState state, AtkUldManager* manager, AtkResNode* after, NativeTexturePart texture, ref uint nodeId)
    {
        var mask = NativeInsertedNode.Mask(after, texture.PartsList, nodeId++);
        if (mask is null)
            return null;

        state.Masks.Add(mask);
        if (!bar.Managers.ContainsKey((nint)manager))
            bar.Managers[(nint)manager] = manager->ResourceFlags.HasFlag(AtkUldManagerResourceFlag.ContainsClippingMaskNode);
        manager->ResourceFlags |= AtkUldManagerResourceFlag.ContainsClippingMaskNode;
        return mask;
    }

    /// <summary>
    /// The game's square frame around the icon: the first picture (a 48×48 square) of ui/uld/IconA_Frame.tex. The
    /// marching ants come from the same texture but from other pictures, and sit in the combo border.
    /// </summary>
    private static bool IsFrame(AtkResNode* node, AtkComponentIcon* icon)
    {
        if (node->Type != NodeType.Image || node->Width != node->Height || node->Width is < 44 or > 52)
            return false;
        if (node == (AtkResNode*)icon->IconImage || IsInside(node, icon->ComboBorder))
            return false;

        var image = (AtkImageNode*)node;
        if (image->PartsList == null || image->PartId >= image->PartsList->PartCount)
            return false;

        var part = &image->PartsList->Parts[image->PartId];
        if (part->U != 0 || part->V != 0 || part->UldAsset == null)
            return false;

        var texture = &part->UldAsset->AtkTexture;
        if (texture->TextureType != TextureType.Resource || texture->Resource == null || texture->Resource->TexFileResourceHandle == null)
            return false;

        var path = System.Text.Encoding.UTF8.GetString(texture->Resource->TexFileResourceHandle->FileName.AsSpan());
        return path.Contains("IconA_Frame", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// For each list of siblings that opens with images to clip, those first images, in order. Images further down a
    /// list, after other nodes, are left alone: a mask after them could reach the nodes before.
    /// </summary>
    private static List<nint> LeadingImages(AtkUldManager* manager, HashSet<nint> targets)
    {
        var heads = new HashSet<nint>();
        foreach (var address in targets)
        {
            var parent = ((AtkResNode*)address)->ParentNode;
            // The first nodes of a component hang from the component node, which does not list them as its children.
            var head = parent == null || (ushort)parent->Type >= 1000 ? manager->RootNode : parent->ChildNode;
            if (head != null)
                heads.Add((nint)head);
        }

        var leading = new List<nint>();
        foreach (var head in heads)
        {
            var steps = 0;
            for (var node = (AtkResNode*)head; node != null && targets.Contains((nint)node) && steps < 64; node = node->PrevSiblingNode, steps++)
                leading.Add((nint)node);
        }

        return leading;
    }

    /// <summary>
    /// Whether a node of the icon can be made transparent: inside the icon, not the icon nor one of its containers, and
    /// an image or a small container of images only (never one that holds the cooldown or the numbers).
    /// </summary>
    private static bool CanHide(AtkResNode* node, AtkResNode* iconNode, AtkResNode* image)
    {
        if (node == null || node == image || !IsInside(node, iconNode) || IsInside(image, node))
            return false;

        if (node->Type == NodeType.Image)
            return true;

        if (node->Type != NodeType.Res || node->ChildCount > 3)
            return false;

        var steps = 0;
        for (var child = node->ChildNode; child != null && steps < 8; child = child->PrevSiblingNode, steps++)
        {
            if (child->Type != NodeType.Image)
                return false;
        }

        return true;
    }

    private static bool IsInside(AtkResNode* node, AtkResNode* ancestor)
    {
        if (ancestor == null)
            return false;

        for (var current = node; current != null; current = current->ParentNode)
        {
            if (current == ancestor)
                return true;
        }

        return false;
    }

    /// <summary>Rebuilds the draw lists of the icons. The hotbar's own list holds the icon components, not what is inside them.</summary>
    private static void UpdateDrawLists(BarState bar)
    {
        foreach (var manager in bar.Managers.Keys)
            ((AtkUldManager*)manager)->UpdateDrawNodeList();
    }

    /// <summary>
    /// Puts every mask and ring on the circle of its icon, wherever the icon is now (layout, scale, animations), and
    /// hides the square background of the slots that show an icon.
    /// </summary>
    private void Follow(BarState bar)
    {
        foreach (var slot in bar.Slots)
        {
            var image = (AtkResNode*)slot.IconImage;
            var (center, scale) = ToAddon(image, new Vector2(image->Width / 2f, image->Height / 2f));
            var diameter = slot.Size * scale;
            var topLeft = center - diameter / 2f;

            // The frame and the cooldown stop short of the edge: the frame's light border would show at the top and
            // bottom of the circle (the icon is not centred in it) and cover the ring.
            var inset = (Settings.Border ? RingWidth(slot.Size) + 0.5f : 2f) * scale;
            foreach (var mask in slot.Masks)
            {
                if (mask == slot.IconMask || mask == slot.SphereMask)
                    Place(mask, topLeft, diameter, slot.Size);
                else
                    Place(mask, topLeft + inset, diameter - inset * 2f, slot.Size);
            }

            var shown = ((AtkResNode*)slot.IconNode)->NodeFlags.HasFlag(NodeFlags.Visible)
                        && image->NodeFlags.HasFlag(NodeFlags.Visible);

            if (slot.Ring is { } ring)
            {
                Place(ring, topLeft, diameter, slot.Size);
                ring.SetShown(shown, image->Color.A);
            }

            if (slot.Sphere is { } sphere)
                FollowSphere(slot, sphere, image, shown, topLeft, diameter);

            if (slot.Charge != 0)
                MoveCharge(slot, center, diameter.X / 2f);

            foreach (var background in slot.Backgrounds)
            {
                var node = (AtkResNode*)background.Node;
                if (shown && node->Color.A != 0)
                {
                    background.Alpha = node->Color.A;
                    background.Hidden = true;
                    SetAlpha(node, 0);
                }
                else if (!shown && background.Hidden)
                {
                    background.Hidden = false;
                    SetAlpha(node, background.Alpha);
                }
            }
        }
    }

    /// <summary>Shows the bent version of the icon the slot has now, asking for it if it is not made yet.</summary>
    private void FollowSphere(SlotState slot, NativeInsertedNode sphere, AtkResNode* image, bool shown, Vector2 topLeft, Vector2 diameter)
    {
        var iconId = ((AtkComponentIcon*)slot.IconComponent)->IconId;
        if (iconId != slot.SphereIcon)
        {
            slot.SphereIcon = iconId;
            slot.SphereSize = 0;
        }

        if (slot.SphereSize == 0 && iconId != 0 && SphereFor(iconId) is { Part: { } part })
        {
            sphere.SetParts(part.PartsList);
            slot.SphereSize = part.PartsList->Parts[0].Width;
        }

        var ready = slot.SphereSize != 0;
        if (ready)
        {
            Place(sphere, topLeft, diameter, slot.SphereSize);
            sphere.CopyColors(image);
        }

        sphere.SetShown(shown && ready, image->Color.A);
    }

    /// <summary>
    /// Puts the charge counter outside the circle: moved away from the centre along the line it already sits on, just
    /// far enough. Always from where the game put it, so it never drifts.
    /// </summary>
    private static void MoveCharge(SlotState slot, Vector2 center, float radius)
    {
        var charge = (AtkResNode*)slot.Charge;
        var (origin, parentScale) = ToAddon(charge->ParentNode, Vector2.Zero);
        if (MathF.Abs(parentScale.X) < 0.0001f || MathF.Abs(parentScale.Y) < 0.0001f)
            return;

        var size = new Vector2(charge->Width * charge->ScaleX, charge->Height * charge->ScaleY) * parentScale;
        var counter = origin + parentScale * slot.ChargeOrigin + size / 2f;

        var away = counter - center;
        var distance = away.Length();
        var wanted = radius + size.X * 0.3f;

        var position = slot.ChargeOrigin;
        if (distance > 0.01f && distance < wanted)
            position += away / distance * (wanted - distance) / parentScale;

        if (MathF.Abs(charge->X - position.X) > 0.01f || MathF.Abs(charge->Y - position.Y) > 0.01f)
            charge->SetPositionFloat(position.X, position.Y);
    }

    private static void Place(NativeInsertedNode node, Vector2 topLeft, Vector2 diameter, ushort size)
    {
        var (origin, parentScale) = ToAddon(node.Parent, Vector2.Zero);
        if (MathF.Abs(parentScale.X) < 0.0001f || MathF.Abs(parentScale.Y) < 0.0001f)
            return;

        node.Place((topLeft - origin) / parentScale, size, diameter / (parentScale * size));
    }

    private static void SetAlpha(AtkResNode* node, byte alpha)
    {
        node->Color.A = alpha;
        node->DrawFlags |= 1;
    }

    /// <summary>Where a point of a node is in the window, and the scale the node is drawn at. Rotation is ignored.</summary>
    private static (Vector2 Position, Vector2 Scale) ToAddon(AtkResNode* node, Vector2 point)
    {
        var scale = Vector2.One;
        for (var current = node; current != null; current = current->ParentNode)
        {
            var origin = new Vector2(current->OriginX, current->OriginY);
            var own = new Vector2(current->ScaleX, current->ScaleY);
            point = new Vector2(current->X, current->Y) + origin + own * (point - origin);
            scale *= own;
        }

        return (point, scale);
    }

    /// <summary>Undoes everything done to a hotbar. Called while the hotbar is still alive.</summary>
    private void Remove(AtkUnitBase* addon)
    {
        if (addon == null || !_bars.Remove((nint)addon, out var bar))
            return;

        // In the reverse order they were added, so every node finds its neighbours as it left them.
        var added = new List<NativeInsertedNode>();
        foreach (var slot in bar.Slots)
        {
            added.AddRange(slot.Masks);
            if (slot.Sphere is { } sphere)
                added.Add(sphere);
            if (slot.Ring is { } ring)
                added.Add(ring);

            if (slot.Charge != 0)
                ((AtkResNode*)slot.Charge)->SetPositionFloat(slot.ChargeOrigin.X, slot.ChargeOrigin.Y);

            foreach (var background in slot.Backgrounds.Where(background => background.Hidden))
                SetAlpha((AtkResNode*)background.Node, background.Alpha);
        }

        for (var i = added.Count - 1; i >= 0; i--)
            added[i].Unlink();

        foreach (var (manager, hadMasks) in bar.Managers)
        {
            if (!hadMasks)
                ((AtkUldManager*)manager)->ResourceFlags &= ~AtkUldManagerResourceFlag.ContainsClippingMaskNode;
        }

        // The draw lists must forget the nodes before they are freed.
        UpdateDrawLists(bar);

        foreach (var node in added)
            node.Free();
    }

    /// <summary>Undoes every hotbar, then frees the textures.</summary>
    private void RemoveAll()
    {
        foreach (var (address, bar) in _bars.ToList())
        {
            // Only a hotbar that is still open can be touched; one that is gone took our nodes with it.
            if ((nint)Svc.GameGui.GetAddonByName(bar.Name).Address == address)
            {
                Remove((AtkUnitBase*)address);
            }
            else
            {
                _bars.Remove(address);
                _texturesInUse = true;
                Svc.Log.Warning("GoblinHotbarIcons: {name} closed without notice; its masks were left in place.", bar.Name);
            }
        }

        // Bent icons on their way are for options that no longer apply.
        _sphereGeneration++;

        if (_texturesInUse)
            return;

        foreach (var (mask, ring) in _textures.Values)
        {
            mask.Dispose();
            ring.Dispose();
        }

        _textures.Clear();

        foreach (var sphere in _spheres.Values)
            sphere.Part?.Dispose();

        _spheres.Clear();
    }

    /// <summary>The bent version of an icon; asked for on a worker thread the first time.</summary>
    private SphereTexture SphereFor(uint iconId)
    {
        if (_spheres.TryGetValue(iconId, out var known))
            return known;

        var sphere = new SphereTexture();
        _spheres[iconId] = sphere;

        var generation = _sphereGeneration;
        var strength = Settings.SphereStrength / 100f;
        var crop = Settings.CircleSize / 100f;
        _ = SphereIcons.MakeAsync(iconId, strength, crop).ContinueWith(task =>
        {
            var (size, pixels) = task.IsCompletedSuccessfully ? task.Result : (0, null);
            _sphereResults.Enqueue(new SphereResult(iconId, generation, size, pixels));
        }, TaskScheduler.Default);

        return sphere;
    }

    /// <summary>Turns the bent icons made on worker threads into textures. On the framework thread.</summary>
    private void TakeSphereResults()
    {
        while (_sphereResults.TryDequeue(out var result))
        {
            if (result.Generation != _sphereGeneration || !_spheres.TryGetValue(result.Icon, out var sphere))
                continue;

            if (result.Pixels is null)
            {
                sphere.Failed = true;
                continue;
            }

            sphere.Part = NativeTexturePart.Create(result.Size, result.Pixels, $"GoblinTweaks sphere {result.Icon}");
            sphere.Failed = sphere.Part is null;
        }
    }

    private (NativeTexturePart Mask, NativeTexturePart Ring)? TexturesFor(ushort size)
    {
        if (_textures.TryGetValue(size, out var known))
            return known;

        var mask = NativeTexturePart.Create(size, CirclePixels(size, ring: false), $"GoblinTweaks circle {size}");
        var ring = NativeTexturePart.Create(size, CirclePixels(size, ring: true), $"GoblinTweaks ring {size}");
        if (mask is null || ring is null)
        {
            mask?.Dispose();
            ring?.Dispose();
            return null;
        }

        _textures[size] = (mask, ring);
        return (mask, ring);
    }

    /// <summary>How wide the ring is, in pixels of a texture of <paramref name="size"/>, the size of the circle.</summary>
    private static float RingWidth(int size) => MathF.Max(1.5f, size * 0.06f);

    /// <summary>
    /// A white circle on black (the mask) or a dark ring at its edge, the size of the texture, with smooth edges. The mask
    /// is opaque like the game's own (ui/uld/BgPartsMask.tex): it is the color that tells what is kept.
    /// </summary>
    private static byte[] CirclePixels(int size, bool ring)
    {
        var pixels = new byte[size * size * 4];
        var radius = size / 2f;
        var width  = RingWidth(size);

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(radius, radius));
                var inside = Math.Clamp(radius - distance + 0.5f, 0f, 1f);
                if (ring)
                    inside *= Math.Clamp(distance - (radius - width) + 0.5f, 0f, 1f);

                var i = (y * size + x) * 4;
                if (ring)
                {
                    pixels[i] = 18;
                    pixels[i + 1] = 14;
                    pixels[i + 2] = 10;
                    pixels[i + 3] = (byte)(inside * 235f);
                }
                else
                {
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(inside * 255f);
                    pixels[i + 3] = 255;
                }
            }
        }

        return pixels;
    }
}
