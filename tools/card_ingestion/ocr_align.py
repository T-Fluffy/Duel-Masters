#!/usr/bin/env python3
"""
OCR-based image-to-card alignment.

Background:
  Our card artwork lives in assets/art/cards/{SET}/Cards/DM01_NNN.jpg. The image
  filenames were produced by alphabetically sorting random GUID names, so the
  number NNN carries NO information about which Duel Masters card the image shows.
  This script re-discovers the correct card for every image by running Tesseract
  OCR on each image and fuzzy-matching the printed card name against the known
  card list (from the vendored Latepate64/duel-masters-json database).

Output:
  tools/card_ingestion/data/image_to_card.json
  A dict: { "set": { "imageFileName": { "card_id", "name", "confidence", "raw" } } }

Usage:
  python tools/card_ingestion/ocr_align.py [--sets DM-01 ...] [--force]
"""
from __future__ import annotations

import argparse
import io
import json
import re
import subprocess
import sys
from pathlib import Path

from rapidfuzz import fuzz, process

try:
    from PIL import Image, ImageOps
    HAVE_PIL = True
except Exception:
    HAVE_PIL = False

TESSERACT = r"C:\Program Files\Tesseract-OCR\tesseract.exe"
REPO_ROOT = Path(__file__).resolve().parents[2]
CARDS_INDEX = REPO_ROOT / "assets" / "art" / "cards" / "cards-index.json"
DB_PATH = REPO_ROOT / "tools" / "card_ingestion" / "data" / "DuelMastersCards.json"
OUT_PATH = REPO_ROOT / "tools" / "card_ingestion" / "data" / "image_to_card.json"
CARDS_ROOT = REPO_ROOT / "assets" / "art" / "cards"

SET_NAME_MAP = {
    "DM-01": "DM-01 Base Set",
    "DM-02": "DM-02 Evo-Crushinators of Doom",
    "DM-03": "DM-03 Rampage of the Super Warriors",
    "DM-04": "DM-04 Shadowclash of Blinding Night",
    "DM-05": "DM-05 Survivors of the Megapocalypse",
    "DM-06": "DM-06 Stomp-A-Trons of Invincible Wrath",
    "DM-07": "DM-07 Thundercharge of Ultra Destruction",
    "DM-08": "DM-08 Epic Dragons of Hyperchaos",
    "DM-09": "DM-09 Fatal Brood of Infinite Ruin",
}

# Corrections to the vendored Latepate64 database: some collector numbers are
# wrong relative to the official Duel Masters set numbering.
# key = "<setcode>/<card name>", value = correct collector id.
ID_CORRECTIONS = {
    "DM-07/Curious Eye": "20/55",
}

# Images whose name band is unreadable by OCR (verified by direct inspection of
# the artwork: OCR of the name band and/or distinctive rules/flavor text).
# key = "<setcode>/<imagefile>", value = exact card name from the DB.
IMAGE_OVERRIDES = {
    "DM-01/DM01_120.jpg": "Fear Fang",
    "DM-01/DM01_042.jpg": "Hanusa, Radiance Elemental",
    "DM-02/DM02_028.jpg": "Scissor Eye",
    "DM-03/DM03_055.jpg": "Mudman",
    "DM-05/DM05_010.jpg": "Gigazoul",
    "DM-05/DM05_052.jpg": "Bombat, General of Speed",
    "DM-06/DM06_022.jpg": "Neon Cluster",
    "DM-06/DM06_070.jpg": "Carrier Shell",
    "DM-06/DM06_080.jpg": "Future Slash",
    "DM-07/DM07_038.jpg": "Battleship Mutant",
    "DM-08/DM08_006.jpg": "Super Necrodragon Abzo Dolba",
    "DM-08/DM08_023.jpg": "Gigaclaws",
}


def load_json(path: Path):
    with path.open("r", encoding="utf-8") as fh:
        return json.load(fh)


