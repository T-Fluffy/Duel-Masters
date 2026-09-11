"""Phase 2 mapping builder.

Reads tools/extract/_report.json (per-card wiki text + catalog facts) and maps
each card's english text onto the engine's keyword / effect palette.

For every playable card it emits:
  keywords    : list[str]                    keyword names (see Keyword enum)
  effects     : list[{id,target?,value?,data?}]
  evolutionOf : str|None                     base race for EvolutionCreatures
  note        : str|None                     documented approximation / unrepresentable
  src         : str                          the source text, for auditability

Lines no rule consumed are tested against a NOTED pattern table (pattern ->
documented approximation note); anything still unmatched is collected in
_lines_unmapped.txt for triage. Run repeatedly; writes mapping.json + triage
file. Idempotent.
"""
from __future__ import annotations

import json
import re
from pathlib import Path

# Repo root derived from this file (tools/map/...) so the pipeline runs on any
# checkout path and any OS (CI runners are Linux).
ROOT = Path(__file__).resolve().parents[2]
REPORT = ROOT / "tools" / "extract" / "_report.json"
OUT_DIR = ROOT / "tools" / "map"
OUT = OUT_DIR / "mapping.json"
TRIAGE = OUT_DIR / "_lines_unmapped.txt"


# ---------------------------------------------------------------- helpers

def norm_line(raw: str) -> str:
    line = re.sub(r"\s+", " ", raw.strip())
    line = re.sub(r"^[\s■•\-*]+", "", line)
    # normalize curly typography to ASCII so rules match cleanly
    line = line.replace("\u2019", "'").replace("\u2018", "'")
    line = line.replace("\u201c", '"').replace("\u201d", '"')
    line = line.replace("\u2014", "-").replace("\u2013", "-")
    line = line.replace("\u2022", " ")
    return line.strip()


def strip_reminders(text: str) -> str:
    """Remove italic reminder flourishes (''...'') but keep bold entity spans ('''...''')."""
    text = re.sub(r"'''(.+?)'''", r" \1 ", text)
    return re.sub(r"\s+", " ", re.sub(r"''.*?''", " ", text)).strip()


def flatten_templates(text: str) -> str:
    """Unwrap nested {{Name|...}} templates, keeping their inner text (e.g.
    {{End Step|end of the turn}} keeps the pipe). Used on Tap Ability bodies."""
    return re.sub(r"\{\{[^{}]*\}\}", lambda m: m.group(0)[2:-2], text)


# Tap Ability lines are the whole {{Tap Ability|<body>}} template on one line.
TAP_LINE = re.compile(r"^\{\{Tap Ability\|(?P<body>.*)\}\}$")

# DM-08 "Turbo Rush" abilities are the whole {{Turbo Rush|<body>}} template on
# one line; unwrap the body so it flows through the normal rule table.
TURBO_RUSH_LINE = re.compile(r"^\{\{Turbo Rush\|(?P<body>.*)\}\}$")


def scope_of(text: str, default="AnyCreature"):
    low = text.lower()
    if "your opponent" in low or "opponent's creatures" in low:
        return "OpponentCreature"
    if "one of your creatures" in low or "your creatures" in low:
        return "OwnCreature"
    return default


def E(id_, t=None, v=0, d=""):
    out = {"id": id_}
    if t:
        out["target"] = t
    if v:
        out["value"] = v
    if d:
        out["data"] = d
    return out


def add_note(notes, label):
    if label not in notes:
        notes.append(label)


# ---------------------------------------------------------------- templates

TEMPLATE_KEYWORDS = {
    "Double Breaker": "DoubleBreaker",
    "Triple Breaker": "TripleBreaker",
    "Blocker": "Blocker",
    "Charger": "Charger",
    "Speed Attacker": "SpeedAttacker",
    "Slayer": "Slayer",
    "Shield Trigger": "ShieldTrigger",
}

POWER_ATTACKER_TPL = re.compile(r"\{\{Power Attacker\|(\d+)\}\}", re.I)
STEALTH_TPL = re.compile(r"\{\{Stealth\|(\w+)\}\}", re.I)
NESTED_SURVIVOR = re.compile(r"^\{\{Survivor\|\|(?:\{\{([A-Za-z ]+)\|dotless\}\})\}\}$")
CREW_BREAKER_TPL = re.compile(r"\{\{Crew Breaker\|([A-Za-z ]+)\}\}", re.I)
CREW_CLAUSE = re.compile(
    r"^each of your (light|water|fire|darkness|nature) creatures may tap instead of "
    r"attacking to use this creature'?s (?:\{\{Tap\}\} )?ability\.?$", re.I)


def extract_power_attacker(text: str):
    m = POWER_ATTACKER_TPL.search(text)
    if not m:
        return None, text
    return int(m.group(1)), text[: m.start()] + " " + text[m.end():]


def split_templates(text: str):
    """Return (leftover, keyword_names). Knows the {{Name|...}} forms."""
    keywords = []
    m = NESTED_SURVIVOR.match(text)
    if m:
        keywords.append("Survivor")
        inner = m.group(1)
        if inner in TEMPLATE_KEYWORDS:
            keywords.append(TEMPLATE_KEYWORDS[inner])
        return "", keywords
    leftover = text
    for _ in range(6):
        m = re.search(r"\{\{(?P<name>[A-Za-z ]+?)(?:\|[^{}]*)?\}\}", leftover)
        if m is None:
            break
        name = m.group("name").strip()
        if name in TEMPLATE_KEYWORDS:
            keywords.append(TEMPLATE_KEYWORDS[name])
        leftover = leftover[: m.start()] + " " + leftover[m.end():]
    return strip_reminders(leftover), keywords


# ------------------------------------------------------------ line rule table
# Each rule: (name, regex, fn(match, text) -> 4-tuple (keywords, effects, note, evolutionOf))

