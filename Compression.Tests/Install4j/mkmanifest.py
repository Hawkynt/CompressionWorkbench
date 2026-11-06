"""Records the data archives of every installer in test-corpus/install4j, unpacked by liblzma.

Usage: python -I mkmanifest.py <corpus> <out.tsv>
For each installer: the archives after the launcher block (located as Install4jReader locates
them), each LZMA one unpacked by Python's lzma module (liblzma), and the unpacked ZIP checked
member by member against its CRC-32 by Python's zipfile. Each row: SHA-256 of the installer, the
archive name, its unpacked size and SHA-256, and the number of ZIP members that passed. A
ZIP with a member that fails its CRC stops the script.
"""
import hashlib, io, lzma, os, struct, sys, zipfile

corpus, out = sys.argv[1], sys.argv[2]


def overlay(d):
    pe = struct.unpack_from('<I', d, 0x3C)[0]
    n = struct.unpack_from('<H', d, pe + 6)[0]
    opt = struct.unpack_from('<H', d, pe + 20)[0]
    end = max(struct.unpack_from('<I', d, pe + 24 + opt + 40 * i + 20)[0] + struct.unpack_from('<I', d, pe + 24 + opt + 40 * i + 16)[0] for i in range(n))
    magic = struct.unpack_from('<H', d, pe + 24)[0]
    cert = struct.unpack_from('<I', d, pe + 24 + (96 if magic == 0x10B else 112) + 32)[0]
    return end, cert if end < cert <= len(d) else len(d)


def table(d, p, limit):
    count = struct.unpack_from('>I', d, p)[0]
    if not 0 < count <= 100:
        return None
    p += 4
    items = []
    for _ in range(count):
        n = struct.unpack_from('>H', d, p)[0]
        name = d[p + 2:p + 2 + n].decode('utf-8')
        size = struct.unpack_from('>I', d, p + 2 + n + 4)[0]
        p += 2 + n + 8
        items.append((name, size))
    if p + sum(s for _, s in items) > limit or limit - (p + sum(s for _, s in items)) > 65536:
        return None
    return p, items


rows = ["# installer-sha256\tarchive\tsize\tsha256\tzip-members-crc-ok -- unpacked by liblzma, checked by zipfile"]
for name in sorted(os.listdir(corpus)):
    data = open(os.path.join(corpus, name), 'rb').read()
    digest = hashlib.sha256(data).hexdigest()
    start, limit = overlay(data)
    assert data[start:start + 4] == b'\xd5\x13\xe4\xe8', name
    at = start + 4
    while True:
        at = data.index(b'\xe8\xe4\x13\xd5', at)
        parsed = table(data, at + 4, limit)
        if parsed:
            break
        at += 1
    p, items = parsed
    launcher = data[start + 4:at]
    rows.append(f"{digest}\tlauncher.dat\t{len(launcher)}\t{hashlib.sha256(launcher).hexdigest()}\t-")
    for archive, size in items:
        blob = data[p:p + size]
        p += size
        content = blob if blob[:2] == b'PK' else lzma.LZMADecompressor(format=lzma.FORMAT_ALONE).decompress(blob)
        with zipfile.ZipFile(io.BytesIO(content)) as z:
            bad = z.testzip()
            assert bad is None, f"{name}/{archive}: {bad} fails its CRC"
            members = len(z.infolist())
        rows.append(f"{digest}\t{archive}\t{len(content)}\t{hashlib.sha256(content).hexdigest()}\t{members}")
with open(out, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(rows) + '\n')
