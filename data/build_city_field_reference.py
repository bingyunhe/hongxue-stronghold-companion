"""Build city/expanded references from round six, its marked A6, and the two-Leto round-four capture."""
import base64
import json
import sys
from pathlib import Path

from PIL import Image, ImageStat

from build_garrison_map_refs import DATA_DIR, homography


def build(game_frame, top_row_frame, double_leto_frame):
    maps = json.loads((DATA_DIR / "garrison_maps.json").read_bytes())
    city = next(entry for entry in maps["maps"] if entry["id"] == "city")
    camera = homography(maps["cameras"]["expanded"])
    # Floor intersections bordering the central three columns in the 320 × 166 map thumbnail.
    reference = homography([((2, 0), (142, 40)), ((5, 0), (202, 40)),
                            ((2, 4), (135, 113.5)), ((5, 4), (208, 113.5))])
    thumb = Image.open(DATA_DIR.parent / "src/ASPAssistant.App/Resources/MapThumbs/city.jpg").convert("RGB")
    frames = [Image.open(path).convert("RGB") for path in (game_frame, top_row_frame)]
    empty, looks, top_looks = {}, [], []
    labelled = {"C4": ("陨星", 1), "B5": ("地灵", 1), "A6": ("烈夏", 1),
                "C5": ("卡达", 1), "B7": ("奥尔佳", 3)}
    for row, cells in enumerate(city["cells"][:4]):
        for col, terrain in enumerate(cells, maps["columns"][0]):
            if terrain not in "GHT":
                continue
            code = f"{chr(65 + row)}{col + 3}"
            tx, ty = reference(col + .5, row + .5)
            scale = reference(col + 1.5, row + .5)[0] - tx
            empty[code] = []
            for dy in [-6, -13, -20, -30, -39, -48, -57, -66]:
                for dx in [-22, -13, -4, 5, 14, 23]:
                    rgb = thumb.getpixel((round(tx + dx * scale / 100),
                                          round(ty + (dy - (18 if terrain == "H" else 0)) * scale / 100)))
                    empty[code].append(int.from_bytes(bytes(rgb), "big"))
            if code not in labelled:
                continue
            frame = frames[1 if row == 0 else 0]
            x, y = camera(col + .5, row + .5)
            tile = (camera(col + 1.5, row + .5)[0] - x) * frame.width
            samples = []
            for dy in range(-50 if row == 0 else -82, -16, 8):
                for dx in range(-20, 18, 7):
                    px, py = round(x * frame.width + dx * tile / 100), round(y * frame.height + dy * tile / 100)
                    near = [frame.getpixel((px + i, py + j)) for i, j in [(-1, 0), (1, 0), (0, -1), (0, 1)]]
                    colour = [round(sum(channel) / 4) for channel in zip(*near)]
                    bg = thumb.getpixel((round(tx + dx * scale / 100), round(ty + dy * scale / 100)))
                    if sum(abs(a - b) for a, b in zip(colour, bg)) > 120:
                        samples.append([dx, dy, *colour])
            name, tier = labelled[code]
            (top_looks if row == 0 else looks).append({"Name": name, "Tier": tier, "Samples": samples})
    # B7's torso is hidden by Ironhide. The other copy in D5 has a clear
    # hairline above the face even when the warm in-game highlight is active.
    with Image.open(double_leto_frame) as capture:
        leto_frame = capture.convert("RGB")
    for col, row, mirror, highlight in [(4, 1, True, False), (2, 3, False, True)]:
        x, y = camera(col + .5, row + .5)
        tile = (camera(col + 1.5, row + .5)[0] - x) * leto_frame.width
        samples = []
        for dy in range(-79, -58 if highlight else -44, 5):
            for dx in range(-16, 12, 4):
                px = round(x * leto_frame.width + dx * tile / 100)
                py = round(y * leto_frame.height + dy * tile / 100)
                patch = leto_frame.crop((px - 3, py - 3, px + 4, py + 4))
                if sum(ImageStat.Stat(patch).stddev) < 39:
                    continue
                near = [leto_frame.getpixel((px + i, py + j)) for i, j in [(-1, 0), (1, 0), (0, -1), (0, 1)]]
                colour = [sum(channel) // 4 for channel in zip(*near)]
                if highlight:
                    colour = [round(v + (255 - v) * gain) for v, gain in zip(colour, (.17, .14, .065))]
                samples.append([-dx if mirror else dx, dy, *colour])
        looks.append({"Name": "烈夏", "Tier": 1, "Samples": samples})
    # Same 7x7 head grid for every identity. These references are used only
    # to order the manual picker, independently of the automatic matchers.
    review_heads = {}
    for frame, col, row, name in [
        (frames[1], 3, 0, "烈夏"), (frames[1], 4, 1, "奥尔佳"),
        (frames[1], 2, 1, "地灵"), (frames[1], 1, 2, "陨星"),
        (frames[1], 2, 2, "卡达"), (frames[1], 4, 2, "“暴躁铁皮”"),
        (leto_frame, 3, 0, "灰喉"), (leto_frame, 4, 0, "折桠"),
    ]:
        x, y = camera(col + .5, row + .5)
        tile = (camera(col + 1.5, row + .5)[0] - x) * frame.width
        rgb = bytearray()
        for dy in range(-79, -44, 5):
            for dx in range(-16, 12, 4):
                px = round(x * frame.width + dx * tile / 100)
                py = round(y * frame.height + dy * tile / 100)
                near = [frame.getpixel((px + i, py + j)) for i, j in [(-1, 0), (1, 0), (0, -1), (0, 1)]]
                rgb.extend(sum(channel) // 4 for channel in zip(*near))
        review_heads[name] = base64.b64encode(rgb).decode("ascii")
    return {"Empty": empty, "Looks": looks, "TopRowLooks": top_looks, "ReviewHeads": review_heads}


if __name__ == "__main__":
    output = DATA_DIR / "template/city-expanded-field.json"
    output.write_text(json.dumps(build(*sys.argv[1:]), ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
