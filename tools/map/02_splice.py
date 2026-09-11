"""Phase 4 splice: apply mapping.json onto cards.json.

- LF-normalising, idempotent, leaves promo skeletons untouched.
- keywords = existing + mapped (union, deduped, mapped wins for overlap).
- effects = mapped when the text produced any; otherwise the existing
  hand-authored effects survive (documented approximations keep their gameplay).
- repairs double-encoded mojibake in names (\\u00c3\\u009c.. -> U..).
- adds evolutionOf for EvolutionCreatures (ignored by the catalog until Phase 5).
"""
from __future__ import annotations

import json
from pathlib import Path

# Repo root derived from this file (tools/map/...) so the pipeline runs on any
# checkout path and any OS (CI runners are Linux).
ROOT = Path(__file__).resolve().parents[2]
CARDS = ROOT / "src" / "resources" / "data" / "cards.json"
MAPPING = ROOT / "tools" / "map" / "mapping.json"

VALID_KW = {
    "None", "Blocker", "ShieldTrigger", "DoubleBreaker", "TripleBreaker", "SpeedAttacker",
    "Slayer", "PowerAttacker", "Unblockable", "CannotAttackPlayers", "CannotAttackCreatures",
    "CanAttackUntappedCreatures", "CannotBeAttacked", "AttacksEachTurn", "Charger",
    "Survivor", "Stealth", "SummonRequiresSpellCast", "CannotAttackOutnumbered", "AnyBreaker",
}

