# Added for ASP Assistant Arknights on 2026-11-13; see NOTICE.
"""Pinyin initials for search (冻河 → dh, 集结指示 → jjzs).

Run after updating the operator, equipment, strategy, covenant, boss or bounty catalogues:
    pip install pypinyin
    python data/build_search_keys.py
Writes the name index and character initials used for player-written lineup names.
"""
import json
from pathlib import Path

from pypinyin import Style, lazy_pinyin

DATA = Path(__file__).resolve().parent
# Readings pypinyin gets wrong for these names.
OVERRIDES: dict[str, str] = {"以己之长": "yjzc"}


def initials(text: str) -> str:
    if text in OVERRIDES:
        return OVERRIDES[text]
    out = []
    for part in lazy_pinyin(text, style=Style.FIRST_LETTER, errors="default"):
        out.extend(ch.lower() for ch in part if ch.isascii() and ch.isalnum())
    return "".join(out)


def main() -> None:
    names: set[str] = set()
    for file in ("operators.json", "custom_operators.json"):
        names |= {o["name"] for o in json.loads((DATA / file).read_text(encoding="utf-8"))["operators"]}
    names |= {e["name"] for e in json.loads((DATA / "equipment.json").read_text(encoding="utf-8"))["equipment"]}
    strategies = json.loads((DATA / "strategies.json").read_text(encoding="utf-8"))
    strategies = strategies["strategies"] if isinstance(strategies, dict) else strategies
    names |= {s["name"] for s in strategies} | {s["initiator"] for s in strategies if s.get("initiator")}
    names |= {c["name"] for c in json.loads((DATA / "covenants.json").read_text(encoding="utf-8"))["covenants"]}
    for boss in json.loads((DATA / "bosses.json").read_text(encoding="utf-8"))["bosses"]:
        names.update([boss["name"], *boss["aliases"]])
    names |= {b["title"] for b in json.loads((DATA / "bounties.json").read_text(encoding="utf-8"))["entries"]}
    keys = {n: initials(n) for n in sorted(names)}
    # One ASCII initial per code point for player-written lineup names (CJK Extension A and Unified Ideographs).
    han = "".join(initials(chr(code)) or "." for code in range(0x3400, 0xA000))
    out = {"_modificationNotice": "Added for ASP Assistant Arknights on 2026-11-13; see NOTICE.", "initials": keys, "hanInitials": han}
    (DATA / "search_keys.json").write_text(json.dumps(out, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"{len(keys)} names")


if __name__ == "__main__":
    main()
