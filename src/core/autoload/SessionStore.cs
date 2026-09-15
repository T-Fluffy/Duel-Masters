using System;
using Godot;

namespace DuelMasters.Core.Autoload;

/// <summary>"Remember me" session persisted to user:// so the next launch can
/// restore the session or at least prefill the login form. Stores the username,
/// email, nickname and JWT token - never the password. Cleared on explicit
/// logout, unchecked login, or any 401 response.</summary>
public static class SessionStore
{
    private const string Path = "user://session.cfg";
    private const string Section = "session";

    public record SavedSession(string Username, string Email, string Nickname, string Token);

    public static SavedSession Blank() => new("", "", "", "");

    public static SavedSession Load()
    {
        try
        {
            var cfg = new ConfigFile();
            cfg.Load(Path);
            return new SavedSession(
                Str(cfg, "username"),
                Str(cfg, "email"),
                Str(cfg, "nickname"),
                Str(cfg, "token"));
        }
        catch (Exception)
        {
            return Blank();
        }
    }

    public static void Save(string username, string email, string nickname, string token)
    {
        var cfg = new ConfigFile();
        cfg.SetValue(Section, "username", username);
        cfg.SetValue(Section, "email", email);
        cfg.SetValue(Section, "nickname", nickname);
        cfg.SetValue(Section, "token", token);
        cfg.Save(Path);
    }

    public static void Clear()
    {
        var cfg = new ConfigFile();
        cfg.Load(Path);
        cfg.EraseSection(Section);
        cfg.Save(Path);
    }

    private static string Str(ConfigFile cfg, string key)
    {
        try
        {
            var v = cfg.GetValue(Section, key, "");
            return v.VariantType == Variant.Type.String ? v.AsString() : "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
