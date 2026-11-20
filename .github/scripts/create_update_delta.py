"""Build an adjacent-version Windows update from two official portable ZIPs."""
import argparse
import ctypes as ct
import hashlib
import json
import zipfile
import zlib
from ctypes import wintypes
from pathlib import Path


class Input(ct.Structure):
    _fields_ = [("data", ct.c_void_p), ("size", ct.c_size_t), ("editable", wintypes.BOOL)]


class Output(ct.Structure):
    _fields_ = [("data", ct.c_void_p), ("size", ct.c_size_t)]


def encode(old, new):
    api = ct.WinDLL("msdelta", use_last_error=True)
    api.CreateDeltaB.argtypes = [ct.c_int64] * 3 + [Input] * 5 + [ct.c_void_p, wintypes.ULONG, ct.POINTER(Output)]
    api.DeltaFree.argtypes = [ct.c_void_p]
    inputs = [Input(ct.cast(ct.c_char_p(b), ct.c_void_p), len(b), False) for b in (old, new)]
    result = Output()
    if not api.CreateDeltaB(1, 0, 0, *inputs, Input(), Input(), Input(), None, 0, ct.byref(result)):
        raise ct.WinError(ct.get_last_error())
    try:
        return ct.string_at(result.data, result.size)
    finally:
        api.DeltaFree(result.data)


def index(archive, package):
    prefix = package.stem + "/"
    files = {}
    for item in archive.infolist():
        if item.is_dir():
            continue
        path = item.filename.replace(chr(92), "/")
        if not path.startswith(prefix):
            raise ValueError(f"Unexpected package root: {path}")
        files[path[len(prefix):]] = item
    return files


def build(old, new, directory):
    versions = [p.stem.removeprefix("hongxue-stronghold-companion-v").removesuffix("-win-x64") for p in (old, new)]
    dest = directory / f"delta-v{versions[0]}-to-v{versions[1]}.zip"
    with zipfile.ZipFile(old) as base, zipfile.ZipFile(new) as target, zipfile.ZipFile(dest, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as patch:
        seen = index(base, old)
        hashes = {name: hashlib.sha256(base.read(item)).hexdigest() for name, item in seen.items()}
        entries = {}
        for name, item in index(target, new).items():
            data = target.read(item)
            digest = hashlib.sha256(data).hexdigest()
            mode = "copy"
            if hashes.get(name) != digest:
                mode = "file"
                if name in seen:
                    diff = encode(base.read(seen[name]), data)
                    if len(diff) < len(zlib.compress(data, 6)) - 6:
                        mode = "delta"
                        data = diff
                patch.writestr("payload/" + name, data, compress_type=zipfile.ZIP_STORED if mode == "delta" else zipfile.ZIP_DEFLATED)
            entries[name] = {"Mode": mode, "Hash": digest}
        patch.writestr("plan.json", json.dumps({"Format": 1, "Base": versions[0], "Target": versions[1], "Old": hashes, "Files": entries}, separators=(",", ":")))
    print(dest.name, dest.stat().st_size, hashlib.sha256(dest.read_bytes()).hexdigest())


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for arg in ("old", "new", "directory"):
        parser.add_argument(arg, type=Path)
    args = parser.parse_args()
    build(args.old, args.new, args.directory)