VALID_EFF = {
    "OnPlay_Draw", "OnDestroyed_Draw", "Spell_DestroyPowerAtMost", "Spell_ReturnToHand",
    "Spell_TapCreature", "Spell_UntapOwnCreature", "Spell_Draw", "Spell_BoostPower",
    "PowerAttacker_AttackBoost", "OnPlay_TapCreature", "OnPlay_ReturnToHand",
    "OnPlay_DestroyPowerAtMost", "OnPlay_UntapOwnCreature", "OnPlay_UntapAllOwnCreatures",
    "OnPlay_ChargeMana", "OnDestroyed_ToHand", "OnDestroyed_ToMana", "Spell_DestroyAllCreatures",
    "Spell_ReturnUpToToHand", "Spell_ChargeMana", "Spell_DiscardRandom",
    "StaticPower_AttackPerGraveyardCiv", "StaticPower_AttackPerOtherCreature",
    "StaticPower_AttackWhileHaveRace", "StaticPower_AlwaysWhileHaveRace",
    "StaticPower_AlwaysPerOtherCreature", "StaticPower_AuraRace", "CostIncrease_Summon_ByCiv",
    "CostIncrease_Cast_ByCiv", "CostDecrease_Summon_All", "CostDecrease_Cast_All",
    "CostDecrease_Summon_ByRace",
    # DM-08 continuous/triggered abilities (senia, turbo rush, etc.)
    "StaticPower_AlwaysBoost", "StaticTurbo_SpeedAttackerAll",
    "AttackTrigger_OpponentDiscardsHand", "AttackTrigger_UntapAllOwnExceptSelf",
    "BlockedTrigger_BreakOneShield",
    # D3: crew breaker + "may"-gated attack-trigger choices
    "Breaker_PerOtherRace", "AttackTrigger_MayLookAtShields", "AttackTrigger_MaySearchToHand",
    "AttackTrigger_UnblockedMayDestroy", "AttackTrigger_MayDestroyPowerAtMost",
    # Activated tap abilities (Tap Ability card family)
    "Tap_Draw", "Tap_ReturnToHand", "Tap_TapOpponentCreature", "Tap_ReturnSpellFromManaToHand",
    "Tap_ReturnCreatureFromManaToHand", "Tap_ReturnManaCardToHand", "Tap_ReturnGraveCreatureToHand",
    "Tap_DestroyPowerAtMost", "Tap_DestroyBlocker", "Tap_BoostPowerEot", "Tap_GrantUnblockableEot",
    "Tap_GrantSlayerEot", "Tap_GrantSpeedAttackerEot", "Tap_GrantDoubleBreakerEot",
    "Tap_GrantUnblockableCivEot", "Tap_GrantCanAttackUntappedCivEot", "Tap_ChargeMana",
    "Tap_DiscardRandom", "Tap_UntapOwnCivEot", "Tap_ChooseRaceUntapEot",
    "Tap_ChooseRaceGrantSlayerEot", "Tap_ChooseRaceToHandEot", "Tap_GraveToMana", "Tap_HandToMana",
    "Tap_ManaToGrave", "Tap_GrantOwnCivPowerDoubleBreakerDestroyEot",
    "Tap_ChooseRaceMustAttackPowerAttackerEot", "Tap_ChooseRaceUnblockableByPowerEot",
    "Tap_OpponentDestroysOwnCreature", "Tap_BlockBreaksShieldEot", "Tap_AddOwnCreatureToShields",
    "Tap_DeckSearchCreatureToHand", "Tap_DeckSearchDragonSummonEotDestroy",
    "Tap_ChooseShieldLook", "Tap_ScryTopCards",
    "Tap_NotModelled",
    # D4: on-play / attack / destroyed / spell families with real card coverage
    "OnPlay_DestroyAny", "OnPlay_DestroyOwnCreature", "OnPlay_ReturnFromGraveyard",
    "OnPlay_ReturnFromMana", "OnPlay_SearchDeck", "OnPlay_DiscardOpponentRandom",
    "OnPlay_OpponentSacrifice", "OnPlay_FromGraveyardToMana", "OnPlay_ManaToGrave",
    "OnPlay_LookAtHand", "OnPlay_LookAtShields",
    "AttackTrigger_Draw", "AttackTrigger_DiscardOpponentRandom",
    "AttackTrigger_ReturnFromGraveyard", "AttackTrigger_ChargeMana", "AttackTrigger_TapCreature",
    "OnDestroyed_OpponentDiscardRandom", "OnDestroyed_DiscardHand", "OnDestroyed_DestroyMana",
    "OnDestroyed_DestroyAllPowerAtMost", "OnDestroyed_ReturnFromGraveyard",
    "OnDestroyed_ShieldToHand", "OnDestroyed_ShieldToGrave",
    "Spell_DestroyAny", "Spell_OpponentSacrifice", "Spell_SearchToHand", "Spell_SearchToMana",
    "Spell_ReturnFromGraveyard",
}

EFFECT_FIELD_ORDER = ("id", "target", "value", "data")

# Hand-authored effects that directly contradict the card's wiki text and are
# fully unrepresentable: keep such cards vanilla (documented note) rather than
# playing a wrong pseudo-effect.
DROP_CONFLICT_EFFECTS = {"dm_01_006", "dm_02_001", "dm_04_023", "dm_08_037"}


def fix_name(name):
    """Undo a latin-1-mis-decoded utf-8 name (Ã\x83\xc5\x93.. -> U..)."""
    if not name:
        return name
    try:
        encoded = name.encode("cp1252")
    except UnicodeEncodeError:
        return name
    try:
        fixed = encoded.decode("utf-8")
    except UnicodeDecodeError:
        return name
    return fixed if fixed != name else name


def canonical_effect(e: dict) -> dict:
    out = {}
    for k in EFFECT_FIELD_ORDER:
        v = e.get(k)
        if v or v == 0:
            out[k] = v
    return out


