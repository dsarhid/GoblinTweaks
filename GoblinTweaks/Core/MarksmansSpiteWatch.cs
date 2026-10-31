using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace GoblinTweaks.Core;

/// <summary>
/// PvP: whether an enemy machinist's Marksman's Spite is on its way to you. Each tweak that wants to know keeps
/// one, feeds it from its own hook of action effects and asks it once a frame.
/// </summary>
/// <remarks>
/// Marksman's Spite is instant: there is no cast bar, only the kneeling animation before it hits. Two signs
/// are watched, and the log says which one came first so the slower one can be dropped once seen in a match:
/// the machinist casting it at you (in case the game keeps it as a cast) and the action reaching your client
/// (its effect lands after the animation).
/// </remarks>
internal sealed unsafe class MarksmansSpiteWatch(string owner)
{
    public const uint ActionId = 29415;
    private const byte ActionKindAction = 1;

    // How long the danger lasts after the action reaches you; it hits about 1.5 to 2 s later.
    private static readonly TimeSpan ImpactWindow = TimeSpan.FromSeconds(2.5);

    private DateTime _incomingUntil = DateTime.MinValue;
    private bool _castSeen;
    private bool _wasIncoming;

    /// <summary>Whether one is on its way to you, as of the last <see cref="Update"/>.</summary>
    public bool Incoming { get; private set; }

    /// <summary>True for the one frame the danger starts, to warn once per Limit Break.</summary>
    public bool JustStarted => Incoming && !_wasIncoming;

    public void Reset()
    {
        _incomingUntil = DateTime.MinValue;
        _castSeen = false;
        _wasIncoming = false;
        Incoming = false;
    }

    /// <summary>Works out <see cref="Incoming"/>. Call once a frame, on the game thread.</summary>
    public void Update()
    {
        _wasIncoming = Incoming;

        var player = (BattleChara*)Control.GetLocalPlayer();
        if (!Svc.ClientState.IsPvP || player == null || player->Health == 0)
        {
            _incomingUntil = DateTime.MinValue;
            _castSeen = false;
            Incoming = false;
            return;
        }

        var aimed = AimedAt(player->EntityId);
        if (aimed != null && !_castSeen)
            Svc.Log.Info($"{owner}: {aimed} is casting Marksman's Spite at you (cast sign).");
        _castSeen = aimed != null;

        Incoming = _castSeen || DateTime.UtcNow < _incomingUntil;
    }

    /// <summary>
    /// An action resolved by the server reaching the client: pass on what the hook of action effects receives.
    /// Runs inside the game's own code; the caller catches.
    /// </summary>
    public void OnActionEffect(uint casterEntityId, Character* caster, ActionEffectHandler.Header* header, GameObjectId* targetEntityIds)
    {
        if (header == null || targetEntityIds == null || header->ActionType != ActionKindAction || header->ActionId != ActionId) return;

        var player = Control.GetLocalPlayer();
        if (player == null || casterEntityId == player->EntityId) return;

        for (var target = 0; target < header->NumTargets; target++)
        {
            if (targetEntityIds[target].ObjectId != player->EntityId) continue;

            _incomingUntil = DateTime.UtcNow + ImpactWindow;
            var name = caster != null ? caster->NameString : casterEntityId.ToString("X");
            Svc.Log.Info($"{owner}: Marksman's Spite from {name} reached your client (effect sign); cast sign seen first: {_castSeen}.");
            return;
        }
    }

    /// <summary>Name of the player casting Marksman's Spite at <paramref name="playerId"/>, if any.</summary>
    private static string? AimedAt(uint playerId)
    {
        var manager = GameObjectManager.Instance();
        if (manager == null) return null;

        foreach (var entry in manager->Objects.IndexSorted)
        {
            var obj = entry.Value;
            if (obj == null || obj->ObjectKind != ObjectKind.Pc || obj->EntityId == playerId) continue;

            var cast = ((BattleChara*)obj)->GetCastInfo();
            if (cast == null || !cast->IsCasting || cast->ActionId != ActionId) continue;
            if (cast->TargetId.ObjectId == playerId)
                return ((Character*)obj)->NameString;
        }

        return null;
    }
}
