import json
import re
import sys
from pathlib import Path

ROOT = Path(r"D:\Godot projects\Projects\duel_masters")
SCRAPE = ROOT / "tools" / "extract" / "scrape"
OCR = ROOT / "tools" / "extract" / "ocr"
OUT = ROOT / "tools" / "extract"


def clean_wikilinks(text):
    def repl(m):
        return m.group(1) or m.group(2)
    return re.sub(r"\[\[([^]|]*)\|([^]]*)\]\]", r"\2", re.sub(r"\[\[([^]]*)\]\]", repl, text))


def main():
    data = json.loads((ROOT / "src" / "resources" / "data" / "cards.json").read_text(encoding="utf-8"))
    by_id = {c["id"]: c for c in data if c.get("name")}
    report = []
    for cid, cat in sorted(by_id.items()):
        sp = SCRAPE / f"{cid}.json"
        oc = OCR / f"{cid}.json"
        entry = {
            "id": cid,
            "name": cat["name"],
            "civ": cat.get("civilization", ""),
            "type": cat.get("cardType", ""),
            "cost": cat.get("manaCost", ""),
            "power": cat.get("power", ""),
            "race": cat["race"] if isinstance(cat.get("race"), str) else "",
            "image": cat.get("imagePath", ""),
            "keywords_now": cat.get("keywords", []),
            "effects_now": cat.get("effects", []),
            "script_effect": cat.get("scriptEffectId", ""),
        }
        if sp.exists():
            d = json.loads(sp.read_text(encoding="utf-8"))
            entry["wiki_type"] = d.get("type", "")
            entry["wiki_race"] = d.get("race", "")
            entry["wiki_power"] = d.get("power", "")
            entry["templates"] = d.get("templates", [])
            entry["engtext_raw"] = d.get("engtext", "")
            entry["engtext"] = clean_wikilinks(d.get("engtext", ""))
            first_set = next((v for k, v in d.get("sets", {}).items() if k == "set1"), "")
            first_num = next((v for k, v in d.get("sets", {}).items() if k == "setnum1"), "")
            entry["set1"] = first_set
            entry["setnum1"] = first_num
            m = re.search(r"/DM-(0\d)/", cat.get("imagePath", ""))
            entry["catalog_set"] = m.group(1) if m else ""
            mn = re.search(r"DM0\d_(\d+)", cat.get("imagePath", ""))
            entry["catalog_no"] = mn.group(1) if mn else ""
        else:
            entry["wiki_type"] = entry["type"]
            entry["templates"] = []
            entry["engtext"] = ""
        if oc.exists():
            entry["ocr"] = json.loads(oc.read_text(encoding="utf-8"))
        report.append(entry)

    with_open_effects = [e for e in report if e["effects_now"]]
    with_keywords = [e for e in report if e["keywords_now"]]
    with_text = [e for e in report if e["engtext"]]
    print(f"catalog named     : {len(report)}")
    print(f"with current effects: {len(with_open_effects)}")
    print(f"with current keywords: {len(with_keywords)}")
    print(f"wiki engtext present: {len(with_text)}")
    no_text = [e for e in report if not e["engtext"]]
    print(f"no wiki engtext   : {len(no_text)}")
    for e in no_text:
        print(f"  {e['id']:10s} {e['name'][:42]:42s} tpl={','.join(e['templates']) or '-'}")
    (OUT / "_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    print("wrote tools/extract/_report.json")


main()