def _rules():
    R = []

    def rule(name, pattern, fn):
        R.append((name, re.compile(pattern, re.I), fn))
        return fn

    def kw(key):
        return lambda m, t: ([key], [], None, None)

    # ----- bare static keywords
    rule("CantAttackPlayers", r"^this creature can'?t attack players\.?$", kw("CannotAttackPlayers"))
    rule("CantAttackCreatures", r"^this creature can'?t attack creatures\.?$", kw("CannotAttackCreatures"))
    rule("CantAttack", r"^this creature can'?t attack\.?$",
         lambda m, t: (["CannotAttackPlayers", "CannotAttackCreatures"], [], None, None))
    rule("CantBeBlocked", r"^this creature can'?t be blocked\.?$", kw("Unblockable"))
    rule("CanAttackUntapped", r"^this creature can attack untapped creatures\.?$", kw("CanAttackUntappedCreatures"))
    rule("CantBeAttacked", r"^this creature can'?t be attacked\.?$", kw("CannotBeAttacked"))
    rule("AttacksEachTurn",
         r"^this creature attacks each turn,? if able\.?$|^this creature attacks each turn\.?$|^this creature attacks each turn attack if able\|if able\.?$",
         kw("AttacksEachTurn"))
    rule("OutnumberedLock", r"^this creature can'?t attack while your opponent has more creatures in the battle zone than",
         kw("CannotAttackOutnumbered"))
    rule("NeedsSpellFirst", r"^you can summon this creature only if you have cast a spell this turn\.?$",
         kw("SummonRequiresSpellCast"))
    rule("PowerAttackerBare", r"^power attacker \+(\d+)(?:\.| \.)?$",
         lambda m, t: (["PowerAttacker"], [E("PowerAttacker_AttackBoost", v=int(m.group(1)))], None, None))
    rule("RaceScopedSlayer", r"^[a-z]+ and [a-z]+ slayer(?: .*)?\.?$",
         lambda m, t: (["Slayer"], [], "slayer limited to two argued civs", None))
    rule("BlockerScoped", r"^blocker\|.*$",
         lambda m, t: (["Blocker"], [], "blocker limited to argued civs", None))

    # ----- DM-08 Turbo Rush / continuous + attacked triggers
    rule("TurboSpeedAttackerAll",
         r"^each of your creatures in the battle zone has \"?speed attacker\"?[\s\.\"]*$",
         lambda m, t: ([], [E("StaticTurbo_SpeedAttackerAll")], None, None))
    rule("FlatPowerPlusBreaker",
         r"^this creature gets \+(\d+) power and has \"?double breaker\"?[\s\.\"]*$",
         lambda m, t: (["DoubleBreaker"], [E("StaticPower_AlwaysBoost", v=int(m.group(1)))], None, None))
    rule("CanAttackUntappedHasPowerAttacker",
         r"^this creature can attack untapped creatures and has \"?power attacker \+(\d+)\"?[\s\.\"]*$",
         lambda m, t: (["CanAttackUntappedCreatures", "PowerAttacker"],
                       [E("PowerAttacker_AttackBoost", v=int(m.group(1)))], None, None))
    rule("TriggerOppDiscardsHand",
         r"^whenever this creature attacks,? your opponent discards (?:his|her|their) hand\.?$",
         lambda m, t: ([], [E("AttackTrigger_OpponentDiscardsHand")], None, None))
    rule("UnblockedUntapExceptSelf",
         r"^whenever this creature is attacking your opponent and isn'?t blocked, untap all your creatures in the battle zone except [^.]*\.?$",
         lambda m, t: ([], [E("AttackTrigger_UntapAllOwnExceptSelf")], None, None))
    rule("BlockedBreaksShield",
         r"^whenever this creature is attacking your opponent and becomes blocked,? it breaks one of your opponent'?s shields?\.?$",
         lambda m, t: ([], [E("BlockedTrigger_BreakOneShield")], None, None))

    # ----- DM-08 "may"-gated attack-trigger choices (crew-family decisions)
    rule("MayLookAtShields",
         r"^when(?:ever)? this creature attacks,?(?: you may)? look at (\d+) of your opponent'?s shields?\. then put them back(?: where they were)?\.?$",
         lambda m, t: ([], [E("AttackTrigger_MayLookAtShields", v=int(m.group(1)))], None, None))
    rule("MaySearchToHand",
         r"^when(?:ever)? this creature attacks,? search your deck\. you may take a (?:card|creature)(?: from your deck)?,? and put it into your hand\. then shuffle your deck\.?$",
         lambda m, t: ([], [E("AttackTrigger_MaySearchToHand")], None, None))
    rule("MayUnblockedDestroy",
         r"^whenever this creature is attacking your opponent and isn'?t blocked,?(?: you may)? destroy a creature\.?$",
         lambda m, t: ([], [E("AttackTrigger_UnblockedMayDestroy")], None, None))
    rule("MayDestroyPowerAtMost",
         r"^whenever this creature attacks,?(?: you may)? destroy (?:one of your opponent'?s creatures?|1 of your opponent'?s creatures?|a creature) that has(?: power)? (\d+) or less\.?$",
         lambda m, t: ([], [E("AttackTrigger_MayDestroyPowerAtMost", v=int(m.group(1)))], None, None))
    rule("AtkDraw", r"^whenever this creature attacks,?(?: you may)? draw a card\.?$",
         lambda m, t: ([], [E("AttackTrigger_Draw", v=1)], None, None))
    rule("AtkOppDiscard", r"^whenever this creature attacks,? your opponent discards a card at random from (?:his|her) hand\.?$",
         lambda m, t: ([], [E("AttackTrigger_DiscardOpponentRandom", v=1)], None, None))
    rule("AtkReturnGrave", r"^whenever this creature attacks,?(?: you may)? return a ((?:[a-z]+ )?(?:spell|creature)) from your graveyard to your hand\.?$",
         lambda m, t: ([], [E("AttackTrigger_ReturnFromGraveyard", v=1, d=m.group(1).title())], None, None))
    rule("AtkCharge", r"^whenever this creature attacks,?(?: you may)? put the top card of your deck into your mana zone\.?$",
         lambda m, t: ([], [E("AttackTrigger_ChargeMana")], "may-flag ignored", None))
    rule("AtkTap", r"^whenever this creature attacks,?(?: you may)? choose a (darkness or fire) creature(?: in the battle zone)? and tap it\.?$",
         lambda m, t: ([], [E("AttackTrigger_TapCreature", d=m.group(1))], None, None))

    # ----- evolution
    rule("EvoDragon", r"^evolution[\u2014\-]put on one of your creatures that has ([A-Za-z ]+) in its race\.?$",
         lambda m, t: ([], [], None, "Dragon"))
    rule("EvoRace", r"^evolution[\u2014\-]put on one of your (.+?)(?:s|\.)$",
         lambda m, t: ([], [], None, m.group(1).strip()))

    # ----- ETB auto/targeted
    rule("EtbDraw", r"^when you put this creature into the battle zone,?(?: you may)? draw a? card\.?$",
         lambda m, t: ([], [E("OnPlay_Draw", v=1)], None, None))
    rule("EtbDrawN", r"^when you put this creature into the battle zone,?(?: you may)? draw (?:up to )?(\d+) cards?\.?$",
         lambda m, t: ([], [E("OnPlay_Draw", v=int(m.group(1)))], "'up to' drew max", None))
    rule("EtbCharge", r"^when you put this creature into the battle zone,?(?: you may)? put the top card of your deck into your mana zone\.?$",
         lambda m, t: ([], [E("OnPlay_ChargeMana")], "may-flag ignored", None))
    rule("EtbChargeN", r"^when you put this creature into the battle zone,?(?: you may)? put the top (\d+) cards? of your deck into your mana zone\.?$",
         lambda m, t: ([], [E("OnPlay_ChargeMana")] * int(m.group(1)), "may-flag ignored", None))
    rule("EtbUntapAll", r"^when you put this creature into the battle zone,?(?: you may)? untap (?:each of your creatures|all your creatures in the battle zone)\.?$",
         lambda m, t: ([], [E("OnPlay_UntapAllOwnCreatures")], None, None))
    rule("EtbUntapOne", r"^when you put this creature into the battle zone,?(?: you may)? untap one of your creatures(?: in the battle zone)?\.?$",
         lambda m, t: ([], [E("OnPlay_UntapOwnCreature", t="OwnCreature")], None, None))
    rule("EtbTap", r"^when you put this creature into the battle zone,?(?: you may)? choose (?:one of your opponent's creatures(?: in the battle zone)?|a creature(?: in the battle zone)?) and tap it\.?$",
         lambda m, t: ([], [E("OnPlay_TapCreature", t=scope_of(t))], None, None))
    rule("EtbReturn", r"^when you put this creature into the battle zone,?(?: you may)? choose (?:a creature(?: in the battle zone)?|one creature(?: in the battle zone)?|1 creature(?: in the battle zone)?|one of your opponent's creatures(?: in the battle zone)?) and return it to its owner's hand\.?$",
         lambda m, t: ([], [E("OnPlay_ReturnToHand", t=scope_of(t))], None, None))
    rule("EtbDestroyN", r"^when you put this creature into the battle zone,?(?: you may)? destroy one of your opponent's creatures that has power (\d+)[ ,]*or less\.?$",
         lambda m, t: ([], [E("OnPlay_DestroyPowerAtMost", t="OpponentCreature", v=int(m.group(1)))], None, None))
    rule("EtbDestroyAny", r"^when you put this creature into the battle zone,?(?: you may)? destroy one of your opponent's creatures\.?$",
         lambda m, t: ([], [E("OnPlay_DestroyAny", t="OpponentCreature")], None, None))
    rule("EtbDestroyOwnThenOppSacrifice",
         r"^when you put this creature into the battle zone,? destroy one of your creatures\. then your opponent chooses one of (?:his|her) creatures(?: in the battle zone)? and destroys it\.?$",
         lambda m, t: ([], [E("OnPlay_DestroyOwnCreature", t="OwnCreature"), E("OnPlay_OpponentSacrifice")], None, None))
    rule("EtbDestroyOwn", r"^when you put this creature into the battle zone,?(?: you may)? destroy one of your creatures(?: that has power (\d+)[ ,]*or less)?\.?$",
         lambda m, t: ([], [E("OnPlay_DestroyOwnCreature", t="OwnCreature", v=int(m.group(1) or 0))], "may-flag ignored", None))
    rule("EtbOppSacrifice", r"^when you put this creature into the battle zone,?(?: you may)? your opponent chooses one of (?:his|her) creatures(?: in the battle zone)? and destroys it\.?$",
         lambda m, t: ([], [E("OnPlay_OpponentSacrifice")], None, None))
    rule("EtbManaToGrave", r"^when you put this creature into the battle zone,?(?: you may)? put (\d+) (?:card|cards) from your mana zone into your graveyard\.?$",
         lambda m, t: ([], [E("OnPlay_ManaToGrave", v=int(m.group(1)))], None, None))
    rule("EtbReturnGrave", r"^when you put this creature into the battle zone,?(?: you may)? return (?!.*mana zone)(?:up to )?(?:a|an|one|(\d+)) (.*?)(?: from your graveyard)? ?to your hand\.?$",
         lambda m, t: ([], [E("OnPlay_ReturnFromGraveyard", v=int(m.group(1) or 1), d=m.group(2).strip().title())], "recent-first approximation", None))
    rule("EtbReturnMana", r"^when you put this creature into the battle zone,?(?: you may)? return (?:a|an|one|(\d+)) (?:card|cards|spell|spells) from your mana zone to your hand\.?(?: then your opponent[^.]*\.?)?$",
         lambda m, t: ([], [E("OnPlay_ReturnFromMana", v=int(m.group(1) or 1))], None, None))
    rule("EtbSearchDeck", r"^when you put this creature into the battle zone,? search your deck\. you may take a ([a-z ]+?) from your deck,?(?: show [a-z ]+? to your opponent,?)? and put it into your hand\.? then shuffle your deck\.?$",
         lambda m, t: ([], [E("OnPlay_SearchDeck", d=m.group(1).strip().title())], None, None))
    rule("EtbOppDiscard", r"^when you put this creature into the battle zone,? your opponent discards a card at random from (?:his|her) hand\.?$",
         lambda m, t: ([], [E("OnPlay_DiscardOpponentRandom", v=1)], None, None))
    rule("EtbGraveToMana", r"^when you put this creature into the battle zone,?(?: you may)? put (?:a|an|one|(\d+)) (?:card|cards|creature|creatures) from your graveyard into your mana zone\.?$",
         lambda m, t: ([], [E("OnPlay_FromGraveyardToMana", v=int(m.group(1) or 1))], None, None))
    rule("EtbLookHand", r"^when you put this creature into the battle zone,? look at your opponent'?s hand\.?$",
         lambda m, t: ([], [E("OnPlay_LookAtHand")], "informational", None))
    rule("EtbLookShields", r"^when you put this creature into the battle zone,? look at your opponent'?s shields\.? then put them back(?: where they were)?\.?$",
         lambda m, t: ([], [E("OnPlay_LookAtShields")], "informational", None))

    # ----- destroyed substitution / triggers
    rule("DieToMana", r"^when this creature would be destroyed, put it into your mana zone instead\.?$",
         lambda m, t: ([], [E("OnDestroyed_ToMana")], None, None))
    rule("DieToHand", r"^when this creature would be destroyed, (?:return|put) it (?:to|into) your hand instead\.?$",
         lambda m, t: ([], [E("OnDestroyed_ToHand")], None, None))
    rule("DieToHandMay", r"^when this creature would be destroyed, you may return (?:it|this creature) to your hand instead\.?(?: if you do, put a card from your hand into your graveyard\.?)?$",
         lambda m, t: ([], [E("OnDestroyed_ToHand")], "may-flag ignored", None))
    rule("DieDraw", r"^when this creature is destroyed,?(?: you may)? draw (?:a card|(\d+) cards?)\.?$",
         lambda m, t: ([], [E("OnDestroyed_Draw", v=int(m.group(1) or 1))], None, None))
    rule("DieOppDiscard", r"^when this creature is destroyed, your opponent discards a card at random from (?:his|her) hand\.?$",
         lambda m, t: ([], [E("OnDestroyed_OpponentDiscardRandom", v=1)], None, None))
    rule("DieDiscardHand", r"^when this creature is destroyed, each player discards (?:his|her|their) hand\.?$",
         lambda m, t: ([], [E("OnDestroyed_DiscardHand")], None, None))
    rule("DieManaToGrave", r"^when this creature is destroyed, each player chooses? (?:a|(\d+)) (?:card|cards) in (?:his|her|their) mana zone and puts? (?:it|them) into (?:his|her|their) graveyard\.?$",
         lambda m, t: ([], [E("OnDestroyed_DestroyMana", v=int(m.group(1) or 0))], None, None))
    rule("DieDestroyAllPower", r"^when this creature is destroyed, destroy all creatures that have power (\d+)[ ,]*or less\.?$",
         lambda m, t: ([], [E("OnDestroyed_DestroyAllPowerAtMost", v=int(m.group(1)))], None, None))
    rule("DieReturnGrave", r"^when this creature is destroyed,?(?: you may)? return another? creature from your graveyard to your hand\.?$",
         lambda m, t: ([], [E("OnDestroyed_ReturnFromGraveyard", v=1)], None, None))
    rule("DieShieldToHand", r"^when this creature is destroyed, you may choose one of your shields and put it into your hand\. (?:you can'?t use the \"?shield trigger\"? ability of that shield\.?)?$",
         lambda m, t: ([], [E("OnDestroyed_ShieldToHand")], "no shield-trigger ban", None))
    rule("DieShieldToGrave", r"^when this creature is destroyed, choose one of your shields and put it into your graveyard\.?$",
         lambda m, t: ([], [E("OnDestroyed_ShieldToGrave")], None, None))

    # ----- spells
    rule("SpDestroyN", r"^(?:destroy|choose) (?:one of your opponent's creatures|1 of your opponent's creatures|a creature) that has power (\d+)[ ,]*or less\.?$",
         lambda m, t: ([], [E("Spell_DestroyPowerAtMost", t=scope_of(t), v=int(m.group(1)))], None, None))
    rule("SpDestroyAny", r"^destroy one of your opponent's creatures\.?$",
         lambda m, t: ([], [E("Spell_DestroyAny", t="OpponentCreature")], None, None))
    rule("SpOppSacrifice", r"^your opponent chooses one of (?:his|her) creatures in the battle zone and destroys it\.?$",
         lambda m, t: ([], [E("Spell_OpponentSacrifice")], None, None))
    rule("SpSearchToHand", r"^search your deck\. you may take a ([a-z ]+?) from your deck,?(?: show [a-z ]+? to your opponent,?)? and? put it into your hand\. then shuffle your deck\.?$",
         lambda m, t: ([], [E("Spell_SearchToHand", d=m.group(1).strip().title())], None, None))
    rule("SpSearchToMana", r"^search your deck\. you may take a (?:[\w -]+ )?card from your deck and put it into your mana zone\. then shuffle your deck\.?$",
         lambda m, t: ([], [E("Spell_SearchToMana")], None, None))
    rule("SpReturnGrave", r"^return a creature from your graveyard to your hand\.?$",
         lambda m, t: ([], [E("Spell_ReturnFromGraveyard", v=1)], None, None))
    rule("SpReturn", r"^choose (?:a creature(?: in the battle zone)?|one of your opponent's creatures(?: in the battle zone)?) and return it to its owner's hand\.?(?: if it has dragon in its race, you may draw a card\.?)?$",
         lambda m, t: ([], [E("Spell_ReturnToHand", t=scope_of(t))], None, None))
    rule("SpReturnUp", r"^return up to (\d+) creatures? in the battle zone to their owners' hands?\.?$|^choose up to (\d+) creatures? in the battle zone and return them to their owners' hands?\.?$",
         lambda m, t: ([], [E("Spell_ReturnUpToToHand", t="AnyCreature", v=int(m.group(1) or m.group(2)))], None, None))
    rule("SpTap", r"^choose one of your opponent's creatures in the battle zone and tap it\.?$",
         lambda m, t: ([], [E("Spell_TapCreature", t="OpponentCreature")], None, None))
    rule("SpUntap", r"^untap one of your creatures(?: in the battle zone)?\.?$",
         lambda m, t: ([], [E("Spell_UntapOwnCreature", t="OwnCreature")], None, None))
    rule("SpDraw", r"^draw a card\.?$", lambda m, t: ([], [E("Spell_Draw", v=1)], None, None))
    rule("SpDrawN", r"^draw (\d+) cards?\.?$", lambda m, t: ([], [E("Spell_Draw", v=int(m.group(1)))], None, None))
    rule("SpDrawUpN", r"^draw up to (\d+) cards?\.?$",
         lambda m, t: ([], [E("Spell_Draw", v=int(m.group(1)))], "'up to' drew max", None))
    rule("SpBoost", r"^one of your creatures(?: in the battle zone)? gets \+(\d+) power until the (?:End Step\|)?end of the turn\.?$",
         lambda m, t: ([], [E("Spell_BoostPower", t="OwnCreature", v=int(m.group(1)))], None, None))
    rule("SpDestroyAll", r"^destroy all creatures(?: in the battle zone)?\.?$",
         lambda m, t: ([], [E("Spell_DestroyAllCreatures")], None, None))
    rule("SpCharge", r"^put the top card of your deck into your mana zone\.?$",
         lambda m, t: ([], [E("Spell_ChargeMana")], None, None))
    rule("SpChargeN", r"^put the top (\d+) cards? of your deck into your mana zone\.?$",
         lambda m, t: ([], [E("Spell_ChargeMana")] * int(m.group(1)), None, None))
    rule("SpChargerKw", r"^after you cast this spell, put it into your mana zone instead of your graveyard\.?$",
         lambda m, t: (["Charger"], [], None, None))
    rule("SpDiscard1", r"^your opponent discards a card at random from his hand\.?$",
         lambda m, t: ([], [E("Spell_DiscardRandom", v=1)], None, None))
    rule("SpDiscardN", r"^your opponent discards (\d+) cards? at random from his hand\.?$",
         lambda m, t: ([], [E("Spell_DiscardRandom", v=int(m.group(1)))], None, None))

    # ----- static powers
    rule("StAttackPerOther", r"^while attacking, this creature gets \+(\d+) power for each other creature you have in the battle zone\.?$",
         lambda m, t: ([], [E("StaticPower_AttackPerOtherCreature", v=int(m.group(1)))], None, None))
    rule("StAttackPerOtherRace", r"^while attacking, this creature gets \+(\d+) power for each other ([A-Za-z ]+?) (?:creature )?in the battle zone\.?$",
         lambda m, t: ([], [E("StaticPower_AttackPerOtherCreature", v=int(m.group(1)), d=m.group(2).strip())], None, None))
    rule("StAlwaysPerOtherRace", r"^this creature gets \+(\d+) power for each other ([A-Za-z ]+?) creature you have in the battle zone\.?$",
         lambda m, t: ([], [E("StaticPower_AlwaysPerOtherCreature", v=int(m.group(1)), d=m.group(2).strip())], None, None))
    rule("StAlwaysPerOtherYour", r"^this creature gets \+(\d+) power for each of your other ([A-Za-z ]+?) creatures? in the battle zone\.?$",
         lambda m, t: ([], [E("StaticPower_AlwaysPerOtherCreature", v=int(m.group(1)), d=m.group(2).strip())], None, None))
    rule("StAttackPerGraveCiv", r"^while attacking, this creature gets \+(\d+) power for each ([a-z]+) card in your graveyard\.?$",
         lambda m, t: ([], [E("StaticPower_AttackPerGraveyardCiv", v=int(m.group(1)), d=m.group(2).title())], None, None))
    rule("StAlwaysHaveRace", r"^this creature gets \+(\d+) power while you have (?:at least? 1|a) ([A-Za-z ]+?) in the battle zone\.?$",
         lambda m, t: ([], [E("StaticPower_AlwaysWhileHaveRace", v=int(m.group(1)), d=m.group(2).strip())], None, None))
    rule("StAlwaysHaveRaceAlt", r"^while you have (?:a|at least 1) ([A-Za-z ]+?) in the battle zone, this creature gets \+(\d+) power\.?$",
         lambda m, t: ([], [E("StaticPower_AlwaysWhileHaveRace", v=int(m.group(2)), d=m.group(1).strip())], None, None))
    rule("StAttackHaveRace", r"^while attacking, this creature gets \+(\d+) power while you have (?:at least? 1|a) ([A-Za-z ]+?) in the battle zone\.?$",
         lambda m, t: ([], [E("StaticPower_AttackWhileHaveRace", v=int(m.group(1)), d=m.group(2).strip())], None, None))
    rule("StAttackHaveRaceAlt", r"^while you have (?:at least 1|one) ([A-Za-z ]+?) in the battle zone, this creature gets \+(\d+) power during its attacks\.?$",
         lambda m, t: ([], [E("StaticPower_AttackWhileHaveRace", v=int(m.group(2)), d=m.group(1).strip())], None, None))
    rule("StAura", r"^each other ([A-Za-z ]+?) in the battle zone gets \+(\d+) power\.?$",
         lambda m, t: ([], [E("StaticPower_AuraRace", v=int(m.group(2)), d=m.group(1).strip())], None, None))

    # ----- cost modifiers
    rule("CostSummonAll", r"^your creatures (?:each )?cost (\d+) less to summon\.?(?: they can'?t cost less than \d+\.?)?$",
         lambda m, t: ([], [E("CostDecrease_Summon_All", v=int(m.group(1)))], None, None))
    rule("CostCastAll", r"^your spells (?:each )?cost (\d+) less to cast\.?(?: they can'?t cost less than \d+\.?)?$",
         lambda m, t: ([], [E("CostDecrease_Cast_All", v=int(m.group(1)))], None, None))
    rule("CostSummonByRace", r"^your creatures that have ([A-Za-z ]+?) in their race each cost (\d+) less to summon\.?(?: they can'?t cost less than \d+\.?)?$",
         lambda m, t: ([], [E("CostDecrease_Summon_ByRace", v=int(m.group(2)), d="Dragon")], "exact race substring", None))
    rule("CostUpCivBoth", r"^each ([a-z]+) creature costs (\d+) more to summon,? and each ([a-z]+) spell costs (\d+) more to cast\.?$",
         lambda m, t: ([], [E("CostIncrease_Summon_ByCiv", v=int(m.group(2)), d=m.group(1).title()),
                             E("CostIncrease_Cast_ByCiv", v=int(m.group(4)), d=m.group(3).title())], None, None))

    return R


