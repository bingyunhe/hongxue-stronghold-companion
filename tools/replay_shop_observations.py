"""Extract purchase-menu snapshots from assistant logs and test SP3's buy/upgrade choice offline."""
import argparse
import hashlib
import json
import re
import statistics
import sys
from pathlib import Path
from time import perf_counter

import torch

from replay_planner_policy import CHECKPOINT, VERSION, fingerprint


def snapshots(files):
    offers = re.compile(r"Baseline established:.*?round=(\d+), mode=规程等级(\d+), .*?money=(\d+).*?cards=\[([^\]]*)\]")
    hud = re.compile(r"Round = (\d+), Money = (\d+), UpgradeCost = (\d+), UpgradeMaxed = False, Mode = 规程等级(\d+), IsPreparation = True")
    seen = set()
    for path in files:
        terminal = economy = None
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            if "progress reset" in line or "Auto-ban computed:" in line:
                terminal = economy = None
            if found := re.search(r"\[Terminal\] Terminal level (\d+)", line):
                detected = int(found[1])
                if terminal is not None and terminal != detected:
                    economy = None
                terminal = detected
            if "[Economy] ShopEconomy" in line:
                found = hud.search(line)
                economy = tuple(map(int, found.groups())) if found else None
            found = offers.search(line)
            if not found or not terminal or economy is None:
                continue
            number, difficulty, money = map(int, found.groups()[:3])
            if (number, money, difficulty) != (economy[0], economy[1], economy[3]) or terminal >= 6 or money < economy[2]:
                continue
            cards = [card.rsplit("@", 1)[0] for card in found[4].split(", ") if card]
            # The log can repeat one visible shop for many frames or in a copied run directory.
            key = (number,difficulty,terminal,money,economy[2],tuple(cards))
            if key not in seen:
                seen.add(key)
                yield dict(round=number, difficulty=difficulty, terminal=terminal, funds=money,
                           upgrade=economy[2], cards=cards)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("repo", type=Path)
    ap.add_argument("weights", type=Path)
    ap.add_argument("reference", type=Path, help="reference observations captured from SP3, not player records")
    ap.add_argument("output", type=Path)
    ap.add_argument("logs", nargs="+", type=Path)
    args = ap.parse_args()
    if hashlib.sha256(args.weights.read_bytes()).hexdigest() != CHECKPOINT:
        ap.error("unexpected SP3 checkpoint")
    sys.path.insert(0,str(args.repo.resolve() / "python/garrison_rl"))
    from model import Encoder, Policy, to_torch
    reference = json.loads(args.reference.read_text(encoding="utf-8"))
    spec = reference["spec"]
    if reference["version"] != VERSION or reference["catalogue"] != fingerprint(spec):
        ap.error("reference observation catalogue mismatch")
    data = args.repo / "Stronghold-Protocol/data/versions/act3autochess-v3_1"
    prices = json.loads((data / "chess.json").read_text(encoding="utf-8"))
    operators = {o["name"]:(i,prices[o["id"]]["price"]) for i,o in enumerate(spec["operators"]) if prices[o["id"]]["visible"]}
    items = {o["name"]:i for i,o in enumerate(spec["items"])}
    enc = Encoder(spec)
    torch.set_num_threads(1)
    model = Policy(enc)
    model.load_state_dict(torch.load(args.weights,map_location="cpu",weights_only=True))
    model.eval()
    buy, upgrade = [spec["kinds"].index(k) for k in ("Buy","Upgrade")]
    records = []
    with torch.inference_mode():
        for row in snapshots(args.logs):
            choices = [operators[name] for name in row["cards"] if name in operators]
            legal = [[buy,i,0] for i,(_,cost) in enumerate(choices) if cost<=row["funds"]]
            if not legal:
                continue
            obs = []
            for donor in reference["observations"]:
                if donor["level"] != row["difficulty"] or round(donor["g"][0]*15) != row["round"] or round(donor["g"][3]*6) != row["terminal"]:
                    continue
                # Unobserved board, health and covenants remain those of each matched simulated context.
                o = dict(donor, g=donor["g"].copy(), shop=[i for i,_ in choices],
                         itemShop=[items[name] for name in row["cards"] if name in items][:1],
                         acts=[*legal,[upgrade,0,0]], level=row["difficulty"])
                o["g"][2],o["g"][4] = row["funds"]/10,row["upgrade"]/10
                obs.append(o)
                if len(obs)==8:
                    break
            if len(obs)<2:
                records.append(dict(**row,matched=len(obs)))
                continue
            start = perf_counter()
            actions = model(to_torch(enc.encode(obs),"cpu"))[0].argmax(1).tolist()
            decisions = ["升本" if i==len(legal) else spec["operators"][choices[legal[i][1]][0]]["name"] for i in actions]
            records.append(dict(**row,matched=len(obs),choices=decisions,ms=(perf_counter()-start)*1000))
    tested = [r for r in records if "choices" in r]
    summary = dict(eligible=len(records),tested=len(tested),unanimous=sum(len(set(r["choices"]))==1 for r in tested),
                   medianMs=statistics.median([r["ms"] for r in tested]) if tested else None)
    args.output.write_text(json.dumps(dict(summary=summary,records=records),ensure_ascii=False),encoding="utf-8")
    print(summary)


if __name__ == "__main__":
    main()
