"""Reduce StrongholdAssistant SPRT0004 masked poses to the live board sample format.

Input: data/sprite-templates.bin.gz from StrongholdAssistant commit 16fea906.
Coordinates are relative to the sprite foot in a 100-pixel board cell.
"""

import argparse
import gzip
import struct
from pathlib import Path

from PIL import Image, ImageFilter


def build(source: Path, destination: Path):
    poses = []
    with gzip.open(source, "rb") as stream:
        def read(fmt):
            return struct.unpack("<" + fmt, stream.read(struct.calcsize("<" + fmt)))

        if stream.read(8) != b"SPRT0004":
            raise ValueError("Expected SPRT0004")
        _, pixels_per_unit, count = read("HfI")
        for _ in range(count):
            name = stream.read(read("H")[0])
            stream.read(read("H")[0])  # All skins are eligible for the same operator.
            tier, width, height, origin_x, origin_y = read("HHHff")
            size = width, height
            color = Image.frombytes("RGB", size, stream.read(width * height * 3), "raw", "BGR")
            color = color.filter(ImageFilter.BoxBlur(1))
            mask = Image.frombytes("L", size, stream.read(width * height)).filter(ImageFilter.MinFilter(3))
            samples = []
            for row in range(8):
                for col in range(7):
                    x = int((col + .5) * width / 7)
                    y = int((row + .5) * height / 8)
                    dx = round((x - origin_x) * .271 / pixels_per_unit)
                    dy = round((y - origin_y) * .271 / pixels_per_unit)
                    if mask.getpixel((x, y)) > 150 and -72 < dx < 72 and -124 < dy < 14:
                        samples.append(struct.pack("bbBBB", dx, dy, *color.getpixel((x, y))))
            if len(samples) >= 8:
                poses.append(bytes([len(name)]) + name + bytes([tier, len(samples)]) + b"".join(samples))
    destination.write_bytes(struct.pack("<IH", 0x31465348, len(poses)) + b"".join(poses))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    build(args.source, args.destination)
