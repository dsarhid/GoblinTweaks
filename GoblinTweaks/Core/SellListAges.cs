using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Services;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace GoblinTweaks.Core;

/// <summary>When one listing went up and when its price was last set (Unix seconds, 0 = not known).</summary>
public sealed class ListingTime
{
    public uint ItemId { get; set; }
    public bool Hq { get; set; }
    public int Quantity { get; set; }
    public uint Price { get; set; }
    public long Listed { get; set; }
    public long Priced { get; set; }
    /// <summary>The time came from Universalis (when it last saw the listing), not from watching the list.</summary>
    public bool ListedApprox { get; set; }
    public bool PricedApprox { get; set; }
}

/// <summary>
/// Two columns added to a retainer's sell list: how long ago each item was put up and how long ago its price was last
/// set. The times are written down while the list is open (so every listing and price change made here, by hand or by
/// Pinch, is exact). Whatever changed while it was closed (another PC, the plugin off) is filled in from Universalis
/// (when it last saw the listing), which is close but not exact.
/// </summary>
internal sealed unsafe class SellListAges(Func<Dictionary<ulong, List<ListingTime>>> store, Action save, Func<string, string> text) : IDisposable
{
    /// <summary>How much wider the window gets: a gap and two columns.</summary>
    private const float Extra = 210f;
    private const float ColumnWidth = 92f;
    /// <summary>Space between the total price and the first new column.</summary>
    private const float ColumnGap = 18f;
    /// <summary>Nodes at least this wide stretch with the window; nodes this far right move with its right edge.</summary>
    private const float Stretch = 500f;

    private const uint HeaderNodeId = 5;
    private const uint ListNodeId = 11;
    private const uint TotalHeaderNodeId = 9; // "Total Price" title, to copy its font
    private const uint RowTotalNodeId = 7;    // a row's total price, to copy its font and place the columns after it

    // Per row of the list, the game's data holds 13 values from index 10; the 6th is the market slot.
    private const int RowValuesStart = 10;
    private const int RowValuesStride = 13;
    private const int RowIconValue = 0;
    private const int RowSlotValue = 5;

    private static readonly TimeSpan ObserveEvery = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan UniversalisRetry = TimeSpan.FromMinutes(5);

    private readonly Dictionary<nint, (float X, ushort Width)> _original = [];
    private readonly HashSet<nint> _ours = [];
    private readonly Dictionary<nint, (TextNode Listed, TextNode Priced)> _rows = [];
    private readonly List<TextNode> _headers = [];
    private readonly ListingTime?[] _bySlot = new ListingTime?[20];
    private readonly Dictionary<ulong, DateTime> _askedUniversalis = [];

    private AtkUnitBase* _addon;
    private float _columnsX;
    private ulong _retainer;
    private bool _watching; // the list was seen complete since it opened: what changes from now on is exact
    private DateTime _nextObserve;
    private CancellationTokenSource? _lookup;
    private int _rowLogs; // TEMP
    private ushort _rendererWidth; // a row before widening

    public bool IsAttached => _addon != null;

    /// <summary>Widens the open sell list and adds the column titles. Call before buttons that sit on its right edge.</summary>
    public void Attach(AtkUnitBase* addon)
    {
        Detach(restore: true);
        if (addon == null || addon->RootNode == null) return;

        var header = addon->GetNodeById(HeaderNodeId);
        var list   = ListOf(addon);
        var title  = addon->GetNodeById(TotalHeaderNodeId);
        if (header == null || list == null || title == null || title->Type != NodeType.Text) return;

        _addon = addon;
        _rendererWidth = RendererWidth(list);
        Widen(addon->RootNode);

        // The titles line up with the row columns: rows start at the list's X.
        _columnsX = ((AtkResNode*)list->OwnerNode)->X + RowColumnsX(list);
        var font = (AtkTextNode*)title;
        _headers.Add(NewText(font, _columnsX, title->Y, title->Height, text("SellAge.Listed"), header));
        _headers.Add(NewText(font, _columnsX + ColumnWidth, title->Y, title->Height, text("SellAge.Priced"), header));

        _watching = false;
        _retainer = 0;
        _nextObserve = DateTime.MinValue;
        _rowLogs = 0;
        Array.Clear(_bySlot);
    }