def build_db_cards_by_set(db: dict) -> dict[str, dict[str, dict]]:
    """Return { set_code: { normalized_name: {"card": entry, "id": collector_id} } }.

    The collector id is taken from the *printing that belongs to this exact set*
    (not the first printing), because reprinted cards appear in multiple sets
    with different collector numbers (e.g. Spiral Gate is 40/110 in DM-01 but
    47/110 in DM-06).
    """
    result: dict[str, dict[str, dict]] = {}
    for card in db.get("cards", []):
        name = card.get("name", "")
        nn = norm_name(name)
        for printing in card.get("printings", []):
            full_set = printing.get("set", "")
            set_code = next((k for k, v in SET_NAME_MAP.items() if v == full_set), None)
            if set_code is None:
                continue
            collector_id = printing.get("id")
            corrected = ID_CORRECTIONS.get(f"{set_code}/{name}")
            if corrected:
                collector_id = corrected
            # Exact set match overrides any earlier (foreign-set) printing.
            result.setdefault(set_code, {})[nn] = {"card": card, "id": collector_id}
    return result


def norm_name(name: str) -> str:
    return re.sub(r"[^a-z0-9]+", " ", name.lower()).strip()


def collector_to_num(collector_id: str) -> int:
    """'85/110' -> 85 (regular). Super rares 'S6/110' are handled separately as
    zero because they need an 's' prefix to avoid colliding with regular high
    collector numbers (DM-01 has regular cards 101..110 AND super rares S1..S10)."""
    base = collector_id.split("/")[0].strip()
    if base.upper().startswith("S") and base[1:].isdigit():
        return -int(base[1:])  # negative sentinel: super rare index
    if base.isdigit():
        return int(base)
    raise ValueError(f"unrecognized collector id: {collector_id}")


def card_id_from_collector(set_code: str, collector_id: str) -> str:
    """Build a unique card id. Regular card '85/110' -> dm_01_085;
    super rare 'S6/110' -> dm_01_s06."""
    n = collector_to_num(collector_id)
    code = set_code.lower().replace("-", "_")
    if n < 0:
        return f"{code}_s{abs(n):02d}"
    return f"{code}_{n:03d}"