RULES = _rules()


# ------------------------------------------------ tap-ability rule table
# The body of a {{Tap Ability|...}} line is mapped onto one activated effect.
# Unmatched bodies become Tap_NotModelled (data is kept, the engine refuses to
# use them until their subsystem lands). Each entry is (name, regex, fn).

def _tap_rules():
    T = []

    def tap(name, pattern, fn):
        T.append((name, re.compile(pattern, re.I), fn))
        return fn

    tap("DrawN", r"^draw (\d+) cards?\.?$", lambda m, t: E("Tap_Draw", v=int(m.group(1))))
    tap("DrawA", r"^draw a card\.?$", lambda m, t: E("Tap_Draw", v=1))
    tap("ReturnAny", r"^choose a creature in the battle zone and return it to its owner's hand\.?$",
        lambda m, t: E("Tap_ReturnToHand", t="AnyCreature"))
    tap("TapOpp", r"^choose one of your opponent's creatures in the battle zone and tap it\.?$",
        lambda m, t: E("Tap_TapOpponentCreature", t="OpponentCreature"))
    tap("ReturnSpellFromMana", r"^return a spell from your mana zone to your hand\.?$",
        lambda m, t: E("Tap_ReturnSpellFromManaToHand", t="OwnManaZone"))
    tap("ReturnCreatureFromMana", r"^return a creature from your mana zone to your hand\.?$",
        lambda m, t: E("Tap_ReturnCreatureFromManaToHand", t="OwnManaZone"))
    tap("ReturnOppMana", r"^choose a card in your opponent's mana zone and return it to his hand\.?$",
        lambda m, t: E("Tap_ReturnManaCardToHand", t="OpponentManaZone"))
    tap("ReturnGraveCiv", r"^return a ([a-z]+) creature from your graveyard to your hand\.?$",
        lambda m, t: E("Tap_ReturnGraveCreatureToHand", t="OwnGraveyard", d=m.group(1).title()))
    tap("DestroyBlocker", r"^destroy one of your opponent's creatures that has \"?blocker\.\"?$",
        lambda m, t: E("Tap_DestroyBlocker", t="OpponentCreature"))
    tap("DestroyPowerAtMost", r"^destroy one of your opponent's creatures that has power (\d+)[ ,]*or less\.?$",
        lambda m, t: E("Tap_DestroyPowerAtMost", t="OpponentCreature", v=int(m.group(1))))
    tap("GrantUnblockable", r"^choose one of your creatures in the battle zone\. it can'?t be blocked this turn\.?$",
        lambda m, t: E("Tap_GrantUnblockableEot", t="OwnCreature"))
    tap("GrantSlayer", r"^one of your creatures in the battle zone gets \"?slayer\"? until the (?:End Step\|)?end of the turn\.?$",
        lambda m, t: E("Tap_GrantSlayerEot", t="OwnCreature"))
    tap("GrantSpeedAttacker", r"^one of your creatures in the battle zone gets \"?speed attacker\"? until the (?:End Step\|)?end of the turn\.?$",
        lambda m, t: E("Tap_GrantSpeedAttackerEot", t="OwnCreature"))
    tap("GrantDoubleBreaker", r"^one of your ([a-z]+) creatures in the battle zone gets \"?double breaker\"? until the (?:End Step\|)?end of the turn\.?$",
        lambda m, t: E("Tap_GrantDoubleBreakerEot", t="OwnCreature", d=m.group(1).title()))
    tap("BoostPowerEot", r"^one of your creatures in the battle zone gets \+(\d+) power until the (?:End Step\|)?end of the turn\.?$",
        lambda m, t: E("Tap_BoostPowerEot", t="OwnCreature", v=int(m.group(1))))
    tap("GrantUnblockableCiv", r"^each of your ([a-z]+) creatures gets \"?this creature can'?t be blocked\"? until the (?:End Step\|)?end of the turn\.?$",
        lambda m, t: E("Tap_GrantUnblockableCivEot", d=m.group(1).title()))
    tap("GrantCanAttackUntappedCiv", r"^each of your ([a-z]+) creatures gets \"?this creature can attack untapped creatures\"? until the (?:End Step\|)?end of the turn\.?$",
        lambda m, t: E("Tap_GrantCanAttackUntappedCivEot", d=m.group(1).title()))
    tap("ChargeMana", r"^put the top card of your deck into your mana zone\.?$",
        lambda m, t: E("Tap_ChargeMana"))
    tap("DiscardRandom", r"^your opponent discards (\d+) cards? at random from his hand\.?$",
        lambda m, t: E("Tap_DiscardRandom", v=int(m.group(1))))
    tap("UntapOwnCiv", r"^end step\|at the end of (?:this |the )?turn,? untap all your ([a-z]+) creatures\.?$",
        lambda m, t: E("Tap_UntapOwnCivEot", d=m.group(1).title()))
    tap("ChooseRaceUntap", r"^choose a race\. at the end of this turn,? untap all creatures of that race in the battle zone\.?$",
        lambda m, t: E("Tap_ChooseRaceUntapEot"))
    tap("ChooseRaceSlayer", r"^choose a race\. each creature of that race gets \"?slayer\"? until the (?:End Step\|)?end of the turn\.?$",
        lambda m, t: E("Tap_ChooseRaceGrantSlayerEot"))
    tap("ChooseRaceToHand", r"^choose a race\. whenever one of your creatures of that race would be destroyed this turn,? return it to your hand instead\.?$",
        lambda m, t: E("Tap_ChooseRaceToHandEot"))
    tap("GraveToMana", r"^put up to (\d+) cards? from your graveyard into your mana zone\.?$",
        lambda m, t: E("Tap_GraveToMana", v=int(m.group(1))))
    tap("HandToMana", r"^put up to (\d+) cards? from your hand into your mana zone\.?$",
        lambda m, t: E("Tap_HandToMana", v=int(m.group(1))))
    tap("ManaToGrave", r"^each player puts? a card from (?:his|her|their) mana zone into (?:his|her|their) graveyard\.?$",
        lambda m, t: E("Tap_ManaToGrave", v=1))
    tap("OwnCivPowerDoubleBreakerDestroy", r"^until the (?:End Step\|)?end of the turn, each of your ([a-z]+) creatures in the battle zone gets \+(\d+) power and \"?double breaker\.\"? whenever any of those creatures battles this turn,? destroy it after the battle\.?(?:\s+\{\{[^}]*\}\})?$",
        lambda m, t: E("Tap_GrantOwnCivPowerDoubleBreakerDestroyEot", d=m.group(1).title(), v=int(m.group(2))))
    tap("ChooseRaceMustAttackPowerAttacker", r"^choose a race\. each creature of that race attacks this turn if able and gets \"?power attacker \+(\d+)\"? until the (?:End Step\|)?end of the turn\.?(?:\s+while attacking, a creature that has \"?power attacker \+(\d+)\"? gets \+(\d+) power\.)?$",
        lambda m, t: E("Tap_ChooseRaceMustAttackPowerAttackerEot", v=int(m.group(1))))
    tap("ChooseRaceUnblockableByPower", r"^choose a race\. creatures of that race can'?t be blocked by creatures that have power (\d+) or less this turn\.?$",
        lambda m, t: E("Tap_ChooseRaceUnblockableByPowerEot", v=int(m.group(1))))
    tap("OpponentDestroysOwnCreature", r"^your opponent chooses one of (?:his|her) creatures in the battle zone and destroys it\.?$",
        lambda m, t: E("Tap_OpponentDestroysOwnCreature"))
    tap("AddOwnCreatureToShields", r"^add one of your creatures from the battle zone to your shields face down\.?$",
        lambda m, t: E("Tap_AddOwnCreatureToShields", t="OwnCreature"))
    tap("BlockBreaksShield", r"^this turn, whenever any of your ([a-z]+) creatures is attacking your opponent and becomes blocked,? it breaks one of his shields\.?",
        lambda m, t: E("Tap_BlockBreaksShieldEot", d=m.group(1).title()))
    tap("DeckSearchCreatureToHand", r"^search your deck\. you may take a creature from your deck, show that creature to your opponent, and put it into your hand\. then shuffle your deck\.?$",
        lambda m, t: E("Tap_DeckSearchCreatureToHand"))
    tap("DeckSearchDragonSummonDestroy", r"^search your deck\. you may take a creature that has ([a-z]+) in its race from your deck and put it into the battle zone\. then shuffle your deck\. that creature has \"?speed attacker\.\"? at the (?:End Step\|)?end of the turn, destroy it\.?$",
        lambda m, t: E("Tap_DeckSearchDragonSummonEotDestroy", d=m.group(1).title()))
    tap("ChooseShieldLook", r"^choose a shield and look at it[,.]? then put it back where it was\.?$",
        lambda m, t: E("Tap_ChooseShieldLook"))
    tap("ScryTopCards", r"^look at the top (\d+) cards? of your deck[,.]? then put them back in any order\.?$",
        lambda m, t: E("Tap_ScryTopCards", v=int(m.group(1))))

    return T