    /// <summary>Removes the columns. With <paramref name="restore"/> the window is shrunk back (the tweak was switched off with it open).</summary>
    public void Detach(bool restore)
    {
        _lookup?.Cancel();
        _lookup = null;

        foreach (var (listed, priced) in _rows.Values)
        {
            listed.Dispose();
            priced.Dispose();
        }
        _rows.Clear();
        foreach (var node in _headers) node.Dispose();
        _headers.Clear();
        _ours.Clear();

        if (restore && _addon != null)
            foreach (var (address, (x, width)) in _original)
            {
                var node = (AtkResNode*)address;
                node->SetPositionFloat(x, node->Y);
                node->SetWidth(width);
            }

        _original.Clear();
        _addon = null;
    }

    public void Dispose() => Detach(restore: true);

    /// <summary>Every frame while the list is open: notes changes and fills the rows on screen.</summary>
    public void Update(AtkUnitBase* addon)
    {
        if (addon != _addon || addon == null || !addon->IsVisible) return;

        var now = DateTime.UtcNow;
        if (now >= _nextObserve)
        {
            _nextObserve = now + ObserveEvery;
            Observe(now);
        }

        Draw(addon, now);
    }

    // ── Watching the listings ────────────────────────────────────────────────

    private void Observe(DateTime now)
    {
        var retMgr = RetainerManager.Instance();
        var active = retMgr == null ? null : retMgr->GetActiveRetainer();
        var inv    = InventoryManager.Instance();
        var market = inv == null ? null : inv->GetInventoryContainer(InventoryType.RetainerMarket);
        if (active == null || market == null) return;

        if (active->RetainerId != _retainer)
        {
            _retainer = active->RetainerId;
            _watching = false;
            Array.Clear(_bySlot);
        }

        // The list fills in a moment after the window opens: until it is all there, nothing is judged.
        var current = new List<(int Slot, uint Item, bool Hq, int Quantity, uint Price)>();
        for (var i = 0; i < market->Size && i < _bySlot.Length; i++)
        {
            var slot = market->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0) continue;
            var price = inv->RetainerMarketPrices[i];
            if (price == 0) return;
            current.Add((i, slot->ItemId, slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), slot->Quantity, (uint)price));
        }
        if (current.Count != active->MarketItemCount) return;

        var records = store().TryGetValue(_retainer, out var saved) ? saved : [];
        var left    = new List<ListingTime>(records);
        var stamp   = new DateTimeOffset(now).ToUnixTimeSeconds();
        var known   = _watching ? stamp : 0; // a change first seen on opening happened at an unknown time
        var changed = false;
        var result  = new List<ListingTime>();
        Array.Clear(_bySlot);

        foreach (var (slot, item, hq, quantity, price) in current)
        {
            var record = left.FirstOrDefault(r => r.ItemId == item && r.Hq == hq && r.Quantity == quantity && r.Price == price)
                      ?? left.FirstOrDefault(r => r.ItemId == item && r.Hq == hq && r.Quantity == quantity);
            if (record is null)
            {
                record = new ListingTime { ItemId = item, Hq = hq, Quantity = quantity, Price = price, Listed = known, Priced = known };
                changed = true;
            }
            else
            {
                left.Remove(record);
                if (record.Price != price)
                {
                    record.Price = price;
                    record.Priced = known;
                    record.PricedApprox = false;
                    changed = true;
                }
            }

            result.Add(record);
            _bySlot[slot] = record;
        }

        // What is gone was sold or taken back.
        if (left.Count > 0) changed = true;
        _watching = true;

        if (changed)
        {
            store()[_retainer] = result;
            save();
        }

        if (result.Any(r => r.Listed == 0 || r.Priced == 0))
            AskUniversalis(active, now);
    }

    /// <summary>Fills the times nobody saw with when Universalis last saw each listing, once per retainer every few minutes.</summary>
    private void AskUniversalis(RetainerManager.Retainer* retainer, DateTime now)
    {
        if (_lookup is not null) return;
        if (_askedUniversalis.TryGetValue(_retainer, out var asked) && now - asked < UniversalisRetry) return;

        var world = SellPrices.HomeWorld();
        if (string.IsNullOrEmpty(world)) return;

        _askedUniversalis[_retainer] = now;
        var id    = _retainer;
        var name  = SellInventory.RetainerName(retainer, 0);
        var items = _bySlot.Where(r => r is { } && (r.Listed == 0 || r.Priced == 0)).Select(r => r!.ItemId).Distinct().ToList();
        var stop  = new CancellationTokenSource();
        _lookup = stop;

        SellListLookup.Start(world, items, name, stop.Token,
            found => ApplyUniversalis(id, found, stop),
            () => { if (_lookup == stop) _lookup = null; });
    }

    private void ApplyUniversalis(ulong retainer, Dictionary<uint, List<MarketListing>> found, CancellationTokenSource request)
    {
        if (_lookup == request) _lookup = null;
        if (request.IsCancellationRequested || !store().TryGetValue(retainer, out var records)) return;

        var changed = false;
        var used    = new HashSet<MarketListing>(ReferenceEqualityComparer.Instance);
        foreach (var record in records.Where(r => r.Listed == 0 || r.Priced == 0))
        {
            if (!found.TryGetValue(record.ItemId, out var listings)) continue;
            var match = listings.FirstOrDefault(l => !used.Contains(l) && l.Hq == record.Hq && l.Quantity == record.Quantity && l.Price == record.Price && l.Reviewed > DateTime.MinValue);
            if (match is null) continue;

            used.Add(match);
            var seen = new DateTimeOffset(match.Reviewed, TimeSpan.Zero).ToUnixTimeSeconds();
            if (record.Listed == 0) { record.Listed = seen; record.ListedApprox = true; }
            if (record.Priced == 0) { record.Priced = seen; record.PricedApprox = true; }
            changed = true;
        }

        // Universalis may still have an older price: the listing was up by then, but when the price changed is unknown.
        foreach (var record in records.Where(r => r.Listed == 0))
        {
            if (!found.TryGetValue(record.ItemId, out var listings)) continue;
            var match = listings.FirstOrDefault(l => !used.Contains(l) && l.Hq == record.Hq && l.Quantity == record.Quantity && l.Reviewed > DateTime.MinValue);
            if (match is null) continue;

            used.Add(match);
            record.Listed = new DateTimeOffset(match.Reviewed, TimeSpan.Zero).ToUnixTimeSeconds();
            record.ListedApprox = true;
            changed = true;
        }

        if (changed) save();
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    private void Draw(AtkUnitBase* addon, DateTime now)
    {
        var list = ListOf(addon);
        if (list == null) return;

        // Rows the list makes later (or after a refresh) are widened too; each one gets its two texts.
        for (var node = list->UldManager.RootNode; node != null; node = node->PrevSiblingNode)
        {
            if (!IsRenderer(node) || _rows.ContainsKey((nint)node)) continue;
            // A row copied from one already widened comes out widened.
            if (_rendererWidth == 0) _rendererWidth = node->Width;
            if (node->Width == _rendererWidth || _original.ContainsKey((nint)node)) Widen(node, siblings: false);
            var font = (AtkTextNode*)((AtkComponentNode*)node)->Component->UldManager.SearchNodeById(RowTotalNodeId);
            if (font == null || ((AtkResNode*)font)->Type != NodeType.Text) continue;
            var x = RowColumnsX(list);
            var y = ((AtkResNode*)font)->Y;
            var h = ((AtkResNode*)font)->Height;
            _rows[(nint)node] = (NewText(font, x, y, h, string.Empty, (AtkComponentNode*)node),
                                 NewText(font, x + ColumnWidth, y, h, string.Empty, (AtkComponentNode*)node));
        }

        var shown = new HashSet<nint>();
        // Only the rows on screen: an entry scrolled away may still point at a row the list reuses for another item.
        var count = Math.Min(Math.Min(list->ListLength, list->AllocatedItemRendererListLength), (addon->AtkValuesCount - RowValuesStart) / RowValuesStride);
        var first = Math.Max(0, list->FirstVisibleItemIndex);
        var last  = Math.Min(count, first + list->NumVisibleRows + 1);
        for (var i = first; i < last && list->ItemRendererList != null; i++)
        {
            var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer == null || renderer->OwnerNode == null) continue;
            var key = (nint)renderer->OwnerNode;
            if (!_rows.TryGetValue(key, out var row) || !shown.Add(key)) continue;

            var record = RecordOfRow(addon, i);
            // TEMP: to check in game that each row gets the times of its own item.
            if (_rowLogs < 40)
            {
                _rowLogs++;
                Svc.Log.Info($"SELLAGES row {i} renderer #{renderer->OwnerNode->AtkResNode.NodeId} y {renderer->OwnerNode->AtkResNode.Y} slot {addon->AtkValues[RowValuesStart + i * RowValuesStride + RowSlotValue].Int} item {record?.ItemId} listed {record?.Listed} priced {record?.Priced}");
            }
            Set(row.Listed, record is null ? string.Empty : Age(record.Listed, now));
            Set(row.Priced, record is null ? string.Empty : Age(record.Priced, now));
        }

        foreach (var (key, row) in _rows)
            if (!shown.Contains(key))
            {
                Set(row.Listed, string.Empty);
                Set(row.Priced, string.Empty);
            }
    }

    /// <summary>The listing shown on row <paramref name="row"/>, checked against the item's icon so a changed layout shows nothing instead of wrong times.</summary>
    private ListingTime? RecordOfRow(AtkUnitBase* addon, int row)
    {
        var start = RowValuesStart + row * RowValuesStride;
        var icon  = addon->AtkValues[start + RowIconValue];
        var slot  = addon->AtkValues[start + RowSlotValue];
        if (icon.Type != AtkValueType.Int || slot.Type != AtkValueType.Int || slot.Int < 0 || slot.Int >= _bySlot.Length) return null;

        var record = _bySlot[slot.Int];
        if (record is null) return null;

        var item = Svc.Data.GetExcelSheet<LuminaItem>()?.GetRowOrDefault(record.ItemId);
        var expected = item is null ? 0 : (int)item.Value.Icon + (record.Hq ? 1_000_000 : 0);
        return expected == icon.Int ? record : null;
    }

    /// <summary>"now", "12m", "5h 20m", "50h 3m" (hours and minutes only); "?" when nobody knows.</summary>
    private string Age(long unix, DateTime now)
    {
        if (unix == 0) return _lookup is null ? "?" : "…";

        var age = now - DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalMinutes < 1 ? text("SellAge.Now")
             : age.TotalHours < 1   ? $"{age.Minutes}m"
             :                        $"{(int)age.TotalHours}h {age.Minutes}m";
    }

    private static void Set(TextNode node, string value)
    {
        if (node.String != value) node.String = value;
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private static AtkComponentList* ListOf(AtkUnitBase* addon)
    {
        var node = addon->GetNodeById(ListNodeId);
        if (node == null || (int)node->Type < 1000) return null;
        var component = ((AtkComponentNode*)node)->Component;
        return component != null && component->GetComponentType() == ComponentType.List ? (AtkComponentList*)component : null;
    }

    private static bool IsRenderer(AtkResNode* node)
        => (int)node->Type >= 1000 && ((AtkComponentNode*)node)->Component is var c && c != null && c->GetComponentType() == ComponentType.ListItemRenderer;

    private static ushort RendererWidth(AtkComponentList* list)
    {
        for (var node = list->UldManager.RootNode; node != null; node = node->PrevSiblingNode)
            if (IsRenderer(node)) return node->Width;
        return 0;
    }

    /// <summary>Where the new columns start inside a row: right after the total price.</summary>
    private static float RowColumnsX(AtkComponentList* list)
    {
        for (var node = list->UldManager.RootNode; node != null; node = node->PrevSiblingNode)
        {
            if (!IsRenderer(node)) continue;
            var total = ((AtkComponentNode*)node)->Component->UldManager.SearchNodeById(RowTotalNodeId);
            if (total != null) return total->X + total->Width + ColumnGap;
        }
        return 534f;
    }

    /// <summary>
    /// Makes <paramref name="node"/> and what is inside it <see cref="Extra"/> wider: wide nodes stretch (and their
    /// insides are looked at), nodes near the right edge move right. Remembers the original sizes to put them back.
    /// </summary>
    private void Widen(AtkResNode* node, bool siblings = true)
    {
        for (; node != null; node = siblings ? node->PrevSiblingNode : null)
        {
            if (_ours.Contains((nint)node)) continue;
            if (!_original.TryGetValue((nint)node, out var original))
                _original[(nint)node] = original = (node->X, node->Width);

            if (original.Width >= Stretch)
            {
                node->SetWidth((ushort)(original.Width + Extra));
                if ((int)node->Type >= 1000)
                {
                    var component = ((AtkComponentNode*)node)->Component;
                    if (component != null) Widen(component->UldManager.RootNode);
                }
                else
                {
                    Widen(node->ChildNode);
                }
            }
            else if (original.X >= Stretch)
            {
                node->SetPositionFloat(original.X + Extra, node->Y);
            }
        }
    }

    private TextNode NewText(AtkTextNode* font, float x, float y, float height, string value, AtkResNode* parent)
    {
        var node = MakeText(font, x, y, height, value);
        node.AttachNode(parent);
        _ours.Add((nint)(AtkResNode*)node);
        return node;
    }

    private TextNode NewText(AtkTextNode* font, float x, float y, float height, string value, AtkComponentNode* parent)
    {
        var node = MakeText(font, x, y, height, value);
        node.AttachNode(parent);
        _ours.Add((nint)(AtkResNode*)node);
        return node;
    }

    private static TextNode MakeText(AtkTextNode* font, float x, float y, float height, string value)
    {
        var node = new TextNode
        {
            Position         = new Vector2(x, y),
            Size             = new Vector2(ColumnWidth, height),
            AlignmentType    = AlignmentType.Center,
            FontSize         = font->FontSize,
            TextColor        = Color(font->TextColor),
            TextOutlineColor = Color(font->EdgeColor),
            String           = value,
        };
        if (font->TextFlags.HasFlag(TextFlags.Edge)) node.AddTextFlags(TextFlags.Edge);
        return node;
    }

    private static Vector4 Color(ByteColor color) => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
}

/// <summary>The Universalis lookup of <see cref="SellListAges"/>, kept out of its unsafe code (await is not allowed there).</summary>
internal static class SellListLookup
{
    /// <summary>Asks in the background; <paramref name="found"/> or <paramref name="failed"/> then runs on the game thread.</summary>
    public static void Start(string world, List<uint> items, string retainer, CancellationToken token,
        Action<Dictionary<uint, List<MarketListing>>> found, Action failed)
    {
        Task.Run(async () =>
        {
            try
            {
                var listings = await UniversalisService.GetRetainerListingsAsync(world, items, retainer, token).ConfigureAwait(false);
                await Svc.Framework.RunOnFrameworkThread(() => found(listings)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) Svc.Log.Warning($"Sell list ages: Universalis lookup failed: {ex.Message}");
                await Svc.Framework.RunOnFrameworkThread(failed).ConfigureAwait(false);
            }
        }, token);
    }
}
