#!/usr/bin/env python3
"""Regenerates the structured pseudo-archive reference vectors from the real implementations.

Every vector next to this script is the output of a third-party tool, never of
CompressionWorkbench. That is the whole point: a vector produced by our own writer and read back
by our own reader cannot see a defect the two share. Run this only to refresh a vector, and only
with the tool versions recorded in README.md.

    python generate.py                  # MessagePack, pickle, JSON and XML vectors (any platform)
    python generate.py --registry       # additionally re-export the .reg vectors (Windows only)

Perl Storable and MS-NRBF have their own generators, `generate-storable.pl` and `generate-nrbf.ps1`.

The archive payload below is the projection `StructuredArchive.FromInputs` builds for the three
inputs `dir/file.bin`, `dir/long.bin` and `top.bin`, in the ordinal order it sorts them into.
Its leaf names and payloads are pairwise distinct on purpose: CPython's pickle memo is keyed on
object identity, so a repeated leaf would make CPython emit BINGET where our tree walker cannot.
"""

import argparse
import base64
import hashlib
import json
import os
import pathlib
import pickle
import subprocess
import sys
import xml.etree.ElementTree as ElementTree

HERE = pathlib.Path(__file__).parent

FILE_BIN = bytes([0x00, 0x01, 0x7F, 0x80, 0xFF, 0x0A])
LONG_BIN = bytes(range(64))
TOP_BIN = bytes([0xDE, 0xAD, 0xBE, 0xEF])


def archive():
    inner = {}
    inner["file.bin"] = FILE_BIN
    inner["long.bin"] = LONG_BIN
    root = {}
    root["dir"] = inner
    root["top.bin"] = TOP_BIN
    return root


def document():
    """A scalar-only document, used for the pickle reader vectors on every protocol.

    Protocols 0-2 encode `bytes` as a `_codecs.encode` GLOBAL/REDUCE pair rather than natively, so
    a bytes payload would project differently per protocol; strings and integers do not.
    """
    return {"dir": {"count": 7, "name": "value"}}


def write(name, data):
    (HERE / name).write_bytes(data)
    print("%-42s %7d bytes  sha256=%s" % (name, len(data), hashlib.sha256(data).hexdigest()))


# --------------------------------------------------------------------------------------- MessagePack

def messagepack_types(msgpack):
    """Every family in the MessagePack specification that our reader names a type for.

    `packb` always picks the shortest encoding, so each entry is sized to land on the format byte
    it is there to pin -- 255 on uint8, 256 on uint16, and so on down the width ladder.
    """
    return {
        "nil": None,
        "true": True,
        "false": False,
        "positive-fixint-min": 0,
        "positive-fixint-max": 127,
        "negative-fixint-min": -32,
        "negative-fixint-max": -1,
        "uint8": 255,
        "uint16": 65535,
        "uint32": 4294967295,
        "uint64": 18446744073709551615,
        "int8": -128,
        "int16": -32768,
        "int32": -2147483648,
        "int64": -9223372036854775808,
        "float64": 1.5,
        "fixstr": "abc",
        "str8": "s" * 40,
        "str16": "m" * 300,
        "utf8": "ä中\U0001f600",
        "bin8": bytes([0x00, 0xFF, 0x10]),
        "bin16": bytes(range(256)) + bytes(range(44)),
        "fixarray": [1, 2, 3],
        "array16": list(range(20)),
        "fixmap": {"inner": 1},
        "nested": {"list": [{"leaf": b"\x01\x02"}]},
        "fixext1": msgpack.ExtType(5, b"\x2a"),
        "fixext4": msgpack.ExtType(3, b"\x01\x02\x03\x04"),
        # The only negative extension code the specification assigns: timestamp, type -1.
        "timestamp": msgpack.Timestamp(1234567890, 123456789),
        "ext8": msgpack.ExtType(7, bytes(range(20))),
        1: "integer-key",
    }


