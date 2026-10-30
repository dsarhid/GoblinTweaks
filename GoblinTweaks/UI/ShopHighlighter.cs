using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace GoblinTweaks.UI;

/// <summary>
/// While a shop is open (gil, scrips, seals, tomestones...), dims every row of its list that does not sell one of the items
/// it is given (the ingredients of the recipe selected in Crafting Materials). A shop that sells none of them is left as
/// it is. Like the other highlighters, the dims are native nodes added to the game's own window, removed when it closes,
/// and nothing is done if the window is not laid out as expected.
/// </summary>
/// <remarks>
/// It does not rely on the exact names of the shop windows nor on what their lists hold: every visible window whose name
/// says it is a shop is read as rows of a list, and what each row sells is read from the names and icons it draws. The
/// names are what counts in the gil shops, whose rows all draw the same icon; the icons in the exchange shops. Only the rows on
/// screen can be read, so a shop counts as selling something wanted from the moment such a row is seen until it closes:
/// scrolling past that row does not bring the shop back to normal.
/// </remarks>
internal sealed unsafe class ShopHighlighter : IDisposable
{
    // Windows that have "Shop" in their name but are not a list of what is sold.
    private static readonly string[] NotShops = ["Dialog", "Context", "Detail", "Collectables", "Dispose", "Sell", "Repair"];

    // Shops whose name does not say so.
    private static readonly string[] OtherShops = ["GrandCompanyExchange"];

    /// <summary>Supplies the item ids to keep in view. Null/empty = nothing to do.</summary>
    public Func<IReadOnlyCollection<uint>>? Source;

    // The look of a dimmed row. Rows next to each other join into one dark block, out of which the wanted ones stand.
    private static readonly Vector4 DimColor = new(0f, 0f, 0f, 0.6f);

    // Room left before the icon of a row.
    private const float EdgeRoom = 2f;

    // Room left between a row and the scroll bar of its list.
    private const float ScrollBarGap = 3f;

    private readonly Dictionary<string, List<ColorImageNode>> _dims = [];
    private readonly Dictionary<uint, uint> _icons = [];
    private readonly Dictionary<uint, string> _names = [];
    private readonly HashSet<uint> _wantedIcons = [];
    private readonly HashSet<string> _wantedNames = new(StringComparer.OrdinalIgnoreCase);
    private string _wantedSignature = string.Empty;

    // The shops seen selling something wanted, by window name and address: they stay dimmed until they close.
    private readonly Dictionary<string, nint> _matched = [];