def main():
    raw = CARDS.read_text(encoding="utf-8")
    cards = json.loads(raw)
    mapping = json.loads(MAPPING.read_text(encoding="utf-8"))
    by_id = {m["id"]: m for m in mapping}

    races = {c["race"] for c in cards if c.get("race")}
    errors = []
    changed = replaced = dropped = mut_kw = mut_eff = mut_evo = mut_tap = 0
    mojibake = []

    for card in cards:
        m = by_id.get(card["id"])
        if m is None:
            continue

        fixed = fix_name(card.get("name"))
        if fixed != card.get("name"):
            card["name"] = fixed
            mojibake.append((card["id"], card.get("name")))

        # ---- validate mapping
        bad_kw = [k for k in m["keywords"] if k not in VALID_KW]
        bad_eff = [e["id"] for e in m["effects"] if e["id"] not in VALID_EFF]
        bad_tap = [e["id"] for e in (m.get("tapAbilities") or []) if e["id"] not in VALID_EFF]
        if bad_kw or bad_eff or bad_tap:
            errors.append(f"{card['id']}: bad kw {bad_kw} / eff {bad_eff} / tap {bad_tap}")

        # ---- keywords: union, existing first
        merged_kw = list(card.get("keywords") or [])
        for k in m["keywords"]:
            if k not in merged_kw:
                merged_kw.append(k)
        if merged_kw != (card.get("keywords") or []):
            card["keywords"] = merged_kw
            mut_kw += 1

        # ---- effects: mapped wins; otherwise keep existing (unless the
        # existing effect contradicts the wiki text -> drop to vanilla)
        mapped_eff = [canonical_effect(e) for e in m["effects"]]
        prev_eff = card.get("effects")
        if mapped_eff:
            card["effects"] = mapped_eff
            if prev_eff != mapped_eff:
                replaced += 1
            mut_eff += 1
        elif prev_eff:
            if card["id"] in DROP_CONFLICT_EFFECTS:
                card["effects"] = []
                dropped += 1
            else:
                mut_eff += 1  # kept existing (documented approximation)

        # ---- tapAbilities: mapped wins; otherwise keep existing (so the splice
        # preserves whatever the catalog already shipped).
        mapped_tap = [canonical_effect(e) for e in (m.get("tapAbilities") or [])]
        if mapped_tap:
            card["tapAbilities"] = mapped_tap
            mut_tap += 1

        # ---- crewCivilization from the mapped crew clause (idempotent: a card
        # whose crew clause disappeared drops the field again).
        if m.get("crew"):
            card["crewCivilization"] = m["crew"]
        elif "crewCivilization" in card:
            del card["crewCivilization"]

        # ---- evolutionOf on EvolutionCreatures only
        evo = m.get("evolutionOf")
        if evo:
            if evo not in races and evo != "Dragon":
                errors.append(f"{card['id']}: evolutionOf '{evo}' not a known race")
            out = {}
            inserted = False
            for k, v0 in card.items():
                out[k] = v0
                if k == "effects" and not inserted:
                    out["evolutionOf"] = evo
                    inserted = True
            if not inserted:
                out["evolutionOf"] = evo
            card.clear()
            card.update(out)
            mut_evo += 1

        changed += 1

    if errors:
        print("ERRORS:")
        for e in errors:
            print("  ", e)
        raise SystemExit(1)

    text = json.dumps(cards, indent=2, ensure_ascii=False)
    if raw.endswith("\n"):
        text += raw[-1]
    CARDS.write_text(text, encoding="utf-8", newline="")

    n_kw = sum(1 for c in cards if c.get("keywords"))
    n_eff = sum(1 for c in cards if c.get("effects"))
    n_evo = sum(1 for c in cards if c.get("evolutionOf"))
    n_tap = sum(1 for c in cards if c.get("tapAbilities"))
    print(f"mapped cards touched: {changed}")
    print(f"keywords mutated: {mut_kw} | effects set: {mut_eff} (replaced {replaced}, dropped {dropped}) | evo set: {mut_evo} | tapAbilities set: {mut_tap}")
    print(f"final: cards {len(cards)} | with keywords {n_kw} | with effects {n_eff} | evolutionOf {n_evo} | tapAbilities {n_tap}")
    print(f"mojibake name fixes: {len(mojibake)} -> {[n for _, n in mojibake]}")


if __name__ == "__main__":
    main()