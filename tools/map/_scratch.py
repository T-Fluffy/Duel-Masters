import json
from pathlib import Path
from collections import Counter

report = json.loads(Path("tools/extract/_report.json").read_text(encoding="utf-8"))
evos = [x for x in report if x["type"] == "EvolutionCreature"]
lines = []
lines.append(f"evos: {len(evos)}")
for x in evos[:10]:
    lines.append(f"{x['id']} race={x['race']!r} cost={x['cost']} text={x['engtext']!r}")
lines.append("races: " + str(Counter(x["race"] for x in evos)))
Path("tools/map/_evo.txt").write_text("\n".join(lines), encoding="utf-8")