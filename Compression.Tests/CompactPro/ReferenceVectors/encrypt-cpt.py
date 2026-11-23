#!/usr/bin/env python3
"""Encrypts every file entry of a Compact Pro archive: encrypt-cpt.py <in.cpt> <out.cpt> <password> [<only entry>]

Each encrypted entry's packed forks become: an eight-byte preamble, then the resource fork's packed
bytes, then the data fork's, all through one keyed register; the preamble is counted in the first
non-empty fork's packed size, flag bit 0 is set, the forks are laid out afresh and the directory
CRC is recomputed. The cipher is MacExtract's reading of Compact Pro's
(https://github.com/uzTools/MacExtract, 0BSD, revision 4b507c21c03377e2224c5c0f4a7eb9be69bbb30e,
src/unc_crypt.c and src/fmt_cpt.c). The preamble here is fixed so the output is reproducible.
"""
import struct
import sys
import zlib

POLY = 0xEDB88320EDB88320
TABLE = []
for i in range(256):
    r = i
    for _ in range(8):
        r = (r >> 1) ^ POLY if r & 1 else r >> 1
    TABLE.append(r)


class Cipher:
    def __init__(self, password):
        key = [0] * 9
        for i, b in enumerate(password):
            key[(i & 7) + 1] = (key[(i & 7) + 1] + b) & 0xFF
        self.reg = 0x0123456789ABCDEF
        for i in range(1, min(len(password), 8) + 1):
            self._step(key[i])

    def _step(self, plain):
        self.reg = (self.reg >> 8) ^ TABLE[(self.reg ^ plain) & 0xFF]

    def encrypt(self, data):
        out = bytearray()
        for p in data:
            out.append(p ^ ((self.reg >> 8) & 0xFF))
            self._step(p)
        return bytes(out)


def crc(data):
    return zlib.crc32(data) ^ 0xFFFFFFFF


def main():
    src, dst, password = sys.argv[1], sys.argv[2], sys.argv[3].encode("mac_roman")
    only = sys.argv[4] if len(sys.argv) > 4 else None
    raw = open(src, "rb").read()
    offset = struct.unpack(">I", raw[4:8])[0]
    d = raw[offset:]
    count = struct.unpack(">H", d[4:6])[0]
    pos = 7 + d[6]
    entries = []  # (name bytes incl. length byte, meta or folder count, is_folder)

    def walk(n):
        nonlocal pos
        while n > 0:
            ln = d[pos]
            name = d[pos:pos + 1 + (ln & 0x7F)]
            pos += 1 + (ln & 0x7F)
            if ln & 0x80:
                nested = struct.unpack(">H", d[pos:pos + 2])[0]
                entries.append((name, d[pos:pos + 2], True))
                pos += 2
                walk(nested)
                n -= nested + 1
            else:
                entries.append((name, bytearray(d[pos:pos + 45]), False))
                pos += 45
                n -= 1

    walk(count)
    out = bytearray(raw[:8])
    salt = bytes(range(0xA0, 0xA8))
    for name, meta, folder in entries:
        if folder:
            continue
        at, flags = struct.unpack(">I", meta[1:5])[0], struct.unpack(">H", meta[27:29])[0]
        rcomp, dcomp = struct.unpack(">II", meta[37:45])
        rsrc, data = raw[at:at + rcomp], raw[at + rcomp:at + rcomp + dcomp]
        label = name[1:].decode("mac_roman")
        if (only is None or label == only) and (rcomp or dcomp):
            blob = Cipher(password).encrypt(salt + rsrc + data)
            if rcomp:
                rcomp += 8
            else:
                dcomp += 8
            flags |= 1
        else:
            blob = rsrc + data
        struct.pack_into(">I", meta, 1, len(out))
        struct.pack_into(">H", meta, 27, flags)
        struct.pack_into(">II", meta, 37, rcomp, dcomp)
        out += blob
    body = bytearray(d[4:7 + d[6]])
    for name, meta, folder in entries:
        body += name + bytes(meta)
    struct.pack_into(">I", out, 4, len(out))
    out += struct.pack(">I", crc(bytes(body))) + body
    open(dst, "wb").write(out)
    print(dst, len(out), "entries", sum(1 for e in entries if not e[2]))


main()
