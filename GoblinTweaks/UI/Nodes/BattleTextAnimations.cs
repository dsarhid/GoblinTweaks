using System.Numerics;
using Animation = GoblinTweaks.Tweaks.GoblinBattleText.BattleTextAnimation;

namespace GoblinTweaks.UI.Nodes;

/// <summary>What an animation does to a message at one moment, on top of its normal place and fade.</summary>
internal struct BattleTextEffect()
{
    /// <summary>Pixels the message is moved by.</summary>
    public Vector2 Offset = Vector2.Zero;

    /// <summary>Horizontal and vertical size, 1 being its own.</summary>
    public Vector2 Scale = Vector2.One;

    public float Degrees = 0f;

    /// <summary>Multiplies the opacity of the message.</summary>
    public float Alpha = 1f;

    /// <summary>Light added to the message (red, green, blue); negative darkens it.</summary>
    public Vector3 Glow = Vector3.Zero;
}

/// <summary>
/// The animations of highlighted messages. None of them is a game asset: each one is a formula that
/// says where the message is, how large, how tilted and how bright, a given time after it appeared.
/// </summary>
internal static class BattleTextAnimations
{
    private static readonly Vector3 White   = new(1f, 1f, 1f);
    private static readonly Vector3 Fire    = new(1f, 0.85f, 0.5f);
    private static readonly Vector3 Ember   = new(1f, 0.45f, 0.1f);
    private static readonly Vector3 Rage    = new(1f, 0.3f, 0.1f);
    private static readonly Vector3 Storm   = new(0.9f, 0.95f, 1f);
    private static readonly Vector3 Ice     = new(0.45f, 0.8f, 1f);
    private static readonly Vector3 Poison  = new(0.2f, 0.9f, 0.25f);
    private static readonly Vector3 Crimson = new(0.9f, 0f, 0f);
    private static readonly Vector3 Holy    = new(1f, 0.95f, 0.7f);

