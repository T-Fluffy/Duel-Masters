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

    /// <summary>Raised whenever <see cref="SetRevealAiHand"/> changes the flag.</summary>
    public static event Action? RevealAiHandChanged;

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

    private static bool ReadBool(string key, bool fallback)
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) != Error.Ok)
            return fallback;
        var value = cfg.GetValue(Section, key, fallback).AsBool();
        return value;
    }
}