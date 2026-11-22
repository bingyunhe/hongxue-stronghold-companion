"""Prepare a stable, verifiable MirrorChyan tree from the official Windows ZIP."""
import argparse
import hashlib
import json
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile


def prepare(official, output, tag):
    prefix = f"hongxue-stronghold-companion-{tag}-win-x64/"
    files = {}
    directories = []
    with ZipFile(official) as source, ZipFile(output, "x", ZIP_DEFLATED) as mirror:
        for entry in source.infolist():
            if not entry.filename.startswith(prefix):
                raise ValueError(f"Unexpected official archive root: {entry.filename}")
            name = entry.filename[len(prefix):]
            if not name:
                continue
            if name.lower() in {"changes.json", "mirror-files.json"}:
                raise ValueError(f"Reserved MirrorChyan filename: {name}")
            if entry.is_dir():
                directories.append(name.rstrip("/"))
                mirror.writestr(name, b"")
                continue
            if name in files:
                raise ValueError(f"Duplicate package file: {name}")
            data = source.read(entry)
            files[name] = hashlib.sha256(data).hexdigest()
            mirror.writestr(name, data)
        if "update-layout.json" not in files or "app/HongxueCompanion.exe" not in files:
            raise ValueError("The portable Windows package is incomplete")
        inventory = {"Version": tag, "Files": files, "Directories": sorted(directories)}
        mirror.writestr("mirror-files.json", json.dumps(inventory, ensure_ascii=False, sort_keys=True).encode("utf-8"))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("official", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("tag")
    args = parser.parse_args()
    prepare(args.official, args.output, args.tag)
