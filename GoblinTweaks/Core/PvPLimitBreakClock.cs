using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace GoblinTweaks.Core;

/// <summary>
/// PvP: how far an enemy's Limit Break has charged. The game does not show the gauge of other players, but in PvP it
/// fills by itself over time, in the "Gauge Charge Time" each Limit Break's description gives, starting with the
/// match: your own gauge says when the match started, and how fast it fills is the fallback for a Limit Break whose
/// description gives no time.
/// </summary>
internal sealed unsafe class PvPLimitBreakClock(string owner)
{
    // A speed is measured over at least this long, so one frame's rounding does not count.
    private static readonly TimeSpan SampleSpan = TimeSpan.FromSeconds(3);

    private int _lastUnits = -1;
    private DateTime _sampleAt;
    private int _sampleUnits;
    private bool _usedOwn;
    private bool _speedLogged;

    /// <summary>Units of gauge per second, while it fills by itself; 0 until measured.</summary>
    public float UnitsPerSecond { get; private set; }

    /// <summary>Units of a full gauge.</summary>
    public int FullUnits { get; private set; }

    /// <summary>When every gauge in this match started filling; null when not known.</summary>
    public DateTime? MatchFillStart { get; private set; }

    /// <summary>Forgets the match: a new zone is a new match.</summary>
    public void Reset()
    {
        _lastUnits = -1;
        _usedOwn = false;
        _speedLogged = false;
        MatchFillStart = null;
    }

    /// <summary>Seconds a gauge takes to fill: measured, or <paramref name="fallback"/> when it can't be.</summary>
    public float ChargeSeconds(float fallback) => UnitsPerSecond > 0f && FullUnits > 0 ? FullUnits / UnitsPerSecond : fallback;

    /// <summary>
    /// How far a Limit Break that takes <paramref name="chargeSeconds"/> to charge has charged, 0 to 1: since it was
    /// last used, or since the match started.
    /// </summary>
    public float Progress(DateTime? usedAt, DateTime now, float chargeSeconds)
    {
        var since = usedAt ?? MatchFillStart;
        if (since is not { } start || chargeSeconds <= 0f) return 1f;

        return Math.Clamp((float)(now - start).TotalSeconds / chargeSeconds, 0f, 1f);
    }

    /// <summary>Reads your gauge. Call once a frame, on the game thread, while in PvP.</summary>
    public void Update(DateTime now)
    {
        var gauge = LimitBreakController.Instance();
        if (gauge == null || !gauge->IsPvP || gauge->BarUnits == 0) return;

        FullUnits = gauge->BarUnits * Math.Max(1, (int)gauge->BarCount);
        var units = (int)gauge->CurrentUnits;

        if (_lastUnits < 0)
        {
            // First look in this match: a full gauge means everyone's is (or the gauge does not move here).
            if (units >= FullUnits) MatchFillStart = null;
            else if (units == 0) MatchFillStart = now;
            else if (UnitsPerSecond > 0f) MatchFillStart = now - TimeSpan.FromSeconds(units / UnitsPerSecond);
            StartSample(units, now);
        }
        else if (units < _lastUnits)
        {
            // You used yours: it starts again, but the others' clock goes on.
            _usedOwn = true;
            StartSample(units, now);
        }
        else if (units > _lastUnits && _lastUnits == 0 && MatchFillStart == null && !_usedOwn)
        {
            MatchFillStart = now;
            StartSample(units, now);
        }
        else if (units >= FullUnits)
        {
            StartSample(units, now);
        }
        else if (now - _sampleAt >= SampleSpan && units > _sampleUnits)
        {
            var speed = (units - _sampleUnits) / (float)(now - _sampleAt).TotalSeconds;
            UnitsPerSecond = UnitsPerSecond <= 0f ? speed : UnitsPerSecond * 0.7f + speed * 0.3f;
            StartSample(units, now);

            if (!_speedLogged)
            {
                _speedLogged = true;
                Svc.Log.Info($"{owner}: your Limit Break gauge fills at {UnitsPerSecond:0.0} units/s: about {FullUnits / UnitsPerSecond:0} s to full.");
            }
        }

        _lastUnits = units;
    }

    private void StartSample(int units, DateTime now)
    {
        _sampleUnits = units;
        _sampleAt = now;
    }
}