def generate_messagepack(msgpack):
    write("messagepack-archive.msgpack", msgpack.packb(archive(), use_bin_type=True))
    write("messagepack-types.msgpack", msgpack.packb(messagepack_types(msgpack), use_bin_type=True))
    # `packb` never chooses float32 on its own; this is the only switch that produces one.
    write("messagepack-float32.msgpack", msgpack.packb({"float32": 1.5}, use_bin_type=True, use_single_float=True))


# -------------------------------------------------------------------------------------------- pickle

def generate_pickle():
    write("pickle-protocol4-archive.pickle", pickle.dumps(archive(), protocol=4))
    for protocol in range(0, pickle.HIGHEST_PROTOCOL + 1):
        write("pickle-protocol%d-document.pickle" % protocol, pickle.dumps(document(), protocol=protocol))


# ---------------------------------------------------------------------------------------- JSON / XML

def json_envelope(payload):
    """The `$cwb:` binary envelope our JSON projection wraps a file's bytes in."""
    return {"$cwb:type": "binary", "$cwb:version": 1, "$cwb:data": base64.b64encode(payload).decode("ascii")}


def generate_json():
    # Written by CPython's json module, not by ours: this is the "we read what they write" half.
    text = json.dumps(
        {"dir": {"count": 7, "name": "value", "ratio": 1.5, "flag": True, "missing": None, "list": [1, "two"]}},
        indent=2,
    )
    write("json-document.json", text.encode("utf-8"))

    # And the other half: `json.dumps(..., indent=2)` and `Utf8JsonWriter { Indented = true }`
    # agree on every layout decision -- two spaces, `": "` after a name, no space before a comma --
    # so our writer is comparable against CPython's byte for byte once its newline is pinned to LF.
    archive_json = json.dumps(
        {
            "dir": {"file.bin": json_envelope(FILE_BIN), "long.bin": json_envelope(LONG_BIN)},
            "top.bin": json_envelope(TOP_BIN),
        },
        indent=2,
    )
    write("json-archive.json", archive_json.encode("utf-8"))


CWB_NAMESPACE = "urn:hawkynt:compressionworkbench:structured-archive:1"


def generate_xml():
    root = ElementTree.Element("root", {"id": "7"})
    ElementTree.SubElement(root, "item").text = "A"
    ElementTree.SubElement(root, "item").text = "B"
    ElementTree.SubElement(ElementTree.SubElement(root, "nested"), "leaf").text = "deep"
    write("xml-document.xml", ElementTree.tostring(root, encoding="utf-8", xml_declaration=True))

    # The same archive envelope our writer emits, rendered by ElementTree instead: a different
    # namespace prefix, a single-quoted declaration, the namespace attribute ahead of `version`.
    # Our reader has to cope with a third party's rendering of the envelope, not only with
    # XmlWriter's. No writer parity vector is possible here -- see README.md.
    # Deliberately not the "cwb" prefix our own writer uses. An XML name is its namespace plus its
    # local name; the prefix is a spelling. A reader that quietly grew to match on the prefix reads
    # its sibling writer's output perfectly and nothing else, and only this vector notices.
    ElementTree.register_namespace("arc", CWB_NAMESPACE)
    qualified = lambda name: "{%s}%s" % (CWB_NAMESPACE, name)  # noqa: E731
    archive_root = ElementTree.Element(qualified("archive"), {"version": "1"})
    directory = ElementTree.SubElement(archive_root, qualified("directory"), {"name": "dir"})
    for name, payload in (("file.bin", FILE_BIN), ("long.bin", LONG_BIN)):
        leaf = ElementTree.SubElement(directory, qualified("file"), {"name": name, "encoding": "base64"})
        leaf.text = base64.b64encode(payload).decode("ascii")
    top = ElementTree.SubElement(archive_root, qualified("file"), {"name": "top.bin", "encoding": "base64"})
    top.text = base64.b64encode(TOP_BIN).decode("ascii")
    ElementTree.indent(archive_root, space="  ")
    write("xml-archive-elementtree.xml", ElementTree.tostring(archive_root, encoding="utf-8", xml_declaration=True))


# -------------------------------------------------------------------------------------------- .reg

REGISTRY_KEY = r"HKCU\Software\CompressionWorkbench\PseudoArchive"
REGISTRY_TYPES_KEY = r"HKCU\Software\CompressionWorkbench\ReferenceTypes"


