import json
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(r"D:\Godot projects\Projects\duel_masters")
CARDS = ROOT / "src" / "resources" / "data" / "cards.json"
OUT = ROOT / "tools" / "extract" / "scrape"
API = "https://duelmasters.fandom.com/api.php"
ENGLISH_SETS = {
    "DM-01": "DM-01 Base Set",
    "DM-02": "DM-02 Oath of Protection",
    "DM-03": "DM-03 Stomp of the Victors",
    "DM-04": "DM-04 Vault of Resurrections",
    "DM-05": "DM-05 Awakening of the Champions",
    "DM-06": "DM-06 Infinite Divinity",
    "DM-07": "DM-07 Cyber Clash",
    "DM-08": "DM-08 Fairy Life",
    "DM-09": "DM-09 Duel of the Dragons",
}

FIELD_RE = re.compile(r"^\|\s*([A-Za-z0-9_]+)\s*=\s*(.*)$")


def unmojibake(s):
    try:
        fixed = s.encode("cp1252", errors="strict").decode("utf-8", errors="strict")
        if fixed != s:
            return fixed
    except (UnicodeEncodeError, UnicodeDecodeError):
        pass
    return s


def api_fetch(page):
    params = urllib.parse.urlencode({
        "action": "parse", "page": page, "prop": "wikitext",
        "format": "json", "formatversion": "2",
    })
    url = f"{API}?{params}"
    req = urllib.request.Request(url, headers={"User-Agent": "DuelMastersCatalogSync/1.0"})
    with urllib.request.urlopen(req, timeout=25) as resp:
        return json.loads(resp.read().decode("utf-8"))


def parse_cardtable(wt):
    start = -1
    for name in ("{{Cardtable", "{{TCGCardtable"):
        start = wt.find(name)
        if start >= 0:
            break
    if start < 0:
        return None
    depth = 0
    i = start
    while i < len(wt):
        o = wt.find("{{", i)
        c = wt.find("}}", i)
        if o != -1 and o < c:
            depth += 1
            i = o + 2
        elif c != -1:
            depth -= 1
            i = c + 2
            if depth == 0:
                break
        else:
            break
    block = wt[start:i]
    first = block.split("\n", 1)[0]
    block = block[len(first):]
    fields = {}
    current = None
    for line in block.split("\n"):
        l = line.rstrip("\r")
        m = FIELD_RE.match(l)
        if m:
            current = m.group(1)
            fields[current] = m.group(2)
            continue
        if current:
            fields[current] = fields.get(current, "") + "\n" + l.rstrip()
    return fields


def redirect_target(wt):
    m = re.match(r"\s*#REDIRECT\s*\[\[([^\]]+)\]\]", wt)
    return m.group(1).strip() if m else None


def templates_in(text):
    found = set()
    for m in re.finditer(r"\{\{\s*([A-Za-z][A-Za-z| ]*?)\s*\}\}", text):
        token = m.group(1).strip()
        token = re.sub(r"\s*\|\s*jp\s*", "", token)
        if token:
            found.add(token)
    return sorted(found)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    data = json.loads(CARDS.read_text(encoding="utf-8"))
    records = [c for c in data if c.get("name") and c.get("civilization")]
    only = sys.argv[1] if len(sys.argv) > 1 else None
    if only and only != "*":
        wanted = set(only.split(","))
        records = [c for c in records if c["id"] in wanted]
    existing = 0
    fresh = 0
    failed = []
    total = len(records)
    for i, rec in enumerate(records, 1):
        card_id = rec["id"]
        name = rec["name"]
        out = OUT / f"{card_id}.json"
        if out.exists() and out.stat().st_size > 60:
            existing += 1
            continue
        m = re.search(r"/DM-(0\d)/", rec.get("imagePath", ""))
        prefix = m.group(1) if m else ""
        candidates = [name]
        fixed = unmojibake(name)
        if fixed != name:
            candidates.insert(0, fixed)
        if prefix:
            candidates.append(f"{name} ({prefix})")
            candidates.append(f"{name} ({ENGLISH_SETS.get(prefix, prefix)})")
        payload = None
        page_used = None
        for page in candidates:
            try:
                payload = api_fetch(page)
                if "parse" in payload and "wikitext" in payload["parse"]:
                    page_used = page
                    break
            except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
                time.sleep(0.5)
                payload = None
        if not payload or "parse" not in payload:
            failed.append((card_id, name, "no page"))
            print(f"[failed] {card_id} {name} no page", flush=True)
            continue
        wt = payload["parse"]["wikitext"]
        target = redirect_target(wt)
        if target and target != page_used:
            try:
                payload = api_fetch(target)
                if "parse" in payload and "wikitext" in payload["parse"]:
                    page_used = target
                    wt = payload["parse"]["wikitext"]
            except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, json.JSONDecodeError):
                pass
        fields = parse_cardtable(wt)
        if fields is None:
            failed.append((card_id, name, "no cardtable"))
            print(f"[failed] {card_id} {name} no cardtable", flush=True)
            continue
        engtext = fields.get("engtext", "")
        result = {
            "card_id": card_id,
            "page": page_used,
            "type": fields.get("type", ""),
            "civilization": fields.get("civilization", ""),
            "race": fields.get("race", ""),
            "cost": fields.get("cost", ""),
            "power": fields.get("power", ""),
            "engtext": engtext,
            "templates": templates_in(engtext),
            "sets": {k: v for k, v in fields.items() if k.startswith("set") or k.startswith("setnum")},
            "wiki": wt,
        }
        out.write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
        fresh += 1
        if i % 25 == 0 or i == total:
            print(f"[progress] {i}/{total} fresh_done={fresh} existing={existing}", flush=True)
        time.sleep(0.55)
    print(f"[done] cards={total} existing={existing} fresh={fresh} failed={len(failed)}", flush=True)
    for f in failed:
        print(f"[failed_reason] {f[0]} :: {f[1]} :: {f[2]}", flush=True)

main()