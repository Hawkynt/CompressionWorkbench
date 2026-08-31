#!/usr/bin/env python3
from __future__ import annotations

import re
from pathlib import Path

DOCS: dict[str, list[tuple[str, str]]] = {
    "HashSizeRange": [
        ("MinimumBits", "The smallest supported hash-output size, in bits."),
        ("MaximumBits", "The largest supported hash-output size, in bits."),
        ("StepBits", "The increment, in bits, between supported sizes."),
    ],
    "ChecksumSizeRange": [
        ("MinimumBits", "The smallest supported checksum-output size, in bits."),
        ("MaximumBits", "The largest supported checksum-output size, in bits."),
        ("StepBits", "The increment, in bits, between supported sizes."),
    ],
    "CrcParameters": [
        ("Width", "The CRC width, in bits."),
        ("Polynomial", "The normal-form CRC polynomial."),
        ("InitialValue", "The initial CRC register value."),
        ("ReflectInput", "Whether each input byte is reflected before processing."),
        ("ReflectOutput", "Whether the CRC register is reflected before the final XOR."),
        ("FinalXor", "The value XORed with the CRC register to produce the final checksum."),
    ],
    "Crc128Parameters": [
        ("Polynomial", "The normal-form 128-bit CRC polynomial."),
        ("InitialValue", "The initial 128-bit CRC register value."),
        ("ReflectInput", "Whether each input byte is reflected before processing."),
        ("ReflectOutput", "Whether the CRC register is reflected before the final XOR."),
        ("FinalXor", "The value XORed with the CRC register to produce the final checksum."),
    ],
    "IsoIec7064Modulus": [
        ("Modulus", "The modulus used by the ISO/IEC 7064 system."),
        ("Radix", "The radix used by the ISO/IEC 7064 system."),
    ],
}

roots = [Path("Hawkynt.Algorithms.Hashing"), Path("Hawkynt.Algorithms.Checksums")]
for type_name, parameters in DOCS.items():
    pattern = re.compile(
        rf"(?m)^(?P<indent>[ \t]*)public[ \t]+(?:(?:readonly|partial)[ \t]+)*record[ \t]+struct[ \t]+{re.escape(type_name)}\s*\("
    )
    hits: list[tuple[Path, re.Match[str]]] = []
    for root in roots:
        for path in root.rglob("*.cs"):
            if any(part in {"bin", "obj"} for part in path.parts):
                continue
            text = path.read_text(encoding="utf-8")
            match = pattern.search(text)
            if match:
                hits.append((path, match))

    if len(hits) != 1:
        raise SystemExit(f"Expected exactly one positional declaration for {type_name}, found {[str(path) for path, _ in hits]}")

    path, match = hits[0]
    text = path.read_text(encoding="utf-8")
    declaration = match.group(0)
    if all(f'<param name="{name}">' in text[max(0, match.start() - 2000):match.start()] for name, _ in parameters):
        continue

    indent = match.group("indent")
    param_docs = "".join(f'{indent}/// <param name="{name}">{description}</param>\n' for name, description in parameters)
    text = text[:match.start()] + param_docs + text[match.start():]
    path.write_text(text, encoding="utf-8")
    print(f"documented primary constructor for {type_name} in {path}")
