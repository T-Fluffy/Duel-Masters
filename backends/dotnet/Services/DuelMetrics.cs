using System.Diagnostics.Metrics;

namespace DuelMasters.Server.Services;

/// <summary>Game-specific instruments, exported alongside the automatic
/// ASP.NET Core + runtime telemetry whenever an OTLP endpoint is configured
/// (see Program.cs). Counter/histogram names are stable API: dashboards and
/// alerts may rely on them.</summary>
public static class DuelMetrics
{
    public const string Name = "DuelMasters.Server";

    private static readonly Meter Meter = new(Name);

    /// <summary>Finished matches (all modes). Tag: winner_side.</summary>
    public static readonly Counter<long> MatchesFinished =
        Meter.CreateCounter<long>("duelmasters.matches_finished", "matches",
            "Finished matches by winner side.");

    /// <summary>Absolute ELO movement per ranked side, winner and loser.</summary>
    public static readonly Histogram<double> EloDelta =
        Meter.CreateHistogram<double>("duelmasters.elo_delta", "points",
            "Absolute ELO movement per ranked side.");
}