    /// <param name="time">Seconds since the message appeared.</param>
    /// <param name="strength">The intensity chosen by the user, 1 being the normal one. It scales how far the animation goes, not how long it lasts.</param>
    public static BattleTextEffect Play(Animation animation, float time, float strength)
    {
        var effect = new BattleTextEffect();
        var t = time;
        var s = strength;
        var capped = Math.Min(1f, s); // for what cannot go past "all of it": shrinking to nothing, going fully dark

        switch (animation)
        {
            // ── Gentle ──────────────────────────────────────────────────────────

            case Animation.Pop when t < 0.18f:
                effect.Scale = Uniform(1f + 0.6f * s * (1f - t / 0.18f));
                break;

            case Animation.Shake when t < 0.45f:
                effect.Offset.X = MathF.Sin(t * 70f) * 5f * s * (1f - t / 0.45f);
                break;

            case Animation.Pulse when t < 1.2f:
                effect.Scale = Uniform(1f + 0.18f * s * MathF.Abs(MathF.Sin(t * MathF.PI / 0.3f)));
                break;

            case Animation.Flash when t < 0.6f:
                effect.Alpha = 1f - Math.Min(1f, 0.65f * s) * (1f - MathF.Abs(MathF.Cos(t * MathF.PI / 0.15f)));
                break;

            // ── Impacts ─────────────────────────────────────────────────────────

            // Comes down from huge, half transparent, and the impact leaves a tremor.
            case Animation.Slam when t < 0.16f:
            {
                var fall = t / 0.16f;
                effect.Scale = Uniform(1f + 1.8f * s * (1f - fall) * (1f - fall));
                effect.Alpha = 0.4f + 0.6f * fall;
                break;
            }
            case Animation.Slam when t < 0.5f:
                effect.Offset = Tremor(t, 4f * s * (1f - (t - 0.16f) / 0.34f));
                break;

            case Animation.Quake when t < 0.6f:
                effect.Offset = Tremor(t, 6f * s * (1f - t / 0.6f));
                break;

            // A long tremor that also rocks the message.
            case Animation.Earthquake when t < 1.1f:
            {
                var left = 1f - t / 1.1f;
                effect.Offset  = Tremor(t, 7f * s * left);
                effect.Degrees = MathF.Sin(t * 55f) * 3f * s * left;
                break;
            }

            // Lands flattened and wide, and springs back.
            case Animation.Stomp when t < 0.5f:
            {
                var squash = s * Spring(t, 24f, 7f);
                effect.Scale = new Vector2(1f + 0.5f * squash, Math.Max(0.2f, 1f - 0.5f * squash));
                break;
            }

            // Hammered flat, then it springs back up.
            case Animation.Crush when t < 0.12f:
            {
                var press = t / 0.12f;
                effect.Scale = new Vector2(1f + 0.4f * s * press, Math.Max(0.2f, 1f - 0.7f * capped * press));
                break;
            }
            case Animation.Crush when t < 0.6f:
            {
                var spring = Spring(t - 0.12f, 26f, 8f);
                effect.Scale = new Vector2(1f + 0.4f * s * spring, Math.Max(0.2f, 1f - 0.7f * capped * spring));
                break;
            }

            // A flat, wide blast of light that snaps back, with an aftershock.
            case Animation.Shockwave when t < 0.35f:
            {
                var blast = s * (1f - t / 0.35f) * (1f - t / 0.35f);
                effect.Scale = new Vector2(1f + 1.5f * blast, Math.Max(0.25f, 1f - 0.5f * blast));
                effect.Glow  = Light(White, 0.5f * blast);
                break;
            }
            case Animation.Shockwave when t < 0.6f:
                effect.Offset = Tremor(t, 2f * s * (1f - (t - 0.35f) / 0.25f));
                break;

            // Streaks in from the upper right, burning, and hits.
            case Animation.Meteor when t < 0.2f:
            {
                var far = (1f - t / 0.2f) * (1f - t / 0.2f);
                effect.Offset = new Vector2(70f * s * far, -70f * s * far);
                effect.Scale  = Uniform(1f + 1.2f * s * far);
                effect.Glow   = Light(Fire, 0.9f * s * (1f - t / 0.2f));
                break;
            }
            case Animation.Meteor when t < 0.55f:
            {
                var left = 1f - (t - 0.2f) / 0.35f;
                effect.Offset = Tremor(t, 5f * s * left);
                effect.Glow   = Light(Fire, 0.4f * s * left);
                break;
            }

            // Slam, tremor and a red glow that throbs: everything at once.
            case Animation.Fury when t < 0.16f:
            {
                var fall = t / 0.16f;
                effect.Scale = Uniform(1f + 1.8f * s * (1f - fall) * (1f - fall));
                effect.Glow  = Light(Rage, 0.8f * s);
                break;
            }
            case Animation.Fury when t < 0.8f:
            {
                var left = 1f - (t - 0.16f) / 0.64f;
                effect.Offset = Tremor(t, 6f * s * left);
                effect.Scale  = Uniform(1f + 0.2f * s * left * MathF.Abs(MathF.Sin(t * 40f)));
                effect.Glow   = Light(Rage, 0.8f * s * left);
                break;
            }

            // ── Size ────────────────────────────────────────────────────────────

            // A spring: overshoots its size and settles.
            case Animation.Bounce when t < 0.7f:
                effect.Scale = Uniform(Math.Max(0.2f, 1f + 0.7f * s * Spring(t, 22f, 6f)));
                break;

            // Starts tiny, shoots past its size and settles.
            case Animation.Zoom when t < 0.5f:
                effect.Scale = Uniform(Math.Max(0.1f, 1f - 0.8f * capped * MathF.Exp(-10f * t) * MathF.Cos(t * 16f)));
                break;

            // Pulled wide and thin, and snaps back.
            case Animation.Stretch when t < 0.3f:
            {
                var pull = s * (1f - t / 0.3f) * (1f - t / 0.3f);
                effect.Scale = new Vector2(1f + 1.2f * pull, Math.Max(0.3f, 1f - 0.3f * pull));
                break;
            }

            // Swells to a bright peak and comes back.
            case Animation.Explode when t < 0.4f:
            {
                var burst = MathF.Sin(MathF.PI * t / 0.4f);
                effect.Scale = Uniform(1f + 1.1f * s * burst);
                effect.Glow  = Light(White, 0.7f * s * burst);
                break;
            }

            // Two quick beats.
            case Animation.Heartbeat when t < 0.5f:
                effect.Scale = Uniform(1f + 0.35f * s * (Beat(t) + Beat(t - 0.28f)));
                break;

            // ── Rotation ────────────────────────────────────────────────────────

            case Animation.Swing when t < 0.7f:
                effect.Degrees = 14f * s * Spring(t, 20f, 5f);
                break;

            // A fast, small rattle around its centre.
            case Animation.Rattle when t < 0.5f:
                effect.Degrees = MathF.Sin(t * 90f) * 6f * s * (1f - t / 0.5f);
                break;

            // One full turn, shrinking to its size.
            case Animation.Spin when t < 0.5f:
            {
                var left = 1f - EaseOut(t / 0.5f);
                effect.Degrees = 360f * left;
                effect.Scale   = Uniform(1f + 0.4f * s * left);
                break;
            }

            // Two turns while it grows from almost nothing.
            case Animation.Tornado when t < 0.6f:
            {
                var left = 1f - EaseOut(t / 0.6f);
                effect.Degrees = 720f * left;
                effect.Scale   = Uniform(Math.Max(0.1f, 1f - 0.8f * capped * left));
                break;
            }

            // ── Movement ────────────────────────────────────────────────────────

            // Falls from above and bounces on its place.
            case Animation.Drop when t < 0.6f:
                effect.Offset.Y = -40f * s * MathF.Exp(-6f * t) * MathF.Abs(MathF.Cos(t * 14f));
                break;

            // Shoots up from below, past its place, and settles.
            case Animation.Launch when t < 0.5f:
                effect.Offset.Y = 40f * s * MathF.Exp(-7f * t) * MathF.Cos(t * 12f);
                break;

            // Charges in from the side.
            case Animation.Dash when t < 0.45f:
                effect.Offset.X = -90f * s * MathF.Exp(-9f * t) * MathF.Cos(t * 13f);
                effect.Alpha    = Math.Min(1f, 0.3f + t / 0.1f);
                break;

            // Kicked back, tilting, and returns.
            case Animation.Recoil when t < 0.4f:
            {
                var kick = MathF.Sin(MathF.PI * t / 0.4f) * (1f - t / 0.4f);
                effect.Offset.X = 18f * s * kick;
                effect.Degrees  = -5f * s * kick;
                break;
            }

            // ── Light ───────────────────────────────────────────────────────────

            // Appears white-hot, slightly larger, and cools down.
            case Animation.Blaze when t < 0.5f:
            {
                var heat = 1f - t / 0.5f;
                effect.Glow  = Light(Fire, 0.9f * s * heat);
                effect.Scale = Uniform(1f + 0.25f * s * heat);
                break;
            }

            // Flickers bright while it jitters.
            case Animation.Thunder when t < 0.5f:
            {
                var charge = 1f - t / 0.5f;
                if (MathF.Sin(t * 60f) > 0f)
                    effect.Glow = Light(Storm, 0.8f * s * charge);
                effect.Offset.X = MathF.Sin(t * 130f) * 3f * s * charge;
                break;
            }

            // Fades in dim, flares up and settles.
            case Animation.Ignite when t < 0.6f:
                effect.Glow  = Light(Fire, s * MathF.Sin(MathF.PI * t / 0.6f));
                effect.Alpha = 0.3f + 0.7f * Math.Min(1f, t / 0.18f);
                break;

            // A warm glow that throbs and dies out.
            case Animation.Ember when t < 1.4f:
                effect.Glow = Light(Ember, 0.6f * s * MathF.Abs(MathF.Sin(MathF.PI * t / 0.35f)) * (1f - t / 1.4f));
                break;

            // A cold flash that contracts to its size.
            case Animation.Frost when t < 0.7f:
            {
                var cold = 1f - t / 0.7f;
                effect.Glow  = Light(Ice, 0.8f * s * cold);
                effect.Scale = Uniform(1f + 0.2f * s * cold);
                break;
            }

            // A green glow that throbs while it sways.
            case Animation.Venom when t < 1.2f:
            {
                var left = 1f - t / 1.2f;
                effect.Glow     = Light(Poison, 0.6f * s * MathF.Abs(MathF.Sin(MathF.PI * t / 0.3f)) * left);
                effect.Offset.X = MathF.Sin(t * 9f) * 2f * s * left;
                break;
            }

            // A red flash that sinks a little, like a drip.
            case Animation.Blood when t < 0.6f:
                effect.Glow     = Light(Crimson, 0.8f * s * (1f - t / 0.6f));
                effect.Offset.Y = 8f * s * MathF.Sin(MathF.PI * t / 0.6f);
                break;

            // A slow golden swell.
            case Animation.Radiance when t < 1f:
            {
                var shine = MathF.Sin(MathF.PI * t);
                effect.Glow  = Light(Holy, 0.7f * s * shine);
                effect.Scale = Uniform(1f + 0.15f * s * shine);
                break;
            }

            // Looms in large and dark, out of nothing.
            case Animation.Shadow when t < 0.6f:
            {
                var near = t / 0.6f;
                effect.Alpha = near;
                effect.Scale = Uniform(1f + 0.8f * s * (1f - near));
                effect.Glow  = White * (-0.4f * capped * (1f - near));
                break;
            }

            // Hard on and off, like a strobe light.
            case Animation.Strobe when t < 0.6f:
                effect.Alpha = MathF.Sin(t * 50f) > 0f ? 1f : 1f - capped;
                break;
        }

        return effect;
    }

    private static Vector2 Uniform(float scale) => new(scale, scale);

    /// <summary>A color of light at a given strength, never past full white.</summary>
    private static Vector3 Light(Vector3 color, float amount)
        => Vector3.Clamp(color * amount, Vector3.Zero, Vector3.One);

    /// <summary>A fast, irregular shake in both directions, <paramref name="size"/> pixels wide.</summary>
    private static Vector2 Tremor(float time, float size)
        => new(MathF.Sin(time * 95f) * size, MathF.Cos(time * 80f) * size * 0.8f);

    /// <summary>Starts at 1 and swings around 0, less each time.</summary>
    private static float Spring(float time, float speed, float damping)
        => MathF.Exp(-damping * time) * MathF.Cos(time * speed);

    private static float EaseOut(float progress) => 1f - (1f - progress) * (1f - progress);

    /// <summary>One beat of a heart: up and back down in 0.18 seconds.</summary>
    private static float Beat(float time)
        => time is >= 0f and < 0.18f ? MathF.Sin(MathF.PI * time / 0.18f) : 0f;
}
