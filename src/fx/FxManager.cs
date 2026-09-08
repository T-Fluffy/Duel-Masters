using System;
using Godot;

namespace DuelMasters.Gameplay.Fx;

/// <summary>
/// Mouse-transparent, full-rect overlay owned by an arena scene that hosts every
/// transient visual effect: screen flash, radial shockwaves, civilization attack
/// beams, one-shot burst particles, board shake and the glass-shatter that plays
/// when a shield breaks. Every spawn is transient and frees itself.
/// </summary>
public partial class FxManager : Control
{
    private static readonly Random Rng = new(1337);

    private static Shader? _shockShader;
    private static Shader? _dissolveShader;

    private ColorRect? _flash;
    private bool _shaking;
    private float _shakeTime;
    private float _shakeDuration;
    private float _shakePower;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        _flash = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _flash.Color = new Color(1f, 1f, 1f, 1f);
        _flash.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_flash);
    }

    public override void _Process(double delta)
    {
        if (!_shaking)
            return;
        var dt = (float)delta;
        if (_shakeTime <= 0f)
        {
            _shaking = false;
            _shakeTime = 0f;
            Position = Vector2.Zero;
            return;
        }
        _shakeTime -= dt;
        var amp = _shakePower * Mathf.Clamp(_shakeTime / _shakeDuration, 0f, 1f);
        Position = new Vector2(Noise01() * 2f - 1f, Noise01() * 2f - 1f) * amp;
    }

    private static float Noise01() => (float)Rng.NextDouble();

    // ------------------------------------------------------------------ primitives

    /// <summary>Decaying random position jitter on the whole overlay for ~0.35s.</summary>
    public void Shake(float power = 16f, float duration = 0.35f)
    {
        _shakePower = power;
        _shakeDuration = duration;
        _shakeTime = duration;
        _shaking = true;
    }

    /// <summary>A brief full-screen tint flare that eases out.</summary>
    public void Flash(Color color, float fadeOut = 0.4f, float peakAlpha = 0.5f)
    {
        if (_flash is null || _flash.IsQueuedForDeletion())
            return;
        _flash.Visible = true;
        _flash.Color = color;
        _flash.Modulate = new Color(1f, 1f, 1f, 0f);
        var tween = CreateTween();
        tween.TweenMethod(Callable.From<float>(a => _flash.Modulate = new Color(1f, 1f, 1f, a)), 0f, peakAlpha, 0.045d);
        tween.TweenMethod(Callable.From<float>(a => _flash.Modulate = new Color(1f, 1f, 1f, a)), peakAlpha, 0f, fadeOut);
        tween.Finished += () => _flash.Visible = false;
    }

    /// <summary>An expanding radial ring around <paramref name="atGlobal"/>.</summary>
    public void Shockwave(Vector2 atGlobal, Color color, float duration = 0.5f, float radius = 1.2f)
    {
        var size = GetViewportRect().Size;
        if (size.X <= 1f || size.Y <= 1f)
            return;
        var local = LocalPos(atGlobal);
        var rect = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore };
        rect.Position = Vector2.Zero;
        rect.Size = size;
        rect.Material = new ShaderMaterial { Shader = LoadShockShader() };
        AddChild(rect);

        var mat = (ShaderMaterial)rect.Material;
        mat.SetShaderParameter("u_center", new Vector2(
            Mathf.Clamp(local.X / size.X, 0f, 1f),
            Mathf.Clamp(local.Y / size.Y, 0f, 1f)));
        mat.SetShaderParameter("u_color", color);
        mat.SetShaderParameter("u_radius", radius);

        var tween = CreateTween();
        tween.TweenMethod(Callable.From<float>(p => mat.SetShaderParameter("u_progress", p)), 0f, 1f, duration);
        tween.Finished += rect.QueueFree;
    }

    /// <summary>One-shot burst of a few dozen particles at a point.</summary>
    public void Burst(Vector2 atGlobal, Color color, int count = 26, float lifetime = 0.7f)
    {
        var particles = new CpuParticles2D
        {
            OneShot = true,
            Explosiveness = 1f,
            Amount = count,
            Lifetime = lifetime,
            Direction = new Vector2(0f, -1f),
            Spread = 180f,
            InitialVelocityMin = 120f,
            InitialVelocityMax = 360f,
            Gravity = new Vector2(0f, 480f),
            ScaleAmountMin = 2.4f,
            ScaleAmountMax = 5.2f,
            Color = color,
        };
        particles.Position = LocalPos(atGlobal);
        AddChild(particles);
        particles.Restart();
        particles.Finished += particles.QueueFree;
    }

    /// <summary>
    /// A tapering civilization-colored beam from <paramref name="fromGlobal"/> to
    /// <paramref name="toGlobal"/> that contracts onto the target as it fades.
    /// </summary>
    public void Beam(Vector2 fromGlobal, Vector2 toGlobal, Color color, float glowWidth = 18f)
    {
        var from = LocalPos(fromGlobal);
        var to = LocalPos(toGlobal);
        if (from.DistanceTo(to) < 1f)
            return;

        var glowColor = Glow(color);
        var coreColor = Bright(color, 0.55f);

        var glow = new Line2D
        {
            Points = new[] { from, to },
            Width = glowWidth,
            DefaultColor = glowColor,
            Antialiased = false,
        };
        var core = new Line2D
        {
            Points = new[] { from, to },
            Width = Mathf.Max(2.5f, glowWidth * 0.3f),
            DefaultColor = coreColor,
            Antialiased = true,
        };
        AddChild(glow);
        AddChild(core);

        var tween = CreateTween();
        tween.Parallel().TweenMethod(Callable.From<float>(k =>
        {
            var tip = from.Lerp(to, k);
            glow.Points = new[] { tip, to };
            core.Points = new[] { tip, to };
        }), 0f, 1f, 0.30d);
        tween.Parallel().TweenMethod(Callable.From<float>(a =>
        {
            glow.DefaultColor = WithAlpha(glowColor, a * glowColor.A);
            core.DefaultColor = WithAlpha(coreColor, a * coreColor.A);
        }), 1f, 0f, 0.30d);
        tween.Finished += () =>
        {
            glow.QueueFree();
            core.QueueFree();
        };
    }

    // ------------------------------------------------------------------ composites

    /// <summary>Small "something happened here" pulse (summons, casts, tap abilities).</summary>
    public void Ping(Vector2 atGlobal, Color color, float radius = 0.45f)
    {
        Shockwave(atGlobal, WithAlpha(color, 0.8f), 0.34f, radius);
        Burst(atGlobal, color, 14, 0.5f);
    }

    /// <summary>The full shield-break beat: shatter shard, ring, sparks, flash and shake.</summary>
    public void ShieldShatter(Vector2 atGlobal, Color tint)
    {
        Shockwave(atGlobal, tint, 0.5f, 0.9f);
        Burst(atGlobal, tint, 34, 0.85f);
        Burst(atGlobal, new Color(1f, 1f, 1f, 0.9f), 16, 0.5f);
        Flash(new Color(1f, 0.92f, 0.55f, 1f), 0.35f, 0.26f);
        Shake(15f, 0.4f);
        SpawnGlassShard(atGlobal, tint);
    }

    /// <summary>Warm golden flash + sparks for a match-winner.</summary>
    public void BigWin(Color? tint = null)
    {
        var gold = tint ?? new Color("ffd25a");
        var center = GetViewportRect().Size * new Vector2(0.5f, 0.42f);
        Flash(new Color(1f, 1f, 1f, 1f), 1.0f, 0.8f);
        Shake(10f, 0.5f);
        Burst(GlobalPos(center), gold, 42, 1.2f);
        Burst(GlobalPos(center), new Color(1f, 1f, 1f, 1f), 20, 0.8f);
    }

    // ------------------------------------------------------------------ internals

    private Vector2 LocalPos(Vector2 global) => GetGlobalTransform().AffineInverse() * global;

    private Vector2 GlobalPos(Vector2 local) => GetGlobalTransform() * local;

    private static Shader LoadShockShader() => _shockShader ??= GD.Load<Shader>("res://src/fx/shockwave.gdshader");
    private static Shader LoadDissolveShader() => _dissolveShader ??= GD.Load<Shader>("res://src/fx/card_dissolve.gdshader");

    private void SpawnGlassShard(Vector2 atGlobal, Color tint)
    {
        var size = new Vector2(96f, 132f);
        var shard = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore };
        shard.Color = tint;
        shard.Size = size;
        shard.Position = LocalPos(atGlobal) - size * 0.5f;
        shard.Rotation = (Noise01() * 2f - 1f) * 0.12f;
        shard.Material = new ShaderMaterial { Shader = LoadDissolveShader() };
        ((ShaderMaterial)shard.Material).SetShaderParameter("u_tint", tint);
        AddChild(shard);

        var tween = CreateTween();
        tween.Parallel().TweenMethod(
            Callable.From<float>(p => ((ShaderMaterial)shard.Material).SetShaderParameter("u_progress", p)),
            0f, 1f, 0.85d);
        tween.Parallel().TweenProperty(shard, "position:y", shard.Position.Y - 16f, 0.85d);
        tween.Parallel().TweenProperty(shard, "scale", Vector2.One * 1.07f, 0.85d);
        tween.Finished += shard.QueueFree;
    }

    private static Color Glow(Color c) => new(c.R, c.G, c.B, 0.26f);

    private static Color Bright(Color c, float amount) => new(
        Mathf.Clamp(c.R + amount, 0f, 1f),
        Mathf.Clamp(c.G + amount, 0f, 1f),
        Mathf.Clamp(c.B + amount, 0f, 1f),
        Mathf.Clamp(c.A * 1.2f, 0f, 1f));

    private static Color WithAlpha(Color c, float a) => new(c.R, c.G, c.B, a);
}
