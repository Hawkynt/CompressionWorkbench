"""Prints the setup data id and the SHA-256 of both setup-0 blocks (decompressed) of an Inno Setup
6.0 - 7.1.1 installer: the values InnoSetupWriterVersionTests compares our headers against.

usage: python iscc-headers.py setup.exe
"""
import hashlib
import lzma
import re
import struct
import sys
import zlib


def lzma1(data):
    props = data[0]
    lc = props % 9
    props //= 9
    lp, pb = props % 5, props // 5
    size = struct.unpack('<I', data[1:5])[0]
    flt = {'id': lzma.FILTER_LZMA1, 'lc': lc, 'lp': lp, 'pb': pb, 'dict_size': size}
    return lzma.LZMADecompressor(lzma.FORMAT_RAW, filters=[flt]).decompress(data[5:])


def block(f, pos, wide):
    """A compressed block: header CRC, stored size (32-bit, 64-bit from 6.7), flag, CRC'd 4 KiB pieces."""
    n = 13 if wide else 9
    assert zlib.crc32(f[pos + 4:pos + n]) == struct.unpack('<I', f[pos:pos + 4])[0], 'block header CRC'
    stored = struct.unpack('<Q' if wide else '<I', f[pos + 4:pos + n - 1])[0]
    compressed = f[pos + n - 1]
    p, end, raw = pos + n, pos + n + stored, bytearray()
    while p < end:
        k = min(4096, end - p - 4)
        assert zlib.crc32(f[p + 4:p + 4 + k]) == struct.unpack('<I', f[p:p + 4])[0], 'piece CRC'
        raw += f[p + 4:p + 4 + k]
        p += 4 + k
    return (lzma1(bytes(raw)) if compressed else bytes(raw)), end


def main():
    f = open(sys.argv[1], 'rb').read()
    i = f.find(b'rDlPtS\xcd\xe6\xd7{\x0b*')
    version = struct.unpack('<I', f[i + 12:i + 16])[0]
    off0 = struct.unpack('<I', f[i + 32:i + 36])[0] if version == 1 else struct.unpack('<q', f[i + 40:i + 48])[0]
    setup_id = f[off0:off0 + 64].rstrip(b'\0').decode()
    v = tuple(int(x) for x in re.search(r'\(([\d.]+)\)', setup_id).group(1).split('.'))
    p = off0 + 64 + (4 + 49 if v >= (6, 5, 0) else 0)  # 6.5+: CRC and encryption header
    wide = v >= (6, 7, 0)
    b1, p = block(f, p, wide)
    b2, p = block(f, p, wide)
    print(setup_id, hashlib.sha256(b1).hexdigest(), hashlib.sha256(b2).hexdigest())


main()
