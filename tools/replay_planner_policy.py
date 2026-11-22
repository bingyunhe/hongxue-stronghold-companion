"""Run paired SP3 shopping policies and replay saved action prefixes on the pinned simulator."""
import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

import torch


CHECKPOINT = "36f3f951c3b32fadf8b42dd566257f1650242ebe99051ec7bcf565be762a4860"
CORES = "kjeragShip,ursusShip,lethaniaShip,egirShip,victoriaShip,sargonShip"
MINORS = "docShip,steadShip,arcaneShip,preciShip,deputShip,skillfulShip,swiftShip,indomShip"
VERSION = "act3autochess@V3_1"


def fingerprint(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("repo", type=Path, help="garrison-rl with its pinned, patched Stronghold-Protocol checkout")
    ap.add_argument("weights", type=Path)
    ap.add_argument("output", type=Path)
    ap.add_argument("--seed", type=int, required=True)
    ap.add_argument("--count", type=int, default=1)
    ap.add_argument("--difficulty", type=int, default=8)
    ap.add_argument("--style", choices=["core", "small", "policy"], default="policy")
    ap.add_argument("--focus", choices=CORES.split(","), help="audit one main covenant when it is available in the draw")
    ap.add_argument("--branch", choices=["buy", "upgrade"], help="force the first legal buy/upgrade choice after the specified round")
    ap.add_argument("--from-round", type=int, default=4)
    ap.add_argument("--replay", type=Path, help="verify each recorded observation and replay its action")
    ap.add_argument("--fork", type=Path, help="replay a saved seed up to --event then take a different legal action")
    ap.add_argument("--event", type=int, help="zero-based recorded decision used with --fork")
    ap.add_argument("--observations", type=Path, help="save the first legal buy/upgrade shop at each round and terminal per seed")
    args = ap.parse_args()
    if args.count < 1:
        ap.error("--count must be positive")
    if args.fork and (args.count != 1 or args.event is None or args.event < 0 or not args.branch or args.replay):
        ap.error("--fork requires one seed, --event and --branch, separately from --replay")
    if args.event is not None and not args.fork:
        ap.error("--event requires --fork")
    if hashlib.sha256(args.weights.read_bytes()).hexdigest() != CHECKPOINT:
        ap.error("checkpoint SHA256 differs from the SP3 v0.1 release")
    sys.path.insert(0, str(args.repo.resolve() / "python/garrison_rl"))
    from model import Encoder, Policy, to_torch
    torch.set_num_threads(1)
    gym = args.repo.resolve() / "sp-gym/spgym.mjs"
    observer = Path(__file__).with_name("record_planner_waves.mjs").resolve().as_uri()
    command = ["node", "--import", observer, str(gym), "--version", VERSION, "--mode", f"规程等级{args.difficulty}",
               "--envs", str(args.count), "--bands", "band_amiya,band_takecare,band_bonus",
               "--focus", args.focus or CORES, "--second", MINORS, "--rehearsal", "0", "--boss-rehearsal", "0"]
    with tempfile.TemporaryDirectory(dir=args.output.parent) as temp:
        waves = Path(temp, "waves.jsonl")
        waves.touch()
        env = dict(os.environ, PLANNER_SIM=str(args.repo.resolve() / "Stronghold-Protocol"), PLANNER_WAVES=str(waves.resolve()))
        with subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, encoding="utf-8", env=env) as proc:
            run(args, proc, waves, Encoder, Policy, to_torch)


