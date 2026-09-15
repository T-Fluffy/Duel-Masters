using System;

namespace DuelMasters.Domain;

/// <summary>Pure ELO rating math for ranked human-vs-human duels. Kept free of
/// I/O so the domain test suite can pin the rounding behaviour; the DuelHub
/// winner seam is the only caller that persists the results.</summary>
public static class EloRating
{
    public const int DefaultRating = 1000;
    public const int KFactor = 32;

    /// <summary>Expected score of <paramref name="self"/> against
    /// <paramref name="opponent"/> (0..1).</summary>
    public static double Expected(int self, int opponent) =>
        1.0 / (1.0 + Math.Pow(10.0, (opponent - self) / 400.0));

    /// <summary>New (winner, loser) ratings after a decisive game.</summary>
    public static (int Winner, int Loser) Apply(int winnerRating, int loserRating, int kFactor = KFactor)
    {
        var winner = winnerRating + (int)Math.Round(kFactor * (1.0 - Expected(winnerRating, loserRating)));
        var loser = loserRating + (int)Math.Round(kFactor * (0.0 - Expected(loserRating, winnerRating)));
        return (winner, loser);
    }
}
