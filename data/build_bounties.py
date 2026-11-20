"""Export the current season's bounty and tactical-training choices from PRTS game data."""
import argparse
import json
import re
from pathlib import Path

from fetch_spdatabase import strip_rich_text


def build(activity, handbook, enemy_db):
    # The visible handbook form supplies the common mechanism for names with multiple variants.
    enemies = {enemy["name"].strip(): enemy for enemy in handbook["enemyData"].values()
               if not enemy["hideInHandbook"]}
    enemies["无人机护障·P"] = enemies["护障·P"]
    attributes = {enemy["Key"]: enemy["Value"][0]["enemyData"] for enemy in enemy_db["enemies"]}
    season = activity["activity"]["AUTOCHESS"]["act3autochess"]["V3_1"]
    rows = []
    for effect in season["effectInfoDataDict"].values():
        if effect["effectType"] != "ENEMY_GAIN":
            continue
        description = strip_rich_text(effect["effectDesc"])
        match = re.search(r"添加\d+[只个](.+?)(?:（|，(?:将其|若|但不|保护|使|其|每|击倒))", description)
        if not match:
            raise ValueError(f"无法识别悬赏目标：{description}")
        title = match[1]
        enemy = enemies[title]
        enemy_id = enemy["enemyId"]
        stats = attributes[enemy_id]
        mechanism = [strip_rich_text(item["text"]).strip() for item in enemy["abilityList"]]
        reach = {"MELEE": "近战", "RANGED": "远程"}.get(stats["applyWay"]["m_value"], "")
        damage = "、".join({"PHYSIC": "物理", "MAGIC": "法术", "TRUE": "真实"}[t] for t in (enemy["damageType"] or [])
                          if t in ("PHYSIC", "MAGIC", "TRUE"))
        if damage:
            mechanism.append(f"{reach}{damage}攻击")
        if not mechanism:
            mechanism.append(enemy["description"].strip())

        subtitle = effect["effectName"]
        tag = subtitle.removeprefix(title + "·").split(" · 假想敌")[0]
        if tag == "悬赏":
            tag = "普通悬赏"
        if "下场" in description:
            rounds = 1
        elif "两场" in description:
            rounds = 2
        elif "每场" in description:
            rounds = -1
        else:
            raise ValueError(f"未知场次：{description}")
        if "击倒" in description and "获得" in description:
            condition = "击杀"
        elif "完美作战" in description:
            condition = "完美"
        elif "不获得资金" in description:
            condition = ""
        else:
            raise ValueError(f"未知奖励条件：{description}")
        reward = re.search(r"获得(\d+)资金", description)
        if condition and not reward:
            raise ValueError(f"未知金额：{description}")
        rows.append(dict(id=effect["effectId"], title=title, subtitle=subtitle, text=description,
                         tag=tag, amount=int(reward[1]) if reward else 0, rounds=rounds, condition=condition,
                         icon=f"icons/enemies/{enemy_id}.png", mechanism="\n".join(mechanism)))
    return {"source": "https://torappu.prts.wiki/gamedata/latest/excel/activity_table.json",
            "season": "act3autochess/V3_1", "entries": rows}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("activity_table", type=Path)
    parser.add_argument("enemy_handbook", type=Path)
    parser.add_argument("enemy_database", type=Path)
    args = parser.parse_args()
    output = Path(__file__).with_name("bounties.json")
    data = build(*(json.loads(path.read_bytes()) for path in
                   (args.activity_table, args.enemy_handbook, args.enemy_database)))
    output.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

