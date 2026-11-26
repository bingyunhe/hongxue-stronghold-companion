"""Build local skill descriptions and compact RGB icon references for the custom roster."""
import argparse
import base64
import json
from pathlib import Path

from PIL import Image
from build_operator_profiles import skill_of


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tables", type=Path)
    parser.add_argument("icons", type=Path)
    args = parser.parse_args()
    data = Path(__file__).resolve().parent
    def read(name):
        return json.loads((args.tables / (name + ".json")).read_text(encoding="utf-8-sig"))
    season = read("activity_table")["activity"]["AUTOCHESS"]["act3autochess"]["V3_1"]
    chess = season["charChessDataDict"]
    levels = {(chess[key]["status"]["skillLevel"], chess[chess[key]["upgradeChessId"]]["status"]["skillLevel"])
              for key in season["diyChessDict"]}
    normal, elite = levels.pop()
    if levels:
        raise ValueError("Custom slots use different skill levels")
    chars, skills = read("character_table"), read("skill_table")
    result = {}
    for op in json.loads((data / "custom_operators.json").read_text(encoding="utf-8"))["operators"]:
        char = chars[op["sourceCharId"]]
        options = []
        for index, entry in enumerate(char["skills"]):
            sid = entry["skillId"]
            icon = skills[sid]["iconId"] or sid
            with Image.open(args.icons / (icon + ".png")) as image:
                rgba = image.convert("RGBA")
                canvas = Image.new("RGB", rgba.size)
                canvas.paste(rgba, mask=rgba.getchannel("A"))
                signature = canvas.resize((12, 12), Image.Resampling.BOX).tobytes()
            options.append({"id": sid, "normal": skill_of(char, index, normal, skills),
                            "elite": skill_of(char, index, elite, skills),
                            "signature": base64.b64encode(signature).decode()})
        result[op["name"]] = options
    (data / "custom_skills.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
