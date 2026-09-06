import json
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(r"D:\Godot projects\Projects\duel_masters")
CARDS = ROOT / "src" / "resources" / "data" / "cards.json"
ART = ROOT / "assets" / "art" / "cards" / "DM-01" / "Cards"
OUT = ROOT / "tools" / "extract" / "ocr"
TESSERACT = r"C:\Program Files\Tesseract-OCR\tesseract.exe"

def main():
    OUT.mkdir(parents=True, exist_ok=True)
    data = json.loads(CARDS.read_text(encoding="utf-8"))
    records = [c for c in data if c.get("name") and "DM-01" in c.get("imagePath", "")]
    only = sys.argv[1] if len(sys.argv) > 1 else None
    if only == "samples":
        records = records[:3]
    existing = 0
    for i, rec in enumerate(records, 1):
        card_id = rec["id"]
        out = OUT / f"{card_id}.json"
        if out.exists() and out.stat().st_size > 20:
            existing += 1
            continue
        img = os.path.basename(rec["imagePath"])
        src = ART / img
        if not src.exists():
            print(f"[missing] {card_id} {img}", flush=True)
            continue
        texts = {}
        for psm in ("6", "4"):
            r = subprocess.run(
                [TESSERACT, str(src), "stdout", "--psm", psm],
                capture_output=True, timeout=120,
            )
            texts[f"psm{psm}"] = (r.stdout or b"").decode("utf-8", errors="replace")
        out.write_text(json.dumps(texts, ensure_ascii=False, indent=1), encoding="utf-8")
        if sum(len(v) for v in texts.values()) < 80:
            print(f"[low] {card_id} {img}", flush=True)
        if i % 10 == 0:
            print(f"[progress] {i}/{len(records)} fresh_done={i-existing}", flush=True)
        time.sleep(0.02)
    print(f"[done] cards={len(records)} existing={existing}", flush=True)

main()