using System;
using Godot;

namespace DuelMasters.Core;

/// <summary>
/// Central persisted prefs service (mirrors <see cref="DisplaySettings"/>). Owns
/// simple boolean toggles players can flip at runtime and that survive restart,
/// persisted to <c>user://game_settings.cfg</c>.
/// </summary>
public static class GameSettings
{
    private const string SettingsPath = "user://game_settings.cfg";
    private const string Section = "gameplay";

    private static bool? _revealAiHand;
    private static float? _cardSizeMultiplier;

    /// <summary>Raised whenever <see cref="SetRevealAiHand"/> changes the flag.</summary>
    public static event Action? RevealAiHandChanged;

    /// <summary>Raised whenever <see cref="SetCardSizeMultiplier"/> changes the scale.</summary>
    public static event Action? CardSizeMultiplierChanged;

    /// <summary>
    /// Reveal the opponent's hand as card fronts instead of face-down backs.
    /// Debug/development aid to inspect what the AI is holding; default off.
    /// </summary>
    public static bool RevealAiHand
    {
        get
        {
            if (_revealAiHand is null)
                _revealAiHand = ReadBool("reveal_ai_hand", false);
            return _revealAiHand.Value;
        }
    }

    /// <summary>Set and persist the reveal-opponent-hand flag.</summary>
    public static void SetRevealAiHand(bool value)
    {
        if (_revealAiHand == value)
            return;
        _revealAiHand = value;
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath);
        cfg.SetValue(Section, "reveal_ai_hand", value);
        var error = cfg.Save(SettingsPath);
        if (error != Error.Ok)
            GD.PushWarning($"[GameSettings] Could not save settings: {error}");
        RevealAiHandChanged?.Invoke();
    }

    /// <summary>Min / max / default for the card size scale control.</summary>
    public const float CardSizeMin = 0.6f;
    public const float CardSizeMax = 1.7f;
    public const float CardSizeDefault = 1.0f;

    /// <summary>
    /// User-facing card size scale applied on top of the auto-computed board size.
    /// Lower makes every zone card smaller (more of the table visible), higher makes
    /// them larger for easier reading. Persisted as a multiplier in the 0.6-1.7 range.
    /// </summary>
    public static float CardSizeMultiplier
    {
        get
        {
            if (_cardSizeMultiplier is null)
                _cardSizeMultiplier = ReadFloat("card_size_multiplier", CardSizeDefault);
            return _cardSizeMultiplier.Value;
        }
    }

    /// <summary>Set, clamp and persist the card size multiplier.</summary>
    public static void SetCardSizeMultiplier(float value)
    {
        value = Mathf.Clamp(value, CardSizeMin, CardSizeMax);
        if (_cardSizeMultiplier is not null && Mathf.IsEqualApprox(_cardSizeMultiplier.Value, value))
            return;
        _cardSizeMultiplier = value;
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath);
        cfg.SetValue(Section, "card_size_multiplier", value);
        var error = cfg.Save(SettingsPath);
        if (error != Error.Ok)
            GD.PushWarning($"[GameSettings] Could not save settings: {error}");
        CardSizeMultiplierChanged?.Invoke();
    }

    private static bool ReadBool(string key, bool fallback)
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) != Error.Ok)
            return fallback;
        var value = cfg.GetValue(Section, key, fallback).AsBool();
        return value;
    }

    private static float ReadFloat(string key, float fallback)
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) != Error.Ok)
            return fallback;
        var value = cfg.GetValue(Section, key, fallback).AsSingle();
        return value;
    }
}