def run(args, proc, waves, Encoder, Policy, to_torch):
    def request(message):
        proc.stdin.write(json.dumps(message) + "\n")
        proc.stdin.flush()
        response = proc.stdout.readline()
        if not response:
            raise RuntimeError(f"simulator exited with {proc.poll()}")
        return json.loads(response)

    try:
        spec = json.loads(proc.stdout.readline())["spec"]
        encoder = Encoder(spec)
        policy = Policy(encoder)
        policy.load_state_dict(torch.load(args.weights, map_location="cpu", weights_only=True))
        policy.eval()
        rows = [dict(seed=args.seed+i, events=[], rounds=[], purchases=[], branch=None) for i in range(args.count)]
        observations = request(dict(cmd="reset", seed=args.seed))
        finished = set()
        source = None
        shops = {}
        metadata = dict(checkpoint=CHECKPOINT, version=VERSION, catalogue=fingerprint(spec),
                        difficulty=args.difficulty, style=args.style, branch=args.branch, fromRound=args.from_round)
        if args.focus:
            metadata["focus"] = args.focus
        if args.replay:
            saved = json.loads(args.replay.read_text(encoding="utf-8"))
            if any(saved["metadata"][k] != v for k,v in metadata.items()) or [r["seed"] for r in saved["runs"]] != [r["seed"] for r in rows]:
                raise ValueError("replay settings or seeds differ from the recorded run")
            metadata = saved["metadata"]
            source = saved["runs"]
        if args.fork:
            saved = json.loads(args.fork.read_text(encoding="utf-8"))
            if any(saved["metadata"][k] != metadata[k] for k in ("checkpoint","version","catalogue","difficulty")):
                raise ValueError("fork source uses a different simulator, difficulty or model")
            source = [r for r in saved["runs"] if r["seed"] == args.seed]
            if len(source) != 1 or args.event >= len(source[0]["events"]):
                raise ValueError("seed or decision not present in the recording")
            metadata["fork"] = dict(event=args.event, observation=source[0]["events"][args.event][0])
        with torch.inference_mode():
            while len(finished) < len(rows):
                obs = observations["obs"]
                for o in obs:
                    o["level"] = args.difficulty
                logits = policy(to_torch(encoder.encode(obs), "cpu"))[0].numpy()
                actions = []
                for i, (o, row) in enumerate(zip(obs, rows)):
                    if i in finished:
                        actions.append(next(j for j,a in enumerate(o["acts"]) if spec["kinds"][a[0]] == "EndRound"))
                        continue
                    number = round(o["g"][0]*15)
                    terminal = round(o["g"][3]*6)
                    names = [spec["kinds"][a[0]] for a in o["acts"]]
                    if args.observations and "Buy" in names and "Upgrade" in names:
                        shops.setdefault((i,number,terminal), o)
                    scores = logits[i, :len(names)].copy()
                    core = next((j for j,v in enumerate(o["plan"]["cov"]) if v == 1), None)
                    minor = next((j for j,v in enumerate(o["plan"]["cov"]) if v == 0.5), None)
                    # SP3 supplies the legal-action ranking. Below five, the two routes give the same
                    # preference to different cards; all other actions and the remainder of the game use SP3.
                    target = core if args.style == "core" else minor
                    if args.style != "policy" and terminal < 5 and target is not None:
                        for j, (kind, slot, _) in enumerate(o["acts"]):
                            if spec["kinds"][kind] == "Buy" and target in spec["operators"][o["shop"][slot]]["covenants"]:
                                scores[j] += 2
                    action = int(scores.argmax())
                    step = len(row["events"])
                    at_branch = step == args.event if args.fork else number >= args.from_round and "Buy" in names and "Upgrade" in names
                    if args.branch and not args.replay and row["branch"] is None and at_branch:
                        choice = "Buy" if args.branch == "buy" else "Upgrade"
                        if choice not in names:
                            raise ValueError(f"{choice} is unavailable at decision {step}")
                        action = max((j for j,k in enumerate(names) if k == choice), key=scores.__getitem__)
                        row["branch"] = dict(round=number, terminal=terminal, funds=round(o["g"][2]*10),
                                             observation=fingerprint(o), shop=[spec["operators"][n]["name"] if n>=0 else None for n in o["shop"]],
                                             policy=names[int(scores.argmax())], forced=choice)
                    event = [fingerprint(o), action]
                    if source is not None and (not args.fork or step <= args.event):
                        expected = source[i]["events"][step]
                        if expected[0] != event[0]:
                            raise ValueError(f"seed {row['seed']} diverged before decision {len(row['events'])}")
                        if not args.fork or step < args.event:
                            action = expected[1]
                            event[1] = action
                        if args.replay and source[i]["branch"] and source[i]["branch"]["observation"] == event[0]:
                            row["branch"] = source[i]["branch"]
                    row["events"].append(event)
                    if not row["rounds"] or row["rounds"][-1]["round"] != number:
                        row["rounds"].append(dict(round=number, terminal=terminal, lifeFraction=o["g"][1],
                            coreActive=core is not None and bool(o["cov"][core][2]),
                            coreTop=core is not None and o["cov"][core][3]>=1,
                            owned=[spec["operators"][u[0]]["name"] for u in o["units"]]))
                    if names[action] == "Buy":
                        row["purchases"].append([number,terminal,spec["operators"][o["shop"][o["acts"][action][1]]]["name"]])
                    actions.append(action)
                observations = request(dict(cmd="step", actions=actions))
                for i, done in enumerate(observations["done"]):
                    if not done or i in finished:
                        continue
                    info = observations["info"][i]
                    if info["seed"] != rows[i]["seed"] or info["stuck"] or info["errors"]:
                        raise ValueError(f"incomplete simulation at seed {rows[i]['seed']}: {info}")
                    rows[i]["result"] = info
                    finished.add(i)
        recorded_waves = [json.loads(line) for line in waves.read_text(encoding="utf-8").splitlines()]
        for row in rows:
            row["waves"] = [wave for wave in recorded_waves if wave["seed"] == row["seed"]]
            if not row["waves"]:
                raise ValueError(f"no waves recorded for seed {row['seed']}")
        if args.replay and rows != source:
            raise ValueError("replayed outcome differs from recording")
        args.output.write_text(json.dumps(dict(metadata=metadata,runs=rows),ensure_ascii=False),encoding="utf-8")
        if args.observations:
            args.observations.write_text(json.dumps(dict(version=VERSION,catalogue=fingerprint(spec),spec=spec,
                                                    observations=list(shops.values())),ensure_ascii=False),encoding="utf-8")
    finally:
        if proc.poll() is None:
            proc.stdin.write('{"cmd":"close"}\n')
            proc.stdin.flush()
            proc.stdin.close()


if __name__ == "__main__":
    main()
