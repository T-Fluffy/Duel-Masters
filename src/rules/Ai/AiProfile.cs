namespace DuelMasters.Domain.Ai;

/// <summary>
/// Behaviour knobs that tune an AI opponent's decisions. A single "Standard"
/// profile ships now; the knobs are the seam future easy/normal/hard levels
/// plug into without touching the decision code.
/// </summary>
public sealed class AiProfile
{
    private AiProfile(string name, float aggression, float valueTempo, float blockCourage)
    {
        Name = name;
        Aggression = aggression;
        ValueTempo = valueTempo;
        BlockCourage = blockCourage;
    }

    /// <summary>The default, balanced opponent.</summary>
    public static AiProfile Standard { get; } = new("Standard", 0.6f, 0.5f, 0.5f);

    public string Name { get; }

    /// <summary>0..1 - how eagerly the AI swings at shields instead of trading.</summary>
    public float Aggression { get; }

    /// <summary>0..1 - prefer big impactful plays (1) over cheap efficient ones (0).</summary>
    public float ValueTempo { get; }

    /// <summary>0..1 - how willing the AI is to block even when the blocker dies.</summary>
    public float BlockCourage { get; }
}