    // The windows we have a say in when they close (see InventoryHighlighter).
    private readonly HashSet<string> _listening = [];
    private readonly HashSet<string> _logged = [];
    private bool _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        Svc.Framework.Update += OnUpdate;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        Svc.Framework.Update -= OnUpdate;
        foreach (var name in _listening) Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, name, OnClosing);
        _listening.Clear();
        foreach (var name in _dims.Keys.ToList()) Clear(name);
        _matched.Clear();
    }

    public void Dispose() => Stop();

    // A window is about to be destroyed: our nodes must leave it first.
    private void OnClosing(AddonEvent type, AddonArgs args)
    {
        Clear(args.AddonName);
        _matched.Remove(args.AddonName);
    }

    private void Clear(string addon)
    {
        if (!_dims.Remove(addon, out var nodes)) return;
        foreach (var node in nodes) node.Dispose();
    }

    /// <summary>Hides the dims of a window without freeing them: the window stays, and they may be needed again.</summary>
    private void Hide(string addon)
    {
        if (!_dims.TryGetValue(addon, out var nodes)) return;
        foreach (var node in nodes) node.IsVisible = false;
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            var ids = Source?.Invoke();
            if (ids is null || ids.Count == 0)
            {
                foreach (var name in _dims.Keys) Hide(name);
                _matched.Clear();
                return;
            }

            WantedIcons(ids);
            Mark();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "ShopHighlighter: update failed");
            Source = null; // stop instead of spamming the log every frame
            foreach (var name in _dims.Keys.ToList()) Clear(name);
        }
    }

    /// <summary>The names and icons of the wanted items. Another set of items makes every shop be looked at again.</summary>
    private void WantedIcons(IReadOnlyCollection<uint> ids)
    {
        var signature = string.Join(",", ids.Order());
        if (signature == _wantedSignature) return;

        _wantedSignature = signature;
        _matched.Clear();
        _wantedIcons.Clear();
        _wantedNames.Clear();
        foreach (var id in ids)
        {
            Read(id, out var icon, out var name);
            if (icon != 0) _wantedIcons.Add(icon);
            if (name.Length > 0) _wantedNames.Add(name);
        }
    }

    /// <summary>The icon of an item and its name in the language of the game, as the shops write it.</summary>
    private void Read(uint itemId, out uint icon, out string name)
    {
        if (!_icons.TryGetValue(itemId, out icon) || !_names.TryGetValue(itemId, out name!))
        {
            var found = Svc.Data.GetExcelSheet<LuminaItem>().TryGetRow(itemId, out var row);
            _icons[itemId] = icon = found ? row.Icon : 0u;
            _names[itemId] = name = found ? Clean(row.Name.ExtractText()) : string.Empty;
        }
    }

    /// <summary>A name without the marks the game font draws around it (the HQ symbol and the like) and without spaces at its ends.</summary>
    private static string Clean(string text)
    {
        var kept = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.PrivateUse)
                kept.Append(c);
        return kept.ToString().Trim();
    }

    private static bool IsShop(string name)
        => (name.Contains("Shop", StringComparison.Ordinal) || OtherShops.Contains(name))
           && !NotShops.Any(word => name.Contains(word, StringComparison.Ordinal));

    private readonly record struct Row(float X, float Y, float W, float H, bool Wanted);

    private void Mark()
    {
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return;

        var list = &stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        var seen = new HashSet<string>();
        for (var i = 0; i < list->Count; i++)
        {
            var addon = list->Entries[i].Value;
            if (addon == null || !addon->IsVisible || addon->UldManager.NodeList == null) continue;

            var name = addon->NameString;
            if (!IsShop(name)) continue;
            seen.Add(name);

            var rows = new List<Row>();
            Collect(&addon->UldManager, rows, 0, addon->Scale <= 0f ? 1f : addon->Scale, clip: null);
            rows = OnScreen(rows);

            if (rows.Any(row => row.Wanted)) _matched[name] = (nint)addon;
            var matched = _matched.TryGetValue(name, out var at) && at == (nint)addon;

            LogOnce($"{name}:{rows.Count}:{rows.Count(r => r.Wanted)}:{matched}",
                $"ShopHighlighter: {name}: {rows.Count} rows on screen, {rows.Count(r => r.Wanted)} selling something wanted; "
                + $"sells something wanted: {matched}; wanted [{string.Join(", ", _wantedNames)}].");

            // A shop that has not shown anything wanted is left as it is.
            if (!matched) { Hide(name); continue; }

            Place(addon, name, rows.Where(row => !row.Wanted).ToList());
        }

        // Windows that were dimmed and are not there any more.
        foreach (var name in _dims.Keys.Where(name => !seen.Contains(name)).ToList()) Clear(name);
        foreach (var name in _matched.Keys.Where(name => !seen.Contains(name)).ToList()) _matched.Remove(name);
    }

    /// <summary>
    /// The rows of the lists of a window that are on screen, cut to the part of the list they show in, and whether they
    /// sell something wanted.
    /// </summary>
    private void Collect(AtkUldManager* uld, List<Row> rows, int depth, float scale, (float Top, float Bottom, float Right)? clip)
    {
        if (depth > 8 || uld == null || uld->NodeList == null) return;

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || (ushort)node->Type < 1000 || !node->IsVisible()) continue; // component nodes only

            var component = ((AtkComponentNode*)node)->Component;
            if (component == null) continue;

            // In screen pixels, as ScreenX and ScreenY are.
            var type   = component->GetComponentType();
            var left   = node->ScreenX;
            var top    = node->ScreenY;
            var width  = node->Width * node->ScaleX * scale;
            var height = node->Height * node->ScaleY * scale;

            if (type == ComponentType.ListItemRenderer)
            {
                // A row the list keeps out of its view (ready for scrolling) is not on screen, whatever it says.
                var bottom = top + height;
                var right  = left + width;
                if (clip is { } c)
                {
                    if (top + height / 2f < c.Top || top + height / 2f > c.Bottom) continue;
                    top    = MathF.Max(top, c.Top);
                    bottom = MathF.Min(bottom, c.Bottom);

                    // Some columns (the count in the bags) are drawn past the end of the row: the dim goes on to the
                    // scroll bar, and not over it.
                    if (c.Right > left) right = c.Right;
                }

                var icons = new List<uint>();
                var texts = new List<string>();
                var drawnLeft = left;
                ReadRow(&component->UldManager, icons, texts, ref drawnLeft, 0);
                if (icons.Count == 0 && texts.Count == 0) continue; // an empty row

                // The dim starts where the row starts to draw (its icon), a little before it.
                left = drawnLeft - EdgeRoom * scale;

                rows.Add(new Row(left, top, right - left, bottom - top,
                    texts.Any(_wantedNames.Contains) || icons.Any(_wantedIcons.Contains)));
                continue;
            }

            // What a list shows is what falls inside it, up to its scroll bar.
            var inner = clip;
            if (type is ComponentType.List or ComponentType.TreeList)
            {
                var end = ScrollBarLeft(&component->UldManager) is { } bar ? bar - ScrollBarGap * scale : left + width;
                inner = clip is { } outer
                    ? (MathF.Max(outer.Top, top), MathF.Min(outer.Bottom, top + height), MathF.Min(outer.Right, end))
                    : (top, top + height, end);
            }

            Collect(&component->UldManager, rows, depth + 1, scale, inner);
        }
    }

    /// <summary>Where the scroll bar of a list starts on screen; null when it has none showing.</summary>
    private static float? ScrollBarLeft(AtkUldManager* uld)
    {
        if (uld == null || uld->NodeList == null) return null;

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || (ushort)node->Type < 1000 || !node->IsVisible()) continue;

            var component = ((AtkComponentNode*)node)->Component;
            if (component != null && component->GetComponentType() == ComponentType.ScrollBar)
                return node->ScreenX;
        }

        return null;
    }

    /// <summary>
    /// Rows drawn over one another are one row on screen: wanted if any of them is (a row the list has not moved out of
    /// the way yet must not dim the one it overlaps), and dimmed once.
    /// </summary>
    private static List<Row> OnScreen(List<Row> rows)
    {
        var result = new List<Row>();
        foreach (var row in rows.OrderByDescending(row => row.Wanted))
        {
            var overlap = result.FindIndex(kept => MathF.Abs(kept.Y - row.Y) < MathF.Min(kept.H, row.H) / 2f
                                                   && MathF.Abs(kept.X - row.X) < MathF.Min(kept.W, row.W) / 2f);
            if (overlap < 0) result.Add(row);
        }

        return result;
    }

    /// <summary>The texts and item icons a row draws: what it sells, and what it costs in an exchange shop.</summary>
    private static void ReadRow(AtkUldManager* uld, List<uint> icons, List<string> texts, ref float drawnLeft, int depth)
    {
        if (depth > 4 || uld == null || uld->NodeList == null) return;

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || !node->IsVisible()) continue;

            // Where the row really starts on screen: its icon is drawn before the start of the row itself.
            if (node->Type is NodeType.Image or NodeType.Text && node->Width > 0)
                drawnLeft = MathF.Min(drawnLeft, node->ScreenX);

            if (node->Type == NodeType.Image)
            {
                var image = (AtkImageNode*)node;
                var parts = image->PartsList;
                if (parts == null || parts->Parts == null || image->PartId >= parts->PartCount) continue;

                var asset = parts->Parts[image->PartId].UldAsset;
                if (asset == null || asset->AtkTexture.Resource == null) continue;

                // 0xFFFFFFFF is no icon; a high-quality item is drawn with its icon + 1,000,000, a collectable + 500,000.
                var id = asset->AtkTexture.Resource->IconId;
                if (id == 0 || id == uint.MaxValue) continue;
                icons.Add(id >= 1_000_000 ? id - 1_000_000 : id >= 500_000 ? id - 500_000 : id);
            }
            else if (node->Type == NodeType.Text)
            {
                var text = ((AtkTextNode*)node)->NodeText;
                if (text.Length == 0) continue;

                var value = Clean(SeString.Parse(text.AsSpan()).TextValue);
                if (value.Length > 0) texts.Add(value);
            }
            else if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null) ReadRow(&component->UldManager, icons, texts, ref drawnLeft, depth + 1);
            }
        }
    }

    private void Place(AtkUnitBase* addon, string name, List<Row> rows)
    {
        // Before a node goes into a window, we must be told when it is going to close.
        if (_listening.Add(name))
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, name, OnClosing);

        if (!_dims.TryGetValue(name, out var nodes))
            _dims[name] = nodes = [];

        // The rows on screen change as the list scrolls: more dims are made when needed, the ones left over are hidden.
        while (nodes.Count < rows.Count)
        {
            var dim = new ColorImageNode { Color = DimColor };
            dim.AttachNode(addon);
            nodes.Add(dim);
        }

        // Relative to the root of the window the nodes belong to, so they follow it when it is dragged. The rows are
        // in screen pixels; the nodes are in the window's own units.
        var scale = addon->Scale <= 0f ? 1f : addon->Scale;
        var rootX = addon->RootNode != null ? addon->RootNode->ScreenX : addon->X;
        var rootY = addon->RootNode != null ? addon->RootNode->ScreenY : addon->Y;

        for (var i = 0; i < nodes.Count; i++)
        {
            if (i >= rows.Count)
            {
                nodes[i].IsVisible = false;
                continue;
            }

            var row  = rows[i];
            var size = new Vector2(row.W / scale, row.H / scale);
            if (size.X <= 0f || size.Y <= 0f)
            {
                nodes[i].IsVisible = false;
                continue;
            }

            nodes[i].Position  = new Vector2((row.X - rootX) / scale,(row.Y - rootY) / scale);
            nodes[i].Size      = size;
            nodes[i].IsVisible = true;
        }
    }

    // Diagnostics of how a window was read: written once per distinct state, at Debug level.
    private const int MaxLogged = 500;

    private void LogOnce(string key, string message)
    {
        if (_logged.Count >= MaxLogged) _logged.Clear();
        if (_logged.Add(key)) Svc.Log.Debug(message);
    }
}