def _reg(*arguments):
    subprocess.run(("reg",) + arguments, check=True, stdout=subprocess.DEVNULL)


def _reg_quiet(*arguments):
    subprocess.run(("reg",) + arguments, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def generate_registry():
    if os.name != "nt":
        sys.exit("--registry needs Windows reg.exe")

    archive_path = HERE / "registry-export.reg"
    _reg_quiet("delete", REGISTRY_KEY, "/f")
    try:
        # `reg add <key> /f` on its own would also create an empty default value, which the export
        # then renders as a leading `@=""` line that our projection has no member for. Creating the
        # key implicitly, through its first value, is what leaves the key default-less.
        _reg("add", REGISTRY_KEY, "/v", "top.bin", "/t", "REG_BINARY", "/d", TOP_BIN.hex(), "/f")
        _reg("add", REGISTRY_KEY + r"\dir", "/v", "file.bin", "/t", "REG_BINARY", "/d", FILE_BIN.hex(), "/f")
        _reg("add", REGISTRY_KEY + r"\dir", "/v", "long.bin", "/t", "REG_BINARY", "/d", LONG_BIN.hex(), "/f")
        _reg("export", REGISTRY_KEY, str(archive_path), "/y")
    finally:
        _reg_quiet("delete", REGISTRY_KEY, "/f")
    write("registry-export.reg", archive_path.read_bytes())

    types_path = HERE / "registry-types.reg"
    _reg_quiet("delete", REGISTRY_TYPES_KEY, "/f")
    try:
        add = (REGISTRY_TYPES_KEY,)
        _reg("add", *add, "/v", "Sz", "/t", "REG_SZ", "/d", "plain text", "/f")
        _reg("add", *add, "/v", "SzEscapes", "/t", "REG_SZ", "/d", r'quote " and backslash \ inside', "/f")
        _reg("add", *add, "/v", "ExpandSz", "/t", "REG_EXPAND_SZ", "/d", "%SystemRoot%\\system32", "/f")
        # `reg add` splits REG_MULTI_SZ on the separator given to /s; each element becomes its own
        # NUL-terminated run inside the hex(7) payload.
        _reg("add", *add, "/v", "MultiSz", "/t", "REG_MULTI_SZ", "/s", "|", "/d", "alpha|beta|gamma", "/f")
        _reg("add", *add, "/v", "Dword", "/t", "REG_DWORD", "/d", "42", "/f")
        _reg("add", *add, "/v", "DwordHigh", "/t", "REG_DWORD", "/d", "4294967295", "/f")
        _reg("add", *add, "/v", "Qword", "/t", "REG_QWORD", "/d", "1234605616436508552", "/f")
        _reg("add", *add, "/v", "Binary", "/t", "REG_BINARY", "/d", "00ff10", "/f")
        # 200 bytes forces `reg export` across several continuation lines, which is the part of the
        # grammar a hand-written parser is most likely to get wrong.
        _reg("add", *add, "/v", "BinaryWrapped", "/t", "REG_BINARY", "/d", bytes((i * 7) & 0xFF for i in range(200)).hex(), "/f")
        _reg("add", REGISTRY_TYPES_KEY + r"\sub", "/v", "Nested", "/t", "REG_SZ", "/d", "child", "/f")
        _reg("export", REGISTRY_TYPES_KEY, str(types_path), "/y")
    finally:
        _reg_quiet("delete", REGISTRY_TYPES_KEY, "/f")
    write("registry-types.reg", types_path.read_bytes())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--registry", action="store_true", help="re-export the .reg vectors (Windows only)")
    args = parser.parse_args()

    import msgpack  # noqa: PLC0415 - only needed when refreshing the MessagePack vectors

    print("python   %s" % sys.version.split()[0])
    print("msgpack  %s" % ".".join(str(x) for x in msgpack.version))
    print()

    generate_messagepack(msgpack)
    generate_pickle()
    generate_json()
    generate_xml()
    if args.registry:
        generate_registry()


if __name__ == "__main__":
    main()