TAP_RULES = _tap_rules()


def map_tap_body(body: str):
    """Map a flattened {{Tap Ability|...}} body onto a tap-effect dict (or None)."""
    for name, rx, fn in TAP_RULES:
        m = rx.match(body)
        if m:
            return fn(m, body)
    return None


# ---------------------------------------------- documented approximations table
# Patterns that describe effects the engine cannot model. Matching one attaches
# a documented note to the card instead of leaving the line in triage.
# Order matters: specific entries run first.

def _noted():
    N = []

    def note(name, pattern, label):
        N.append((name, re.compile(pattern, re.I), label))

    note("EndUntap", r"^end step\|at the end of each of your turns, (?:you may )?untap (?:this creature|all your creatures in the battle zone)\.?$", "end-step untap")
    note("EndSurvivor", r"^end step\|at the end of each of your turns, if this is your only creature in the battle zone, destroy it\.?$", "end-step survivor destroy")
    note("EndToHand", r"^end step\|at the end of your turn, return this creature to your hand\.?$", "end-step return to hand")
    note("EndUntapAll", r"^end step\|at the end of the turn,? untap all your creatures in the battle zone\.?$", "end-step untap all")
    note("EndSearch", r"^end step\|at the end of this turn, search your deck\.", "end-step search")
    note("TapAbility", r"^: .*$", "tap ability")
    note("GroupTap", r"^each of your (?:light|water|fire|darkness|nature) creatures may tap instead of attacking to use this creature's ability\.?$", "tap ability")
    note("AttackTrigger", r"^whenever this creature attacks,", "attack trigger")
    note("BattleTrigger", r"^whenever this creature (?:wins a battle|becomes blocked|blocks|is attacked|battles|finishes attacking),", "battle/block trigger")
    note("UnblockedAttack", r"^whenever this creature is attacking your opponent and isn'?t blocked,", "unblocked-attack trigger")
    note("OppTrigger", r"^whenever your opponent", "opponent trigger")
    note("OppCreatureTrigger", r"^whenever an opponent'?s creature", "opponent trigger")
    note("SiblingPut", r"^whenever you put (?:another|a|an) ", "sibling trigger")
    note("SiblingEvent", r"^whenever another creature (?:is|would be)", "sibling trigger")
    note("AnyTrigger", r"^whenever (?:any of your creatures|one of your|another of your|one of your other)", "trigger")
    note("CreateLoss", r"^whenever any of your creatures would be destroyed", "creature-loss trigger")
    note("WheneverCatch", r"^whenever (?:a player|you|each)", "trigger")
    note("CastTrigger", r"^when you cast this spell,? ", "cast trigger")
    note("DestructEvent", r"^when this creature (?:would be destroyed|would be put into your graveyard|is destroyed|is put into your graveyard|wins a battle|battles|attacks),? ", "destruction/battle event")
    note("WhenAttacks", r"^when this creature attacks", "attack event")
    note("OnPlayCatch", r"^when you put this creature into the battle zone,? ", "on-play effect")
    note("EventCatch", r"^when (?:one of|any of|a creature|another|your|you)", "event trigger")
    note("MonoMana", r"^while all the cards in your mana zone ", "mono-mana static")
    note("BattleStatic", r"^while (?:battling|attacking) ", "battle static")
    note("NoShieldStatic", r"^while you have no shields, ", "no-shield static")
    note("CondStatic", r"^while ", "conditional static")
    note("UnblockableClause", r"^this creature can'?t be blocked by (?:any creature that has power|creatures that have power|[a-z]+ creatures)", "unblockable clause")
    note("ProtectionClause", r"^this creature can'?t be attacked by (?:[a-z]+ creatures|[a-z]+ and [a-z]+ creatures|any creature that has)", "protection clause")
    note("CantAttackUntappedCiv", r"^this creature can attack untapped [a-z]+ creatures\.?$", "can-attack-untapped (civ-scoped)")
    note("AttackRestriction", r"^this creature can[ ']?t? attack (?:only|if|while|untapped)", "attack restriction")
    note("FlatStatic", r"^this creature gets \+(\d+) power\.?$", "flat static power")
    note("CountStatic", r"^this creature gets \+(\d+) power for each ", "count static power")
    note("PowerLock", r"^creatures that have power \d+ or more can'?t attack\.?$", "power lock")
    note("GlobalMustAttack", r"^each creature attacks each turn if able\.?$", "global must-attack")
    note("OppChoice", r"^your opponent chooses?", "opponent choice")
    note("OppDiscard", r"^your opponent discards", "opponent discard")
    note("Symmetric", r"^each player ", "symmetric effect")
    note("ShieldManip", r"^choose (?:one of your shields|any number of your shields|the same number of your shields)", "shield manipulation")
    note("ShieldToGrave", r"^each of your creatures in the battle zone gets \"?double breaker\"? until the (?:End Step\|)?end of the turn\.?$", "group temporary buff")
    note("TempGrant", r"^one of your creatures(?: in the battle zone)? gets \"?[a-z+0-9 ]+\"?(?: and \"?[a-z+0-9 ]+\"?)? until the (?:End Step\|)?end of the turn\.?$", "temporary ability grant")
    note("TempBoostBlocked", r"^one of your creatures(?: in the battle zone)? that has \"?blocker\"? gets \+\d+ power until the (?:End Step\|)?end of the turn\.?$", "blocker-scoped temp boost")
    note("GroupTempBuff", r"^each of your creatures in the battle zone gets (?:.+ until the (?:End Step\|)?end of the turn|\+\d+ power until)", "group temporary buff")
    note("MultiTap", r"^choose up to \d+ of your opponent'?s creatures in the battle zone and tap them\.?$", "multi-target tap")
    note("MassTap", r"^tap all ", "mass tap")
    note("Destroy", r"^destroy ", "destruction effect")
    note("Return", r"^return ", "return effect")
    note("Put", r"^put ", "zone effect")
    note("Add", r"^add ", "shield effect")
    note("Search", r"^search ", "search effect")
    note("Look", r"^look at ", "look effect")
    note("Reveal", r"^reveal ", "reveal effect")
    note("Draw", r"^draw ", "draw effect")
    note("ForEach", r"^for each ", "per-creature effect")
    note("Conditional", r"^if (?:your|the|all|you|it)", "conditional effect")
    note("TurnScoped", r"^during your opponent'?s", "turn-scoped effect")
    note("Lockdown", r"^players can'?t ", "rule lockdown")
    note("GroupAura", r"^each (?:of your|e) ", "group keyword/power aura")
    note("RulesText", r"^evolution creatures are put into the battle zone tapped\.?$", "rules text")
    note("FlexEvo", r"^you can put an evolution creature of any race on this creature\.?$", "flex evolution host")
    note("BattleUnblockable", r"^while attacking a creature, this creature can'?t be blocked\.?$", "battle unblockable")
    note("CastRestrict", r"^you can cast this spell only if ", "cast restriction")
    note("ShieldTriggerBonus", r"^after you cast a spell by using its \"?shield trigger\"? ability, put it into your hand instead of your graveyard\.?$", "shield-trigger bonus")
    note("Until", r"^until the ", "turn-scoped effect")
    note("DragonCost", r"^your creatures that have [a-z ]+ in their race each cost", "race-filtered cost discount")
    note("AttackEnabler", r"^this turn, ignore any effects", "attack-enabler spell")
    note("LiquidPeopleUnblockable", r"^liquid people can'?t be blocked\.?$", "aura: liquid people unblockable")
    note("GroupAura2Civ", r"^[\w ]+ and [\w ]+ (?:creatures? )?in the battle zone each get \+\d+ power\.?$", "group aura (two races)")
    note("ScaleDiscard", r"^discard any number of cards from your hand\. ", "scale-to-discard spell")
    note("ShieldBreak", r"^whenever this creature (?:would )?break? a shield,? ", "shield-break trigger")
    note("Targeted", r"^choose ", "targeted effect")
    note("ThisCreature", r"^this creature ", "creature clause")
    note("YourClause", r"^your ", "aura/condition")
    note("NowCatch", r"^unless |^that creature |^both players |^each [a-z]+ |^you may ", "misc unmodelled")

    return N


