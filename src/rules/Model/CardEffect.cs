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

    /// <summary>Static continuous: this creature always gets +{Value} power.</summary>
    StaticPower_AlwaysBoost = 706,

    /// <summary>
    /// Static aura (DM-08 Turbo Rush): every creature the controller controls in
    /// the battle zone has Speed Attacker - creatures entering while it is active,
    /// and creatures already there when it enters, lose summoning sickness.
    /// </summary>
    StaticTurbo_SpeedAttackerAll = 707,

    /// <summary>When this creature attacks, the opponent discards his entire hand.</summary>
    AttackTrigger_OpponentDiscardsHand = 708,

    /// <summary>When this creature attacks the opponent and is not blocked, untap all your creatures except itself.</summary>
    AttackTrigger_UntapAllOwnExceptSelf = 709,

    /// <summary>When this creature attacks the opponent and becomes blocked, it breaks one of the opponent's shields.</summary>
    BlockedTrigger_BreakOneShield = 710,

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

    // ------------------------------------------------ activated tap abilities
    // A creature with one of these effects may be tapped during its owner's main
    // phase on that owner's turn (while untapped and not summoning-sick, and only
    // before any creature has attacked) to use it instead of attacking - the
    // Duel Masters "Tap Ability" card family.

    /// <summary>Tap ability: draw {Value} card(s).</summary>
    Tap_Draw = 900,

    /// <summary>Tap ability: return target creature to its owner's hand.</summary>
    Tap_ReturnToHand = 901,

    /// <summary>Tap ability: tap one of your opponent's creatures.</summary>
    Tap_TapOpponentCreature = 902,

    /// <summary>Tap ability: return a spell from your mana zone to your hand.</summary>
    Tap_ReturnSpellFromManaToHand = 903,

    /// <summary>Tap ability: return a creature from your mana zone to your hand.</summary>
    Tap_ReturnCreatureFromManaToHand = 904,

    /// <summary>Tap ability: return a card in your opponent's mana zone to his hand.</summary>
    Tap_ReturnManaCardToHand = 905,

    /// <summary>Tap ability: return a {Data} creature from your graveyard to your hand.</summary>
    Tap_ReturnGraveCreatureToHand = 906,

    /// <summary>Tap ability: destroy opponent's creature with power &lt;= {Value}.</summary>
    Tap_DestroyPowerAtMost = 907,

    /// <summary>Tap ability: destroy one of your opponent's creatures that has Blocker.</summary>
    Tap_DestroyBlocker = 908,

    /// <summary>Tap ability: one of your creatures gets +{Value} power until end of turn.</summary>
    Tap_BoostPowerEot = 909,

    /// <summary>Tap ability: one of your creatures can't be blocked until end of turn.</summary>
    Tap_GrantUnblockableEot = 910,

    /// <summary>Tap ability: one of your creatures gets Slayer until end of turn.</summary>
    Tap_GrantSlayerEot = 911,

    /// <summary>Tap ability: one of your creatures gets Speed Attacker until end of turn.</summary>
    Tap_GrantSpeedAttackerEot = 912,

    /// <summary>Tap ability: one {Data} creature of yours gets Double Breaker until end of turn.</summary>
    Tap_GrantDoubleBreakerEot = 913,

    /// <summary>Tap ability: each {Data} creature of yours can't be blocked until end of turn.</summary>
    Tap_GrantUnblockableCivEot = 914,

    /// <summary>Tap ability: each {Data} creature of yours can attack untapped creatures until end of turn.</summary>
    Tap_GrantCanAttackUntappedCivEot = 915,

    /// <summary>Tap ability: put the top card of your deck into your mana zone.</summary>
    Tap_ChargeMana = 916,

    /// <summary>Tap ability: your opponent discards {Value} random card(s) from hand.</summary>
    Tap_DiscardRandom = 917,

    /// <summary>Tap ability: at the end of this turn, untap all of your {Data} creatures.</summary>
    Tap_UntapOwnCivEot = 918,

    /// <summary>Tap ability: choose a race. At the end of this turn, untap all creatures of that race.</summary>
    Tap_ChooseRaceUntapEot = 919,

    /// <summary>Tap ability: choose a race. Each creature of that race gets Slayer until end of turn.</summary>
    Tap_ChooseRaceGrantSlayerEot = 920,

    /// <summary>Tap ability: choose a race. Whenever one of your creatures of that race would be destroyed this turn, return it to your hand instead.</summary>
    Tap_ChooseRaceToHandEot = 921,

    /// <summary>Tap ability: put up to {Value} cards from your graveyard into your mana zone.</summary>
    Tap_GraveToMana = 922,

    /// <summary>Tap ability: put up to {Value} cards from your hand into your mana zone.</summary>
    Tap_HandToMana = 923,

    /// <summary>Tap ability: each player puts a card from their mana zone into their graveyard.</summary>
    Tap_ManaToGrave = 924,

    /// <summary>Tap ability: each of your {Data} creatures gets +{Value} power and Double Breaker until end of turn, and is destroyed after any battle it fights this turn.</summary>
    Tap_GrantOwnCivPowerDoubleBreakerDestroyEot = 925,

    /// <summary>Tap ability: choose a race. Each creature of that race attacks this turn if able and gets "Power Attacker +{Value}" until end of turn.</summary>
    Tap_ChooseRaceMustAttackPowerAttackerEot = 926,

    /// <summary>Tap ability: choose a race. Creatures of that race can't be blocked by creatures that have power {Value} or less this turn.</summary>
    Tap_ChooseRaceUnblockableByPowerEot = 927,

    /// <summary>Tap ability: your opponent chooses one of his creatures in the battle zone and destroys it.</summary>
    Tap_OpponentDestroysOwnCreature = 928,

    /// <summary>Tap ability: this turn, whenever any of your {Data} creatures attacks the opponent and is blocked, it breaks one of his shields.</summary>
    Tap_BlockBreaksShieldEot = 929,

    /// <summary>Tap ability: add one of your creatures from the battle zone to your shields face down.</summary>
    Tap_AddOwnCreatureToShields = 930,

    /// <summary>Tap ability: search your deck, put a creature into your hand, then shuffle.</summary>
    Tap_DeckSearchCreatureToHand = 931,

    /// <summary>Tap ability: search your deck for a creature with {Data} in its race, put it into the battle zone with "speed attacker", destroyed at the end of the turn; shuffle.</summary>
    Tap_DeckSearchDragonSummonEotDestroy = 932,

    /// <summary>Tap ability: choose one of your shields and look at it, then put it back where it was.</summary>
    Tap_ChooseShieldLook = 933,

    /// <summary>Tap ability: look at the top {Value} cards of your deck, then put them back in any order.</summary>
    Tap_ScryTopCards = 934,

    /// <summary>
    /// Tap Ability placeholder for cards whose activated ability is not yet
    /// representable in the engine. The card still carries the ability in the
    /// catalog (data preserved), but the engine refuses to activate it.
    /// </summary>
    Tap_NotModelled = 998,
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

    /// <summary>One of the caster's mana-zone cards.</summary>
    OwnManaZone = 4,

    /// <summary>One of the opponent's mana-zone cards.</summary>
    OpponentManaZone = 5,

    /// <summary>One of the caster's graveyard cards.</summary>
    OwnGraveyard = 6,
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