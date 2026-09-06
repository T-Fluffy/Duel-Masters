namespace DuelMasters.Domain;

/// <summary>
/// The named effects the rules engine can resolve. Cards that carry none are
/// vanilla. The flat list mirrors the authoring palette used in the catalog.
/// Several effects carry a <see cref="CardEffect.Data"/> string (a civilization,
/// race or other condition) interpreted by the resolver.
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

    // ------------------------------------------------ creature triggered effects

    /// <summary>When this creature enters the battle zone, tap target creature.</summary>
    OnPlay_TapCreature = 500,

    /// <summary>When this creature enters the battle zone, return target creature to its owner's hand.</summary>
    OnPlay_ReturnToHand = 501,

    /// <summary>When this creature enters the battle zone, destroy target creature with power &lt;= {Value}.</summary>
    OnPlay_DestroyPowerAtMost = 502,

    /// <summary>When this creature enters the battle zone, untap one of your creatures.</summary>
    OnPlay_UntapOwnCreature = 503,

    /// <summary>When this creature enters the battle zone, untap each of your creatures.</summary>
    OnPlay_UntapAllOwnCreatures = 504,

    /// <summary>When this creature enters the battle zone, put the top card of your deck into your mana zone.</summary>
    OnPlay_ChargeMana = 505,

    // --------------------------------------------- destruction substitution rules

    /// <summary>If this creature would be destroyed, put it into its owner's hand instead.</summary>
    OnDestroyed_ToHand = 520,

    /// <summary>If this creature would be destroyed, put it into its owner's mana zone instead.</summary>
    OnDestroyed_ToMana = 521,

    // ------------------------------------------------------- spell effects

    /// <summary>Spell: destroy all creatures in the battle zone.</summary>
    Spell_DestroyAllCreatures = 600,

    /// <summary>Spell: return up to {Value} creatures in the battle zone to their owners' hands.</summary>
    Spell_ReturnUpToToHand = 601,

    /// <summary>Spell: put the top card of your deck into your mana zone.</summary>
    Spell_ChargeMana = 602,

    /// <summary>Spell: your opponent discards {Value} random card(s) from hand.</summary>
    Spell_DiscardRandom = 603,

    // ------------------------------------------- continuous / static power rules

    /// <summary>While attacking, +{Value} power for each card of civilization {Data} in your graveyard.</summary>
    StaticPower_AttackPerGraveyardCiv = 700,

    /// <summary>While attacking, +{Value} power for each other creature you have.</summary>
    StaticPower_AttackPerOtherCreature = 701,

    /// <summary>While attacking, +{Value} power while you have a {Data} in the battle zone.</summary>
    StaticPower_AttackWhileHaveRace = 702,

    /// <summary>+{Value} power while you have a {Data} in the battle zone.</summary>
    StaticPower_AlwaysWhileHaveRace = 703,

    /// <summary>+{Value} power for each other {Data} creature you have (all your creatures when {Data} is empty).</summary>
    StaticPower_AlwaysPerOtherCreature = 704,

    /// <summary>Each other creature that has race {Data} in the battle zone gets +{Value} power.</summary>
    StaticPower_AuraRace = 705,

    // ------------------------------------------------------- cost modifiers

    /// <summary>Each {Data} creature costs {Value} more to summon (global).</summary>
    CostIncrease_Summon_ByCiv = 800,

    /// <summary>Each {Data} spell costs {Value} more to cast (global).</summary>
    CostIncrease_Cast_ByCiv = 801,

    /// <summary>Your creatures cost {Value} less to summon (min 1).</summary>
    CostDecrease_Summon_All = 802,

    /// <summary>Your spells cost {Value} less to cast (min 1).</summary>
    CostDecrease_Cast_All = 803,

    /// <summary>Your creatures that have race {Data} cost {Value} less to summon (min 1).</summary>
    CostDecrease_Summon_ByRace = 804,
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
/// targeting scope, an optional number parameter (power cap, draw count or power
/// boost amount) and an optional data string (civilization, race or condition).
/// </summary>
public sealed record CardEffect(EffectId Id, EffectTargetScope Target, int Value = 0, string Data = "")
{
    /// <summary>True when resolving this effect requires the caller to pick a target creature.</summary>
    public bool NeedsTarget => Target != EffectTargetScope.None;
}