NOTED = _noted()


def match_line(line: str):
    for name, rx, fn in RULES:
        m = rx.match(line)
        if m:
            return name, fn(m, line)
    return None, None


def match_noted(line: str):
    for name, rx, label in NOTED:
        if rx.match(line):
            return name, label
    return None, None


# --------------------------------------------------------------- main driver

def main():
    report = json.loads(REPORT.read_text(encoding="utf-8"))
    OUT_DIR.mkdir(exist_ok=True)
    mapping = []
    triage = []
    used_rules = {}
    used_notes = {}

    for card in report:
        raw = card.get("engtext") or ""
        keywords, effects, tap_abilities, notes = [], [], [], []
        evo = None
        crew = None
        handled_lines = 0

        for line in (norm_line(x) for x in raw.split("\n")):
            if not line:
                continue

            # Tap abilities: the whole line is the {{Tap Ability|...}} template.
            m = TAP_LINE.match(line)
            if m:
                handled_lines += 1
                body = flatten_templates(strip_reminders(m.group("body")))
                eff = map_tap_body(body)
                if eff is None:
                    add_note(notes, "tap ability not modelled")
                    tap_abilities.append(E("Tap_NotModelled", d=body[:64]))
                else:
                    tap_abilities.append(eff)
                continue

            # DM-08 Turbo Rush: unwrap the whole-line template so the body flows
            # through the normal rules (fully modelled) or gets a documented note.
            m = TURBO_RUSH_LINE.match(line)
            if m:
                handled_lines += 1
                body = flatten_templates(strip_reminders(m.group("body")))
                name, res = match_line(body)
                if res is None:
                    add_note(notes, "turbo rush not modelled")
                    continue
                used_rules[name] = used_rules.get(name, 0) + 1
                kws, effs, note, evo2 = res
                if evo2 is not None:
                    evo = evo2
                for k in kws:
                    if k not in keywords:
                        keywords.append(k)
                effects.extend(effs)
                if note:
                    add_note(notes, note)
                continue

            # Crew: "Each of your {civ} creatures may tap instead of attacking to
            # use this creature's {{Tap}} ability." -> remember the civilization;
            # the ":{{Tap}} body" line for the same card becomes the tap ability.
            cm = CREW_CLAUSE.match(line)
            if cm:
                crew = cm.group(1).title()
                handled_lines += 1
                continue

            # Crew Breaker: {{Crew Breaker|Race}} -> break extra shields for each
            # other creature of that race (extra unanswered attackers).
            cb_m = CREW_BREAKER_TPL.search(line)
            if cb_m:
                effects.append(E("Breaker_PerOtherRace", d=cb_m.group(1).strip()))
                line = CREW_BREAKER_TPL.sub(" ", line)

            for templ_note in ("{{Tap Ability", "{{Turbo Rush", "{{Crew Breaker", "use this creature's {{Tap}} ability",
                               "use this creature's ability"):
                if templ_note in line:
                    add_note(notes, "tap/turbo/crew ability not modelled")

            pa, line2 = extract_power_attacker(line)
            if pa is not None:
                if "PowerAttacker" not in keywords:
                    keywords.append("PowerAttacker")
                effects.append(E("PowerAttacker_AttackBoost", v=pa))
                line = line2

            if STEALTH_TPL.search(line):
                if "Stealth" not in keywords:
                    keywords.append("Stealth")
                add_note(notes, "stealth (civ-blocked) approximated as unblockable")
                line = STEALTH_TPL.sub(" ", line)

            rest, tpl_kws = split_templates(line)
            for k in tpl_kws:
                if k not in keywords:
                    keywords.append(k)

            rest = rest.strip()
            if not rest:
                handled_lines += 1
                continue

            # Crew cards: ":{{Tap}} body" lines carry the activated tap ability.
            if crew and rest.startswith(":"):
                body = rest.lstrip(":").strip()
                eff = map_tap_body(body)
                if eff is None:
                    add_note(notes, "tap ability not modelled")
                    tap_abilities.append(E("Tap_NotModelled", d=body[:64]))
                else:
                    tap_abilities.append(eff)
                handled_lines += 1
                continue

            name, res = match_line(rest)
            if res is None:
                nname, nlabel = match_noted(rest)
                if nlabel is None:
                    triage.append(f"{card['id']}  {card['name'][:44]:44s} :: {rest}")
                    continue
                add_note(notes, nlabel)
                used_notes[nname] = used_notes.get(nname, 0) + 1
                handled_lines += 1
                continue
            used_rules[name] = used_rules.get(name, 0) + 1
            handled_lines += 1
            kws, effs, note, evo2 = res
            if evo2 is not None:
                evo = evo2
            for k in kws:
                if k not in keywords:
                    keywords.append(k)
            effects.extend(effs)
            if note:
                add_note(notes, note)

        if card["type"] == "EvolutionCreature":
            evo = card.get("race") or evo
            if "Dragon in its race" in raw:
                evo = "Dragon"

        mapping.append({"id": card["id"], "keywords": keywords, "effects": effects,
                        "tapAbilities": tap_abilities, "crew": crew,
                        "evolutionOf": evo, "note": "; ".join(notes) if notes else None,
                        "src": raw})

    OUT.write_text(json.dumps(mapping, ensure_ascii=False, indent=1), encoding="utf-8")
    Path(TRIAGE).write_text("\n".join(triage), encoding="utf-8")

    n_eff = sum(1 for m in mapping if m["effects"])
    n_kw = sum(1 for m in mapping if m["keywords"])
    n_note = sum(1 for m in mapping if m["note"])
    n_evo = sum(1 for m in mapping if m["evolutionOf"])
    n_tap = sum(1 for m in mapping if m["tapAbilities"])
    print(f"cards {len(mapping)} | effects {n_eff} | keywords {n_kw} | notes {n_note} | evo {n_evo} | tapAbilities {n_tap}")
    print(f"rule hits: {sum(used_rules.values())} across {len(used_rules)} rules")
    print(f"note hits: {sum(used_notes.values())} across {len(used_notes)} patterns")
    print(f"unmapped lines: {len(triage)}  -> {TRIAGE}")


if __name__ == "__main__":
    main()