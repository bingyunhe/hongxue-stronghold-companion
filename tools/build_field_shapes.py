"""Compile foot-relative foreground samples from StrongholdAssistant SPRT0004.

Input revision 680869da90e1c481e387851bc3c5f4a30648acca. Coordinates use
20 samples per board cell. Keep the operator and skin identifiers separately.
"""
import argparse
import gzip
import json
import struct
from pathlib import Path

from PIL import Image


def build(source, destination):
    poses = []
    data = Path(__file__).resolve().parents[1] / "data"
    names = {op["name"] for file in ("operators.json", "custom_operators.json")
             for op in json.loads((data / file).read_text(encoding="utf-8-sig"))["operators"]}
    with gzip.open(source, "rb") as stream:
        def read(fmt):
            return struct.unpack("<" + fmt, stream.read(struct.calcsize("<" + fmt)))

        if stream.read(8) != b"SPRT0004":
            raise ValueError("Expected SPRT0004")
        _, pixels_per_unit, count = read("HfI")
        step = .00271 * 20 / pixels_per_unit
        for _ in range(count):
            name = stream.read(read("H")[0])
            skin = stream.read(read("H")[0])
            tier, width, height, ox, oy = read("HHHff")
            size = width, height
            color = Image.frombytes("RGB", size, stream.read(width * height * 3), "raw", "BGR")
            mask = Image.frombytes("L", size, stream.read(width * height))
            operator = name.decode()
            if operator == "暴躁铁皮":
                operator = "“暴躁铁皮”"
            elif operator == "Pith（优等生）" and tier == 3:
                operator = "盟约·辅助干员"
            if operator not in names:
                continue
            name = operator.encode()
            color.putalpha(mask)
            # Place every pose on the same skeleton origin before filtering. Alpha
            # premultiplication keeps floor colours outside the silhouette out.
            bounds = ((ox - 16.5 / step), (oy - 34.5 / step),
                      (ox + 16.5 / step), (oy + 4.5 / step))
            patch = color.convert("RGBa").transform((33, 39), Image.Transform.EXTENT,
                bounds, Image.Resampling.BILINEAR).convert("RGBA")
            samples = bytearray()
            for i, (r, g, b, alpha) in enumerate(patch.getdata()):
                if alpha >= 220:
                    samples.extend(struct.pack("<bbBBB", i % 33 - 16, i // 33 - 34, r, g, b))
            if len(samples) >= 5 * 32:
                poses.append(bytes([len(name)]) + name + bytes([len(skin)]) + skin
                             + struct.pack("<BH", tier, len(samples) // 5) + samples)
    destination.write_bytes(b"FSH1" + struct.pack("<H", len(poses)) + b"".join(poses))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    build(args.source, args.destination)

