using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;

namespace GoblinTweaks.Glamour.Game;

/// <summary>
/// Optional link to the Glamourer plugin, through its public API. With it an outfit can be worn on the player's own
/// character (only they see it, like any Glamourer design) and saved as a Glamourer design. Before wearing, the
/// player's Glamourer state is kept, and "back to my clothes" applies it again, so changes they had made in
/// Glamourer are not lost. Every call is guarded: if Glamourer is missing, too old or fails, it is simply not
/// available. Framework thread only.
/// </summary>
internal sealed class GlamourerBridge
{
    /// <summary>Identifies the changes made by GoblinTweaks to Glamourer.</summary>
    private const uint Key = 0x47544B47;   // "GTKG"

    /// <summary>The Glamourer API this was written against: major 1, minor 8 or later.</summary>
    private const int Major = 1, MinMinor = 8;

    private static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(3);

    private readonly ApiVersion _version = new(Svc.PluginInterface);
    private readonly SetItem _setItem = new(Svc.PluginInterface);
    private readonly SetBonusItem _setBonusItem = new(Svc.PluginInterface);
    private readonly GetStateBase64 _getState = new(Svc.PluginInterface);
    private readonly ApplyState _applyState = new(Svc.PluginInterface);
    private readonly RevertToAutomation _revertToAutomation = new(Svc.PluginInterface);
    private readonly AddDesign _addDesign = new(Svc.PluginInterface);

    private bool _available;
    private DateTime _nextCheck = DateTime.MinValue;
    private bool _loggedFailure;

    // The player's Glamourer state before the first outfit was worn; null while nothing is worn.
    private string? _before;

    /// <summary>Glamourer is loaded and its API is one this understands. Checked again every few seconds.</summary>
    public bool Available
    {
        get
        {
            if (DateTime.UtcNow < _nextCheck) return _available;
            _nextCheck = DateTime.UtcNow + RecheckEvery;
            try
            {
                var (major, minor) = _version.Invoke();
                _available = major == Major && minor >= MinMinor;
            }
            catch
            {
                _available = false;
            }
            if (!_available) _before = null;   // whatever was worn went away with Glamourer
            return _available;
        }
    }

    /// <summary>An outfit is being worn through Glamourer.</summary>
    public bool Wearing => _before is not null;

    /// <summary>Puts the outfit on the player's character. True when every piece was accepted.</summary>
    public bool Wear(IReadOnlyDictionary<GlamourSlot, OutfitPiece> pieces)
    {
        if (!Available) return false;
        try
        {
            if (_before is null)
            {
                var (code, state) = _getState.Invoke(0, Key);
                if (code != GlamourerApiEc.Success || state is null) return Fail($"reading the state answered {code}");
                _before = state;
            }

            var ok = true;
            foreach (var (slot, piece) in pieces)
            {
                if (piece.ItemId == 0) continue;
                GlamourerApiEc code;
                if (GlamourIds.IsFacewear(piece.ItemId))
                    code = _setBonusItem.Invoke(0, ApiBonusSlot.Glasses, piece.ItemId - GlamourIds.FacewearBase, Key);
                else if (Slot(slot) is { } apiSlot)
                    code = _setItem.Invoke(0, apiSlot, piece.ItemId, [piece.Stain0, piece.Stain1], Key);
                else
                    continue;
                ok &= code == GlamourerApiEc.Success;
            }
            return ok;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>Back to what the player wore before (their Glamourer state as it was). Does nothing if nothing is worn.</summary>
    public void Revert()
    {
        if (_before is not { } before) return;
        _before = null;
        try
        {
            var code = _applyState.Invoke(before, 0, Key, ApplyFlag.Equipment);
            if (code != GlamourerApiEc.Success)
                _revertToAutomation.Invoke(0, Key);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>
    /// Saves the outfit as a new Glamourer design named <paramref name="name"/>: it is worn for a moment, the
    /// resulting look is saved, and the player goes back to what they wore unless an outfit was already worn.
    /// </summary>
    public bool SaveDesign(string name, IReadOnlyDictionary<GlamourSlot, OutfitPiece> pieces)
    {
        var wasWearing = Wearing;
        if (!Wear(pieces)) return false;
        try
        {
            var (code, state) = _getState.Invoke(0, Key);
            if (code != GlamourerApiEc.Success || state is null) return Fail($"reading the state answered {code}");
            var added = _addDesign.Invoke(state, name, out _);
            return added == GlamourerApiEc.Success || Fail($"saving the design answered {added}");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            if (!wasWearing) Revert();
        }
    }

    private static ApiEquipSlot? Slot(GlamourSlot slot) => slot switch
    {
        GlamourSlot.MainHand => ApiEquipSlot.MainHand,
        GlamourSlot.OffHand  => ApiEquipSlot.OffHand,
        GlamourSlot.Head     => ApiEquipSlot.Head,
        GlamourSlot.Body     => ApiEquipSlot.Body,
        GlamourSlot.Hands    => ApiEquipSlot.Hands,
        GlamourSlot.Legs     => ApiEquipSlot.Legs,
        GlamourSlot.Feet     => ApiEquipSlot.Feet,
        GlamourSlot.Ears     => ApiEquipSlot.Ears,
        GlamourSlot.Neck     => ApiEquipSlot.Neck,
        GlamourSlot.Wrists   => ApiEquipSlot.Wrists,
        GlamourSlot.Ring     => ApiEquipSlot.RFinger,
        _                    => null,
    };

    private bool Fail(string what)
    {
        if (!_loggedFailure)
        {
            _loggedFailure = true;
            Svc.Log.Warning($"GoblinGlamour: Glamourer call failed ({what}).");
        }
        return false;
    }
}
