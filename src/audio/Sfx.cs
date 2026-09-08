using System;
using System.Collections.Generic;
using Godot;

namespace DuelMasters.Gameplay.Audio;

/// <summary>The sound effects the arena plays for game events.</summary>
public enum SfxId
{
    Draw,
    Attack,
    ShieldBreak,
    Destroy,
    Summon,
    Cast,
    Tap,
    Mana,
    Turn,
    Win,
}

/// <summary>
/// Self-contained, procedurally synthesized sound effects: every clip is built as
/// raw 16-bit PCM at first use (no binary audio assets needed) and played through
/// a short-lived <see cref="AudioStreamPlayer"/> parented to the scene tree root,
/// which frees itself when the clip ends.
/// </summary>
public static class Sfx
{
    private const int SampleRate = 22050;
    private static readonly object Gate = new();
    private static readonly Dictionary<SfxId, AudioStreamWav> Cache = new();

    /// <summary>Play a synthesized clip once (no-op when the tree is not ready).</summary>
    public static void Play(SfxId id, float volumeDb = 0f)
    {
        var clip = Get(id);
        if (clip is null || Engine.GetMainLoop() is not SceneTree tree)
            return;
        var player = new AudioStreamPlayer { Stream = clip, VolumeDb = volumeDb };
        tree.Root.AddChild(player);
        player.Finished += player.QueueFree;
        player.Play();
    }

    private static AudioStreamWav? Get(SfxId id)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(id, out var cached))
                return cached;
            var built = Build(id);
            if (built is not null)
                Cache[id] = built;
            return built;
        }
    }

    // ------------------------------------------------------------------------ synthesis

    /// <summary>
    /// Renders <paramref name="seconds"/> of 16-bit mono PCM. <paramref name="sample"/>
    /// receives the local time (0..seconds) and a deterministic per-clip RNG so the
    /// noise floor is stable between runs.
    /// </summary>
    private static AudioStreamWav Synth(float seconds, Func<float, Random, float> sample, float master = 0.34f)
    {
        var count = (int)(seconds * SampleRate);
        var data = new byte[count * 2];
        var rnd = new Random(9001 + count);
        for (var i = 0; i < count; i++)
        {
            var t = i / (float)SampleRate;
            var s = Mathf.Clamp(sample(t, rnd) * master, -1f, 1f);
            var v = (short)(s * 32000f);
            data[i * 2] = (byte)(v & 0xFF);
            data[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = SampleRate,
            Stereo = false,
            Data = data,
        };
    }

    private static float Noise(Random rnd) => (float)(rnd.NextDouble() * 2.0 - 1.0);

    private static AudioStreamWav? Build(SfxId id) => id switch
    {
        // Deck <-> hand swoosh: a rising chirp over a soft noise swell.
        SfxId.Draw => Synth(0.22f, (t, rnd) =>
        {
            var chirp = Mathf.Sin(Mathf.Tau * (300f * t + 410f * t * t));
            var env = Mathf.Pow(1f - t, 2.2f);
            return (chirp * 0.7f + Noise(rnd) * 0.5f) * env;
        }),
        // Metallic "shing" as a weapon is declared.
        SfxId.Attack => Synth(0.16f, (t, rnd) =>
        {
            var f = 1500f * Mathf.Exp(-6f * t) + 220f;
            var saw = 2f * (f * t % 1f) - 1f;
            var env = Mathf.Exp(-7f * t);
            return (saw * 0.7f + Noise(rnd) * 0.5f) * env;
        }),
        // Shattering glass: noise crackle, high ring and a low slam.
        SfxId.ShieldBreak => Synth(0.5f, (t, rnd) =>
        {
            var ring = Mathf.Sin(Mathf.Tau * 2100f * t) * Mathf.Exp(-11f * t);
            var slam = Mathf.Sin(Mathf.Tau * 120f * t) * Mathf.Exp(-15f * t);
            var crackle = Noise(rnd) * Mathf.Exp(-9f * t);
            var attack = Mathf.Min(1f, t * 30f);
            return (crackle + ring * 0.55f + slam * 0.85f) * attack;
        }),
        // Falling "poof" for a destroyed creature.
        SfxId.Destroy => Synth(0.35f, (t, rnd) =>
        {
            var tone = Mathf.Sin(Mathf.Tau * (520f * t - 470f * t * t));
            var env = Mathf.Exp(-4.5f * t);
            return tone * env * 0.8f + Noise(rnd) * Mathf.Exp(-5.5f * t) * 0.55f;
        }),
        // Low thud for a creature entering the battle zone.
        SfxId.Summon => Synth(0.24f, (t, rnd) =>
        {
            var thud = Mathf.Sin(Mathf.Tau * 95f * t) * Mathf.Exp(-13f * t) * Mathf.Sin(Mathf.Pi * t);
            return thud * 1.15f + Noise(rnd) * Mathf.Exp(-26f * t) * 0.25f;
        }),
        // Rising whoosh for a spell.
        SfxId.Cast => Synth(0.30f, (t, rnd) =>
            Noise(rnd) * Mathf.Sin(Mathf.Pi * t) * (0.3f + 0.7f * t)),
        // Short control blip when a tap ability fires.
        SfxId.Tap => Synth(0.08f, (t, rnd) =>
            Mathf.Sign(Mathf.Sin(Mathf.Tau * 760f * t)) * Mathf.Exp(-30f * t) * 0.5f),
        // Soft descending mana deposit.
        SfxId.Mana => Synth(0.14f, (t, rnd) =>
            Mathf.Sin(Mathf.Tau * (240f * t - 350f * t * t)) * Mathf.Exp(-9f * t) * 0.7f),
        // Quiet tick as the turn passes.
        SfxId.Turn => Synth(0.10f, (t, rnd) =>
            Mathf.Sin(Mathf.Tau * 330f * t) * Mathf.Exp(-18f * t) * 0.6f),
        // Victory arpeggio: three bright notes, no time-dependent envelope bugs.
        SfxId.Win => Synth(0.78f, (t, rnd) =>
        {
            var slot = Mathf.Min(2, Mathf.FloorToInt(t / 0.26f));
            var ti = t - slot * 0.26f;
            var note = slot switch { 0 => 523.25f, 1 => 659.25f, _ => 783.99f };
            var env = Mathf.Clamp(ti * 14f, 0f, 1f) * Mathf.Pow(1f - ti / 0.26f, 1.6f);
            return Mathf.Sin(Mathf.Tau * note * t) * env * 0.8f;
        }),
        _ => null,
    };
}