"""Build current-round routes from a saved Lubiao bundle and zh_CN/gamedata."""

import argparse
from collections import defaultdict, deque
from itertools import product
import json
from pathlib import Path
import re


MAP_IDS = {
    "m01": "desert_crates", "m02": "desert_cactus", "m03": "desert_steel",
    "m04": "desert_laser", "m05": "city", "m06": "desert_camo", "m09": "snow",
    "m10": "palace", "m11": "snow_ice", "m12": "palace_arrows",
}


def shortest(start, end, walkable):
    # Equal-length routes follow the direction order checked in StrongholdAssistant.
    steps = ((0, 1), (-1, 0), (0, -1), (1, 0))
    if start not in walkable or end not in walkable:
        raise ValueError(f"地面路线端点不可通行: {start}, {end}")
    distances = {end: 0}
    queue = deque([end])
    while queue:
        r, c = cell = queue.popleft()
        for dr, dc in steps:
            nxt = (r + dr, c + dc)
            if nxt in walkable and nxt not in distances:
                distances[nxt] = distances[cell] + 1
                queue.append(nxt)
    if start not in distances:
        raise ValueError(f"地面路线无法到达终点: {start}, {end}")
    path = [start]
    while path[-1] != end:
        r, c = path[-1]
        path.append(next((r + dr, c + dc) for dr, dc in steps
                         if distances.get((r + dr, c + dc)) == distances[r, c] - 1))
    return path


def spawn_actions(level):
    for wave in level["waves"]:
        for fragment in wave["fragments"]:
            for action in fragment["actions"]:
                if action["actionType"] == "SPAWN":
                    yield "routes", action
    for phase in level["branches"].get("boss_battle_multi_player", {}).get("phases", []):
        for action in phase["actions"]:
            if action["actionType"] == "SPAWN":
                yield "extraRoutes", action


def terrain(layout, top, left, height, width):
    data = layout["mapData"]
    grid = data["map"]
    blocked = {(len(grid) - 1 - t["position"]["row"], t["position"]["col"])
               for preset in ("predefines", "hardPredefines") for t in (layout[preset] or {}).get("tokenInsts", [])
               if not t["hidden"] and t["inst"]["characterKey"] in {"trap_1105_accrate", "trap_1106_achplat"}}
    return {(r, c) for r in range(height) for c in range(width)
            if data["tiles"][grid[top+r][left+c]]["passableMask"] == "ALL" and (top+r, left+c) not in blocked}


def geometry(route, walkable, height, separate):
    flying = route["motionMode"] == "FLY"
    if route["motionMode"] not in {"WALK", "FLY"}:
        raise ValueError(route["motionMode"])
    approx = False

    def cell(pos):
        nonlocal approx
        r, c = height - 1 - pos["row"], pos["col"]
        if flying or (r, c) in walkable:
            return r, c
        approx = True
        return min(walkable, key=lambda q: (abs(q[0]-r)+abs(q[1]-c), q[0] != r, *q))

    part = [cell(route["startPosition"])]
    parts = []
    patrol = None
    for cp in route["checkpoints"]:
        kind = cp["type"]
        if kind.startswith("WAIT"):
            continue
        if kind in {"DISAPPEAR", "APPEAR_AT_POS"}:
            if part:
                parts.append(part)
            part = [] if kind == "DISAPPEAR" else [cell(cp["position"])]
        elif kind in {"MOVE", "PATROL_MOVE"}:
            dest = cell(cp["position"])
            if kind == "PATROL_MOVE" and patrol is None:
                patrol = dest
            part.extend([dest] if flying else shortest(part[-1], dest, walkable)[1:])
        else:
            raise ValueError(kind)
    if part:
        dest = patrol if patrol is not None else cell(route["endPosition"])
        part.extend([dest] if flying else shortest(part[-1], dest, walkable)[1:])
        parts.append(part)
    offset = 0 if separate else 1
    segments = [[{"col": c-offset, "row": r-offset} for r, c in segment] for segment in parts]
    lane = "stage" if separate else "lower" if route["startPosition"]["row"] == 0 else "upper"
    return {"lane": lane, "flying": flying, "approx": approx, "segments": segments}


