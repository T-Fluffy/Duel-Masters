#!/usr/bin/env python3
"""Phase 1 ingestion pipeline.

Reads the OCR alignment (image_to_card.json) plus the vendored card database
(DuelMastersCards.json) and emits:

  src/resources/data/cards.json        -- every extracted card image as a card
  src/resources/data/cards.schema.json -- JSON Schema validating cards.json

DM-01..DM-09 images get full stats from the DB; the DMR-23-Promo images (not
present in the vendored DB) get skeleton entries (id + imagePath only).

Output card schema (GDD-compatible superset):
  id, name, civilization, cardType, manaCost, manaNumber, power, race,
  imagePath, keywords[], scriptEffectId
"""
import json
import re
import sys
from pathlib import Path

# import shared set-name mapping from the OCR pipeline (same directory)
sys.path.insert(0, str(Path(__file__).resolve().parent))
from ocr_align import SET_NAME_MAP  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
ALIGN = ROOT / "tools/card_ingestion/data/image_to_card.json"
DB = ROOT / "tools/card_ingestion/data/DuelMastersCards.json"
OUT_CARDS = ROOT / "src/resources/data/cards.json"
OUT_SCHEMA = ROOT / "src/resources/data/cards.schema.json"
ART_ROOT = ROOT / "assets/art/cards"

IMPORTED_SETS = {"DM-01", "DM-02", "DM-03", "DM-04", "DM-05",
                 "DM-06", "DM-07", "DM-08", "DM-09"}
SKELETON_SET = "DMR-23-Promo"

# GDD cardType enum: Creature, Spell, EvolutionCreature.
# cardType mapping from DB type / supertypes.
CARDTYPE_MAP = {
    "Spell": "Spell",
    "Creature": "Creature",
    "Cross Gear": "Creature",  # out of DM-01..09 scope anyway
}


def norm_set_dbmatch(full_set: str) -> str | None:
    """Map a DB 'set' string to the short set code, or None."""
    return next((k for k, v in SET_NAME_MAP.items() if v == full_set), None)


def load_align():
    with ALIGN.open(encoding="utf-8") as fh:
        return json.load(fh)


def load_db():
    with DB.open(encoding="utf-8") as fh:
        return json.load(fh)


def card_id_from_collector(set_code: str, collector_id: str) -> str:
    """'85/110' -> 'dm_01_085'; super rare 'S6/110' -> 'dm_01_s06'."""
    base = collector_id.split("/")[0].strip()
    num_part = re.sub(r"\D", "", base)
    is_super = base[0].upper() == "S"
    code = set_code.lower().replace("-", "_")
    return f"{code}_{('s' if is_super else '')}{num_part}"


def db_index(db):
    """{ (set_code, normalized_name): {"card":.., "id": collector} } per set."""
    idx = {}
    for card in db.get("cards", []):
        name = card.get("name", "")
        for printing in card.get("printings", []):
            set_code = norm_set_dbmatch(printing.get("set", ""))
            if set_code is None:
                continue
            idx.setdefault(set_code, {})[norm(name)] = {
                "card": card,
                "id": printing.get("id"),
            }
    idx.setdefault(SKELETON_SET, {})
    return idx


def norm(name: str) -> str:
    return re.sub(r"[^a-z0-9]+", " ", name.lower()).strip()


def parse_power(power) -> int:
    """'3000' -> 3000, '6000+' -> 6000, None -> None."""
    if power is None:
        return None
    m = re.search(r"\d+", str(power))
    return int(m.group(0)) if m else None


def build_card(entry, dbcard, image_path, set_code):
    """Assemble a GDD-shaped card object from a DB card + image path."""
    civs = dbcard.get("civilizations") or []
    civ = civs[0] if civs else "Nature"
    db_type = dbcard.get("type", "Creature")
    supertypes = dbcard.get("supertypes") or []
    if "Evolution" in supertypes:
        card_type = "EvolutionCreature"
    else:
        card_type = CARDTYPE_MAP.get(db_type, "Creature")
    subtypes = dbcard.get("subtypes") or []
    race = subtypes[0] if subtypes else ("Spell" if card_type == "Spell" else None)
    power = parse_power(dbcard.get("power"))
    return {
        "id": entry["card_id"],
        "name": dbcard.get("name"),
        "civilization": civ,
        "cardType": card_type,
        "manaCost": int(dbcard.get("cost", 1)),
        "manaNumber": 1,
        "power": power,
        "race": race,
        "imagePath": image_path,
        "keywords": [],
        "scriptEffectId": "VANILLA",
    }


def skeleton_card(image_name, set_code):
    """imagePath-only entry for sets absent from the vendored DB."""
    base = image_name.rsplit(".", 1)[0].lower()
    card_id = re.sub(r"[^a-z0-9_]+", "_", base)
    return {
        "id": card_id,
        "name": None,
        "civilization": None,
        "cardType": "Creature",
        "manaCost": 1,
        "manaNumber": 1,
        "power": None,
        "race": None,
        "imagePath": image_path_for(set_code, image_name),
        "keywords": [],
        "scriptEffectId": "VANILLA",
    }


def image_path_for(set_code: str, image_name: str) -> str:
    return f"res://assets/art/cards/{set_code}/Cards/{image_name}"


def main():
    align = load_align()
    db = load_db()
    idx = db_index(db)

    cards = []
    problems = []

    for set_code in IMPORTED_SETS:
        smap = align.get(set_code, {})
        known = idx.get(set_code, {})
        for image_name in sorted(smap):
            e = smap[image_name]
            card_id = e["card_id"]
            dbcard = None
            if card_id is None:
                problems.append(f"{set_code}/{image_name}: no card_id (unmatched OCR)")
                cards.append(skeleton_card(image_name, set_code))
                continue
            # Look up DB card by the aligned name for this set.
            nm = norm(e["name"]) if e.get("name") else ""
            info = known.get(nm)
            if info is None:
                problems.append(f"{set_code}/{image_name} ({card_id}): DB card "
                                f"'{e.get('name')}' not found for set")
                cards.append(skeleton_card(image_name, set_code))
                continue
            dbcard = info["card"]
            ip = image_path_for(set_code, image_name)
            cards.append(build_card(e, dbcard, ip, set_code))

    # Skeleton entries for DMR-23-Promo images (not in the vendored DB).
    promo_dir = ART_ROOT / SKELETON_SET / "Cards"
    if promo_dir.is_dir():
        for img in sorted(promo_dir.glob("*.jpg")):
            cards.append(skeleton_card(img.name, SKELETON_SET))
    else:
        problems.append(f"{SKELETON_SET}: Cards directory not found")

    cards.sort(key=lambda c: c["id"])
    OUT_CARDS.parent.mkdir(parents=True, exist_ok=True)
    with OUT_CARDS.open("w", encoding="utf-8") as fh:
        json.dump(cards, fh, indent=2, ensure_ascii=False)

    full = sum(1 for c in cards if c["name"])
    print(f"Wrote {OUT_CARDS}: {len(cards)} cards "
          f"({full} full, {len(cards) - full} skeletons)")
    if problems:
        print("PROBLEMS:")
        for p in problems:
            print("  -", p)
    else:
        print("No problems.")


if __name__ == "__main__":
    main()
