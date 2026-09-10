using System;
using System.Globalization;

namespace DuelMasters.Domain.Networking;

/// <summary>
/// Parses scry decision tokens. The owner-visible state snapshot exposes the looked-at
/// top-of-deck cards with the instance ids <c>Scry:{i}</c>, where "i" is the position of
/// the card inside the exposed window. The player submits the window by sending those
/// instance ids back in the desired deck order; the server maps each token to the exact
/// window slot, keeping duplicate catalog cards distinguishable.
/// </summary>
public static class ScryTokens
{
    public const string Prefix = "Scry:";

    /// <summary>Returns the window slot index for a scry token, or -1 when malformed.</summary>
    public static int TryGetIndex(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return -1;
        var suffix = token.StartsWith(Prefix, StringComparison.Ordinal) ? token[Prefix.Length..] : token;
        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : -1;
    }
}