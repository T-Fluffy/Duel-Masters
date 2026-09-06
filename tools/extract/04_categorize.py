import json
import re
from pathlib import Path
from collections import Counter, defaultdict

report = json.loads(Path("tools/extract/_report.json").read_text(encoding="utf-8"))

families = [
    ("EVO", re.compile(r"evolution\s*[-:—]*put", re.I)),
    ("ETB_DRAW", re.compile(r"when you put (?:this|a) creature", re.I)),
    ("ETB_OTHER", re.compile(r"when you put", re.I)),
    ("WOULD_DESTROY_WHEN", re.compile(r"when this creature would be destroyed", re.I)),
    ("DESTROYED_WHEN", re.compile(r"when this creature is destroyed", re.I)),
    ("WINDS_WHEN", re.compile(r"when this creature.*battle", re.I)),
    ("BLOCKS_WHEN", re.compile(r"whenever this creature blocks", re.I)),
    ("ATTACKS_WHEN", re.compile(r"whenever this creature attacks|when this creature attacks", re.I)),
    ("STATIC_CANT_ATTACK_PLAYER", re.compile(r"can'?t attack players", re.I)),
    ("STATIC_CANT_ATTACK_CREATURES", re.compile(r"can'?t attack creatures", re.I)),
    ("STATIC_CANT_ATTACK", re.compile(r"can'?t attack\.", re.I)),
    ("STATIC_CAN_ATTACK_UNTAPPED", re.compile(r"can attack untapped", re.I)),
    ("STATIC_CANNOT_BE_BLOCKED", re.compile(r"can'?t be blocked", re.I)),
    ("STATIC_CANNOT_BE_ATTACKED", re.compile(r"can'?t be attacked", re.I)),
    ("STATIC_ATTACK_EACH_TURN", re.compile(r"attacks each turn", re.I)),
    ("ETB_TAP", re.compile(r"tap (?:it|creature|opponent)", re.I)),
    ("ETB_RETURN", re.compile(r"return (?:it|creature)", re.I)),
    ("ETB_DESTROY", re.compile(r"destroy", re.I)),
    ("ETB_DISCARD", re.compile(r"discard", re.I)),
    ("ETB_MANA", re.compile(r"mana zone", re.I)),
    ("ETB_LOOK_SHIELD", re.compile(r"look at (?:opponent.*shield|their shields|shield)", re.I)),
    ("BOARDWIPE", re.compile(r"destroy all creatures", re.I)),
    ("SPELL_DESTROY_N", re.compile(r"destroy.*power N.*or less|destroy.*power N|destroy one of your opponent", re.I)),
    ("SPELL_RETURN", re.compile(r"return (?:all|any number|up to N|N|one|creature)", re.I)),
    ("SPELL_DRAW", re.compile(r"draw", re.I)),
    ("SPELL_TAP", re.compile(r"tap", re.I)),
    ("SPELL_BOOST", re.compile(r"(?:power|creature).*\+N|gets \+N|\+N power|increase.*power", re.I)),
    ("SPELL_MANA", re.compile(r"mana zone|mana", re.I)),
    ("SEARCH_DECK", re.compile(r"search (?:your|the) deck", re.I)),
    ("SHIELD_GRAVE", re.compile(r"shield", re.I)),
    ("TAP_TRAIT", re.compile(r"\btap\b", re.I)),
    ("END_STEP_UNL", re.compile(r"end (?:of )?each turn", re.I)),
    ("WHEN_CAST_OWN", re.compile(r"when you cast", re.I)),
    ("DESTROYED_TO_MANA", re.compile(r"put (?:it|this creature) into (?:your )?mana zone", re.I)),
    ("DESTROYED_TO_HAND", re.compile(r"return (?:it|this creature) to (?:your )?hand|put (?:it|this creature) into (?:your )?hand", re.I)),
]

unmatched = []
buckets = defaultdict(list)
for e in sorted(report, key=lambda x: x["id"]):
    txt = e.get("engtext") or ""
    cur = set()
    for name, rx in families:
        if rx.search(txt):
            cur.add(name)
    lines = [l.strip() for l in txt.split("\n") if l.strip()]
    if not lines:
        cur.add("NO_TEXT")
    if cur:
        buckets[k := "|".join(sorted(cur))].append(e)
    else:
        unmatched.append(e)

print("family-group counts (cards):", len(buckets))
for k, v in sorted(buckets.items(), key=lambda kv: -len(kv[1])):
    print(f"{len(v):4d}  {k}")
print(f"\nUNMATCHED CARDS: {len(unmatched)}")
for e in unmatched:
    txt = e.get("engtext") or ""
    print(f"  {e['id']:10s} {e['name'][:44]:44s} :: {txt[:110]}")