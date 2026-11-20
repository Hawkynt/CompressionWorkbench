"""Fetches the large Compact Pro archives CompactProCorpusTests reads from the Info-Mac mirror at
ftp.funet.fi, BinHex-decodes them and writes each data fork (the .cpt) into the given directory.
They are not committed: redistribution terms vary from package to package.

usage: python3 -I fetch-info-mac.py <repo>/test-corpus/compactpro
"""
import os, sys, urllib.request

MIRROR = 'https://ftp.funet.fi/pub/mac/info-mac/'
FILES = [
    'edu/yng/coloring-book-32.hqx', 'game/brd/vampire-chess.hqx', 'dev/lib/sprite-anim-toolkit-238.hqx',
    'dev/lib/sprite-anim-toolkit-demos.hqx', 'game/word/mac-pc.hqx', 'edu/yng/kid-grid-11-demo.hqx',
    'game/brd/carotto-104.hqx', 'edu/yng/peekaboo-demo.hqx', 'game/com/total-war-scenarios.hqx',
    'comm/bbs/hyper-qwk-10-hc.hqx', 'dev/src/macintosh-tracker-12-c.hqx', 'dev/src/ncsa-mac-telnet-261d1-c.hqx',
    'dev/src/peek-a-boo-112-ad-c.hqx', 'info/sft/ambrosia-times-36.hqx',
]
ALPHABET = '!"#$%&\'()*+,-012345689@ABCDEFGHIJKLMNPQRSTUVXYZ[`abcdefhijklmpqr'


def binhex(text):
    """Decodes the BinHex 4.0 body after the leading ':' (6-bit characters, then 0x90 run-length)."""
    bits = count = 0
    packed = bytearray()
    for ch in text:
        if ch == ':':
            break
        value = ALPHABET.find(ch)
        if value < 0:
            continue
        bits = bits << 6 | value
        count += 6
        if count >= 8:
            count -= 8
            packed.append(bits >> count & 255)
    out = bytearray()
    i = 0
    while i < len(packed):
        if packed[i] == 0x90 and i + 1 < len(packed):
            n = packed[i + 1]
            out += b'\x90' if n == 0 else bytes([out[-1]]) * (n - 1)
            i += 2
        else:
            out.append(packed[i])
            i += 1
    return bytes(out)


def data_fork(hqx):
    start = hqx.index('(This file must be converted with BinHex')
    body = ''.join(hqx[hqx.index(':', start) + 1:].split())
    raw = binhex(body)
    n = raw[0]
    length = int.from_bytes(raw[12 + n:16 + n], 'big')
    header = 1 + n + 1 + 4 + 4 + 2 + 4 + 4 + 2
    return raw[header:header + length]


out = sys.argv[1]
os.makedirs(out, exist_ok=True)
for rel in FILES:
    text = urllib.request.urlopen(MIRROR + rel, timeout=120).read().decode('latin1')
    fork = data_fork(text)
    name = os.path.basename(rel)[:-4] + '.cpt'
    with open(os.path.join(out, name), 'wb') as f:
        f.write(fork)
    print(name, len(fork))