def generate(bundle, gamedata, revision):
    raw = bundle.read_text(encoding="utf-8")
    datasets = [json.loads(p)["seq1"] for p in re.findall(r'JSON\.parse\(`(\{"seq1".*?\})`\)', raw)]
    source = next(d for d in datasets if "templateActions" in d)
    endgames = next(d for d in datasets if "modeRoundLimits" in d)
    bosses = {t["bossId"]: t for templates in endgames["templates"].values() for t in templates}
    activity = json.loads((gamedata/"excel/activity_table.json").read_bytes())["activity"]["AUTOCHESS"]["act3autochess"]["V3_1"]
    enemy_db = json.loads((gamedata/"levels/enemydata/enemy_database.json").read_bytes())
    names = {enemy["Key"]: enemy["Value"][0]["enemyData"]["name"]["m_value"] for enemy in enemy_db["enemies"]}
    names.update(source["names"])
    slots = {u["slot"]: u["key"] for t in source["templates"] for u in t["units"]}
    maps = {}
    for key in activity["stageDatasDict"]:
        map_id = MAP_IDS[key.split("_")[-1]]
        maps[map_id] = json.loads((gamedata/f"levels/activities/act3autochess/level_{key}.json").read_bytes())

    modes, levels, shapes, shape_ids = {}, {}, [], {}
    grids, grid_ids = [], {}
    for mode, battles in activity["battleDataDict"].items():
        definition = activity["modeDataDict"][mode]
        valid_maps = [MAP_IDS[k.split("_")[-1]] for k, stage in activity["stageDatasDict"].items() if mode in stage["mode"]]
        rounds = {}
        for number, entries in battles.items():
            rounds[number] = []
            for battle in entries:
                level_id = battle["levelId"].split("/")[-1].lower()
                rounds[number].append(level_id)
                if level_id in levels:
                    continue
                level = json.loads((gamedata/"levels"/(battle["levelId"].lower()+".json")).read_bytes())
                actions = list(spawn_actions(level))
                boss = bosses[battle["bossId"]] if battle["bossId"] and mode != "mode_training_2" else None
                training = level_id.startswith("level_act1autochess")
                separate = bool(boss or training)
                h, w = len(level["mapData"]["map"]), len(level["mapData"]["map"][0])
                board_data = {}
                for map_id, layout in maps.items():
                    if training and map_id not in valid_maps:
                        continue
                    walkable = terrain(level if training else layout, 0 if training else 14 if boss else 29,
                                       0 if training else 2, h, w)
                    grid_id = None
                    if separate:
                        grid = tuple("".join("." if (r,c) in walkable else "#" for c in range(w)) for r in range(h))
                        grid_id = grid_ids.setdefault(grid, len(grids))
                        if grid_id == len(grids):
                            grids.append(grid)
                    ids = {}
                    for table, act in actions:
                        ref = f"{table}/{act['routeIndex']}"
                        if ref in ids:
                            continue
                        path = geometry(level[table][act["routeIndex"]], walkable, h, separate)
                        signature = json.dumps(path, separators=(",", ":"))
                        shape = shape_ids.setdefault(signature, len(shapes))
                        if shape == len(shapes):
                            shapes.append(path)
                        ids[ref] = shape
                    board_data[map_id] = {"grid": grid_id, "routes": ids}

                groups = []
                if not separate:
                    by_action = defaultdict(set)
                    for table, act in actions:
                        route = level[table][act["routeIndex"]]
                        gate = "lower" if route["startPosition"]["row"] == 0 else "upper"
                        key = (act["key"], gate, act["preDelay"], act["interval"], act["count"])
                        by_action[key].add(f"{table}/{act['routeIndex']}")
                    for group in source["rounds"][number]:
                        if group["weight"] <= 0:
                            continue
                        choices = []
                        for act in group["acts"]:
                            key = slots[act["slot"]] if act["slot"] else act["enemy"]
                            refs = by_action[(key, act["gate"], act["pre"], act["interval"], act["tpl"])]
                            if not refs:
                                raise ValueError(f"{level_id} 缺少出怪动作 {act}")
                            choices.append([(names[act["enemy"]], ref, act["count"]) for ref in sorted(refs)])
                        for combination in product(*choices):
                            groups.append(group_record(group["key"], combination))
                else:
                    # Endgame templates name the replaceable slots. The actual solo / co-op
                    # level owns its route indices, random packs and multiplayer branches.
                    substitutions = {v["templateEnemy"]: k for k,v in boss["placeholderActions"].items() if v} if boss else {}
                    pool = source["rounds"][str(min(13, int(number)))] if boss else [None]
                    for group in pool:
                        if group and group["weight"] <= 0:
                            continue
                        units = {u["slot"]: u["key"] for u in source["units"][group["key"]]} if group else {}
                        fixed, packs, extra = [], defaultdict(lambda: defaultdict(list)), []
                        for table, act in actions:
                            key = act["key"]
                            if key in substitutions:
                                tier, motion = substitutions[key].split("_")
                                if group["fly"] != (motion == "fly"):
                                    continue
                                key = units[{"normal":"N","elite":"E","special":"S"}[tier]]
                            assignment = (names[key], f"{table}/{act['routeIndex']}", act["count"])
                            if table == "extraRoutes":
                                extra.append(assignment)
                            elif act["randomSpawnGroupKey"]:
                                packs[act["randomSpawnGroupKey"]][act["randomSpawnGroupPackKey"]].append(assignment)
                            else:
                                fixed.append(assignment)
                        for selected in product(*(list(v.values()) + [[]] for v in packs.values())):
                            for side in ([[], extra] if extra else [[]]):
                                groups.append(group_record(group["key"] if group else "", fixed + sum(selected, []) + side))
                levels[level_id] = {"maps": board_data, "groups": groups}
        modes[definition["name"]] = {"maps": valid_maps, "inactive": definition["inactiveEnemyKey"] or [], "rounds": rounds}
    return {"source": {"spawns": "https://www.lubiao.wiki/tools/garrison-spawn-preview",
                       "generated": source["meta"]["generated"], "bundle": bundle.name, "gameDataRevision": revision},
            "modes": modes, "levels": levels, "paths": shapes, "grids": grids}


def group_record(key, assignments):
    units = defaultdict(lambda: defaultdict(int))
    for name, ref, count in assignments:
        units[name][ref] += count
    return {"key": key, "units": units}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path, help="路标档案馆出怪预览页面的公开 JS 数据块")
    parser.add_argument("gamedata", type=Path, help="ArknightsGameData 的 zh_CN/gamedata")
    parser.add_argument("revision", help="输入的 ArknightsGameData 提交")
    args = parser.parse_args()
    output = Path(__file__).with_name("garrison_round_routes.json")
    output.write_text(json.dumps(generate(args.bundle, args.gamedata, args.revision), ensure_ascii=False,
                                 separators=(",", ":")) + "\n", encoding="utf-8")
