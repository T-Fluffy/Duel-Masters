namespace DuelMasters.Domain;

/// <summary>
/// The named effects the rules engine can resolve. Cards that carry none are
/// vanilla. The flat list mirrors the authoring palette used in the catalog.
/// </summary>
public enum EffectId
{
    None = 0,

    /// <summary>When this card is put into the battle zone, draw {Value} card(s).</summary>
    OnPlay_Draw = 100,

    /// <summary>When this creature is destroyed, draw {Value} card(s).</summary>
    OnDestroyed_Draw = 200,

    /// <summary>Spell: destroy target creature with power &lt;= {Value}.</summary>
    Spell_DestroyPowerAtMost = 300,

    /// <summary>Spell: return target creature to its owner's hand.</summary>
    Spell_ReturnToHand = 301,

    /// <summary>Spell: tap target creature.</summary>
    Spell_TapCreature = 302,

    /// <summary>Spell: untap one of your creatures.</summary>
    Spell_UntapOwnCreature = 303,

    /// <summary>Spell: draw {Value} card(s).</summary>
    Spell_Draw = 304,

    /// <summary>Spell: target own creature gains +{Value} power until end of turn.</summary>
    Spell_BoostPower = 305,

    /// <summary>While attacking, this creature has +{Value} power (the Power Attacker modifier).</summary>
    PowerAttacker_AttackBoost = 400,
}

/// <summary>
/// Which battle-zone creatures a targeted effect may choose from. Effects that
/// resolve automatically (no choice) use <see cref="None"/>.
/// </summary>
public enum EffectTargetScope
{
    None = 0,

    /// <summary>Any player's battle-zone creature.</summary>
    AnyCreature = 1,

    /// <summary>Only the caster/owner's battle-zone creatures.</summary>
    OwnCreature = 2,

    /// <summary>Only the opponent's battle-zone creatures.</summary>
    OpponentCreature = 3,
}

/// <summary>
/// One game-rule effect on a <see cref="Card"/>: an <see cref="EffectId"/>, the
/// targeting scope, and an optional number parameter (power cap, draw count or
/// power boost amount).
/// </summary>
public sealed record CardEffect(EffectId Id, EffectTargetScope Target, int Value = 0)
{
    /// <summary>True when resolving this effect requires the caller to pick a target creature.</summary>
    public bool NeedsTarget => Target != EffectTargetScope.None;
}