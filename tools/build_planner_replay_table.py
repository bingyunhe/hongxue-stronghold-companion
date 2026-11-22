"""Build the desktop reference from selected batches of paired SP3 replays."""
import argparse
import gzip
import json
from collections import defaultdict
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def build(folder, profiles, batches):
    by_style = {}
    source = None
    for style in ("core", "small"):
        matches = {}
        for file in sorted(p for batch in batches for p in folder.glob(f"{batch}-{style}-*.json")):
            recording = read(file)
            meta = recording["metadata"]
            if meta["difficulty"] != 8 or meta["style"] != style or meta["branch"] is not None:
                raise ValueError(f"unexpected route policy in {file}")
            fingerprint = (meta["checkpoint"], meta["version"], meta["catalogue"])
            if source is not None and source != fingerprint:
                raise ValueError(f"different model or simulator data in {file}")
            source = fingerprint
            for row in recording["runs"]:
                if row["seed"] in matches:
                    raise ValueError(f"duplicate training seed {row['seed']}")
                matches[row["seed"]] = row
        by_style[style] = matches
    seeds = sorted(by_style["core"])
    if not seeds or set(seeds) != set(by_style["small"]):
        raise ValueError("expected the same nonempty seed set for both policies")
    names = sorted(profiles)
    ids = {name:i for i,name in enumerate(names)}
    purchases = [[0]*6 for _ in names]
    routes = defaultdict(list)
    for seed in seeds:
        pair = [by_style[s][seed] for s in ("core","small")]
        scopes = [(row["result"]["focus"],row["result"]["second"],row["result"]["boss"],row["result"]["band"],
                   row["events"][0][0]) for row in pair]
        if scopes[0] != scopes[1]:
            raise ValueError(f"unpaired state at seed {seed}")
        for style, row in enumerate(pair):
            first_buy = {}
            for _,term,name in row["purchases"]:
                if name in ids:
                    first_buy[name] = min(first_buy.get(name,term),term)
            for name,first in first_buy.items():
                for t in range(first-1,6):
                    purchases[ids[name]][t] += 1
            focus = row["result"]["focus"]
            if focus is None or row["result"]["second"] is None:
                continue
            waves = row["waves"]
            routes[focus].append(dict(Style=style, Activation=next((s["round"] for s in waves if focus in s["active"]),0),
                EarlyLeaks=sum(s["leaks"] for s in waves if s["terminal"]<5), Clear=row["result"]["cleared"],
                Hands=[[r["round"], *sorted({ids[n] for n in r["owned"] if n in ids})] for r in row["rounds"] if r["round"]<=14]))
    return dict(Names=names, PurchaseRuns=len(seeds)*2, Purchases=purchases, Routes=routes)


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("recordings",type=Path)
    ap.add_argument("--batches", nargs="+", default=["training"], help="filename prefixes for the paired batches to include")
    args = ap.parse_args()
    repo = Path(__file__).resolve().parents[1]
    table = build(args.recordings,read(repo/"data/operator_profiles.json")["operators"],args.batches)
    target = repo/"src/ASPAssistant.Core/Data/PlannerReplaySamples.json.gz"
    target.write_bytes(gzip.compress(json.dumps(table, ensure_ascii=False,separators=(",",":")).encode(),mtime=0))
    print(f"{table['PurchaseRuns']} trajectories; {len(table['Routes'])} main covenants; {target.stat().st_size} compressed bytes")