def ocr_image(path: Path) -> str:
    """OCR the card. The card name sits in the top band of the art; to improve
    name fidelity we also OCR an upscaled crop of the top portion and merge."""
    results = []

    if HAVE_PIL:
        try:
            with Image.open(path) as im:
                im = im.convert("L")  # grayscale
                w, h = im.size
                # The name bar occupies roughly the top 15-25% of the card.
                crop = im.crop((0, 0, w, int(h * 0.35)))
                scale = max(1, 2400 // crop.width) * 2
                crop = crop.resize((crop.width * scale, crop.height * scale), Image.LANCZOS)
                crop = ImageOps.autocontrast(crop)
                buf = io.BytesIO()
                crop.save(buf, format="PNG")
                results.append(_run_tesseract_bytes(buf.getvalue(), psm=11))
                results.append(_run_tesseract_bytes(buf.getvalue(), psm=6))
        except Exception:
            pass

    # Full-card pass (rule text for fallback + a name hint).
    results.append(_run_tesseract(path, psm=6))

    merged = "\n".join(results)
    return merged


def _run_tesseract(path: Path, psm: int) -> str:
    proc = subprocess.run(
        [TESSERACT, str(path), "stdout", "--psm", str(psm)],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    return proc.stdout or ""


def _run_tesseract_bytes(data: bytes, psm: int) -> str:
    proc = subprocess.run(
        [TESSERACT, "stdin", "stdout", "--psm", str(psm)],
        input=data, capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    return proc.stdout or ""


def refine_candidate_lines(ocr_text: str) -> list[str]:
    """Return plausible card-name candidate strings from raw OCR output.

    The card name is printed near the top of the card. We return the first few
    non-empty, reasonably letter-rich lines.
    """
    candidates = []
    for line in ocr_text.splitlines():
        line = line.strip()
        if not line:
            continue
        letters = sum(c.isalpha() for c in line)
        if letters < 3:
            continue
        candidates.append(line)
        if len(candidates) >= 10:
            break
    return candidates


CARD_SUBTYPES = {
    "angel command", "armored wyvern", "lich", "shinobi", "twinpact cylinder",
    "cyber lord", "liquid people", "armorloid", "guardian", "giant insect",
    "beast folk", "dark lord", "rainbow phoenix", "volcano dragon", "survivor",
    "hedrian", "devil mask", "armored dragon", "living dead", "saint",
    "starlight tree", "splash queen", "melt warrior", "fire bird", "tornado animal",
    "dark monster", "blessed the shots", "kingdom", "boiler", "warrior of the almighty",
    "robot cat", "cyber virus", "grim reaper", "great beast", "ghost",
    "merfolk", "darkness beast", "fairy", "enchanter", "machine eat",
    "appallon express", "phantom flame", "dreammate", "machine", "blade",
    "warrior", "light bringer", "sea hacker", "cyber cluster", "mystery totem",
    "joker", "multi", "wild veggies", "balloon mushroom", "earth dragon",
    "nocturnic", "gigaberos", "deep marine", "snow faerie", "explorer",
    "machine eater", "fish", "zombie dragon", "pyramid", "dry worm",
    "empty crystal", "sea sleuth", "aeroforce", "lunatic emperor", "minomalia",
    "puzzle parts", "of yggdra", "armored dragon", "dragonoid", "tyranno drake",
    "horned beast", "phantom beast", "amazon", "pandora's box", "chrono dragon",
    "melon cat", "guardian", "beast folk", "pillage", "samurai",
}


def strip_noise(line: str, known_names: set[str]) -> str:
    """Minimize OCR noise for better matching."""
    s = line.strip()
    # Drop a leading single char + space that is OCR garbage like "6 ", "E ", "y "
    s = re.sub(r"^.\s+", "", s)
    # Drop a trailing stray colon/dashes then numbers
    s = re.sub(r"[-,:;\"'`~!*^=|]/?\s*$", "", s)
    s = s.strip()
    return s


def best_match(candidates: list[str], known: dict[str, list[str]]) -> tuple[str, float, str]:
    """Return (best_norm_name, score, raw_line) across all candidate lines."""
    best = (None, -1.0, "")
    for line in candidates:
        query = norm_name(strip_noise(line, set(known.keys())))
        if not query:
            continue
        hit = process.extractOne(query, list(known.keys()), scorer=fuzz.token_sort_ratio)
        if hit and hit[1] > best[1]:
            best = (hit[0], float(hit[1]), line)
    return best


def norm_for_frag(q: str) -> str:
    """Normalize for spell text fragment matching."""
    return re.sub(r"[^a-z0-9]+", " ", q.lower()).strip()


def build_text_index(known: dict[str, dict[str, dict]]) -> list[dict]:
    """Index each card by its rules text keywords so a noisy OCR line can be
    matched against real card text."""
    idx = []
    for name, info in known.items():
        entry = info["card"]
        tokens = set()
        for field in ("text", "subtypes", "races", "race"):
            vals = entry.get(field)
            if isinstance(vals, str):
                vals = [vals]
            if isinstance(vals, list):
                for v in vals:
                    tokens.update(norm_for_frag(v).split())
        idx.append({"name": name, "norm_name": name, "tokens": tokens})
    return idx


def text_fallback_match(raw_ocr: str, text_index: list[dict]) -> tuple[str, float]:
    """Try to match the full OCR dump against card rules-text tokens/names."""
    if not text_index:
        return None, 0.0
    query_tokens = set(norm_for_frag(raw_ocr).split())
    best_name, best_score = None, -1.0
    for item in text_index:
        inter = query_tokens & item["tokens"]
        if inter:
            # score = fraction of the card's distinctive tokens found, boosted
            # by matching against name words as well.
            name_tokens = set(norm_for_frag(item["norm_name"]).split())
            name_hit = len(query_tokens & name_tokens)
            score = len(inter) * 2 + name_hit
            if score > best_score:
                best_score = score
                best_name = item["norm_name"]
    return best_name, best_score


def build_mapping(sets_to_run: list[str], force: bool) -> dict:
    index = load_json(CARDS_INDEX)
    db = load_json(DB_PATH)
    by_set = build_db_cards_by_set(db)

    mapping: dict = {}
    set_by_code = {s["code"]: s for s in index["sets"]}

    for code in sets_to_run:
        set_info = set_by_code.get(code)
        if not set_info:
            print(f"[skip] unknown set code {code}", file=sys.stderr)
            continue
        known = by_set.get(code)
        if not known:
            # Try matching against the full-set name variant
            print(f"[warn] no DB cards for set {code} ({len(set_info.get('extractedImages', []))} images)",
                  file=sys.stderr)
            if code == "DMR-23-Promo":
                print("[warn] DMR-23-Promo not in Latepate64 db; skipping stats mapping", file=sys.stderr)
            known = {}

        cards_dir = CARDS_ROOT / code / "Cards"
        images = sorted(set_info.get("extractedImages", []))
        set_map: dict = {}
        stats = {"total": len(images), "matched": 0, "unmatched": 0}
        for image_name in images:
            image_path = cards_dir / image_name
            if not image_path.exists():
                set_map[image_name] = {"card_id": None, "name": None, "confidence": 0.0,
                                       "raw": "", "error": "file_missing"}
                stats["unmatched"] += 1
                continue

            # Manual override: name band unreadable by OCR but resolved by eye.
            # Checked first (no OCR needed) so it is authoritative.
            override_name = IMAGE_OVERRIDES.get(f"{code}/{image_name}")
            if override_name:
                on = norm_name(override_name)
                if known and on in known:
                    info = known[on]
                    card_id = card_id_from_collector(code, info["id"])
                    set_map[image_name] = {
                        "card_id": card_id,
                        "name": info["card"]["name"],
                        "confidence": 100.0,
                        "raw": "override",
                        "match": "override",
                    }
                    stats["matched"] += 1
                else:
                    set_map[image_name] = {"card_id": None, "name": None, "confidence": 100.0,
                                           "raw": "override", "error": "override_no_db"}
                    stats["unmatched"] += 1
                continue

            ocr_text = ocr_image(image_path)
            if not ocr_text.strip():
                set_map[image_name] = {"card_id": None, "name": None, "confidence": 0.0,
                                       "raw": ocr_text, "error": "empty_ocr"}
                stats["unmatched"] += 1
                continue
            candidates = refine_candidate_lines(ocr_text)
            if not known:
                set_map[image_name] = {"card_id": None, "name": None, "confidence": 0.0,
                                       "raw": "\n".join(candidates), "error": "no_db"}
                stats["unmatched"] += 1
                continue
            best_norm, score, raw_line = best_match(candidates, known)
            if best_norm and score >= 60:
                info = known[best_norm]
                card_id = card_id_from_collector(code, info["id"])
                set_map[image_name] = {
                    "card_id": card_id,
                    "name": info["card"]["name"],
                    "confidence": round(score, 1),
                    "raw": raw_line,
                }
                stats["matched"] += 1
            else:
                # Fallback: match full OCR text against card rules text tokens.
                text_index = build_text_index(known)
                txt_best, txt_score = text_fallback_match(ocr_text, text_index)
                if txt_best and txt_score >= 6:
                    info = known[txt_best]
                    card_id = card_id_from_collector(code, info["id"])
                    set_map[image_name] = {
                        "card_id": card_id,
                        "name": info["card"]["name"],
                        "confidence": round(50.0 + min(txt_score, 50), 1),
                        "raw": "\n".join(candidates),
                        "match": "text_fallback",
                    }
                    stats["matched"] += 1
                else:
                    set_map[image_name] = {"card_id": None, "name": None, "confidence": round(score, 1),
                                           "raw": "\n".join(candidates), "error": "low_confidence"}
                    stats["unmatched"] += 1
        mapping[code] = set_map
        print(f"[{code}] {stats['matched']}/{stats['total']} matched, {stats['unmatched']} unmatched")

    report_card_collisions(mapping)
    return mapping


def report_card_collisions(mapping: dict) -> None:
    """Report any card_id that maps to more than one image. Manual overrides are
    expected to resolve these; collisions left here mean the mapping is ambiguous
    and must be corrected before the mapping is trusted."""
    for code, set_map in mapping.items():
        by_card: dict[str, list[str]] = {}
        for img, info in set_map.items():
            cid = info.get("card_id")
            if cid:
                by_card.setdefault(cid, []).append(img)
        for cid, imgs in by_card.items():
            if len(imgs) > 1:
                print(f"[{code}] COLLISION card_id {cid} on images {imgs} "
                      f"-> {set_map[imgs[0]].get('name')}", file=sys.stderr)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--sets", nargs="*", default=list(SET_NAME_MAP.keys()), help="Optional subset of set codes")
    ap.add_argument("--force", action="store_true", help="Overwrite mapping for supplied sets")
    args = ap.parse_args()

    mapping = build_mapping(args.sets, args.force)
    OUT_PATH.write_text(json.dumps(mapping, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Wrote {OUT_PATH}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
