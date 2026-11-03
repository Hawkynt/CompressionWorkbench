"""Builds the FreeArc 0.67 archives that exercise the filter, tor, ppmd, grzip, lzp, dict and encryption stages.

Usage: python -I mkfilters.py <Arc.exe> <workdir> <outdir>
The inputs are generated deterministically; the archives and filters.tsv (name, size, SHA-256
of each file as `Arc.exe x` extracts it) are checked in.
"""
import hashlib, os, random, shutil, struct, subprocess, sys

arc, work, out = sys.argv[1], os.path.abspath(sys.argv[2]), os.path.abspath(sys.argv[3])
shutil.rmtree(work, ignore_errors=True)
src = os.path.join(work, 'f')
os.makedirs(src)
r = random.Random(20261103)


def write(name, data):
    with open(os.path.join(src, name), 'wb') as f:
        f.write(data)


words = b"alpha beta gamma delta epsilon zeta eta theta iota kappa lambda".split()
write('text.txt', b" ".join(r.choice(words) for _ in range(6000)))
block = bytes(r.getrandbits(8) for _ in range(24000))
# The same 24000 random bytes twice, 3000 bytes apart: only a long-range match finds the repeat.
write('repeats.bin', block + bytes(r.getrandbits(8) for _ in range(3000)) + block)
# A table of 8-byte records: a counter rising by 7, a cycling small value, a constant.
write('table.bin', b"".join(struct.pack("<IHH", 1000 + 7 * i, i % 3, 0xBEEF) for i in range(6000)))
code = bytearray()
while len(code) < 20000:
    if r.randrange(5) == 0:
        code += b"\xE8" + struct.pack("<i", r.randrange(-4000, 4000))
    else:
        code += bytes([0x8B, 0x45, r.randrange(256)])
write('code.exe', bytes(code))
write('empty.bin', b'')

# Sentences on lines, which FreeArc's -m2..-m4 recognise as text and give to grzip.
prose = os.path.join(work, 'p')
os.makedirs(prose)
sentence_words = (b"the quick brown fox jumps over a lazy dog while archivers compress every sentence "
                  b"of this document into fewer bytes and readers decode them again later on").split()
with open(os.path.join(prose, 'book.txt'), 'wb') as f:
    lines = (b" ".join(r.choice(sentence_words) for _ in range(r.randint(5, 14))).capitalize() + b"."
             for _ in range(3000))
    f.write(bytes([13, 10]).join(lines))

rows = ["# archive\tname\tsize\tsha256 -- produced by FreeArc 0.67 Arc.exe x"]
for name, switch, *inputs in [('filters-rep.arc', '-mrep:1mb'), ('filters-delta.arc', '-mdelta'),
                     ('filters-exe.arc', '-mexe'), ('filters-mx.arc', '-mx'),
                     ('filters-4x4.arc', '-m4x4:b64kb:lzma'), ('filters-m5x.arc', '-m5x'),
                     ('tor-1.arc', '-mtor:1'), ('tor-2.arc', '-mtor:2'), ('tor-3.arc', '-mtor:3'),
                     ('tor-5.arc', '-mtor:5'), ('tor-12.arc', '-mtor:12'),
                     ('filters-m1.arc', '-m1'), ('filters-m2.arc', '-m2'),
                     ('ppmd-6.arc', '-mppmd:6:16mb'), ('ppmd-restart.arc', '-mppmd:12:256kb'),
                     ('ppmd-cutoff.arc', '-mppmd:12:256kb:r'), ('ppmd-freeze.arc', '-mppmd:12:256kb:r2'),
                     ('grzip-m1.arc', '-mgrzip:m1'), ('grzip-m2.arc', '-mgrzip:m2'),
                     ('grzip-m3.arc', '-mgrzip:m3'), ('grzip-m4.arc', '-mgrzip:m4'),
                     ('grzip-strong.arc', '-mgrzip:m1:s'), ('grzip-nolzp.arc', '-mgrzip:m3:l'),
                     ('grzip-delta.arc', '-mgrzip:m2:d:b64kb'),
                     ('text-m3.arc', '-m3', prose), ('text-m4.arc', '-m4', prose),
                     ('lzp.arc', '-mlzp'), ('lzp-short.arc', '-mlzp:b64kb:12:h12:d8kb:s20'),
                     ('lzp-tor.arc', '-mlzp:16:h16+tor:3'),
                     ('dict.arc', '-mdict', prose), ('dict-p.arc', '-mdict:p:b64kb', prose),
                     ('text-m5.arc', '-m5', prose), ('text-mx.arc', '-mx', prose), ('text-m9x.arc', '-m9x', prose),
                     ('crypt-aes.arc', '-mlzma -pSecret'), ('crypt-aes128-cfb.arc', '-mlzma -pSecret -ae=aes-128/cfb'),
                     ('crypt-blowfish.arc', '-mlzma -pSecret -ae=blowfish'), ('crypt-serpent.arc', '-mlzma -pSecret -ae=serpent'),
                     ('crypt-twofish.arc', '-mlzma -pSecret -ae=twofish'),
                     ('crypt-chain.arc', '-mlzma -pSecret -ae=blowfish/cfb+twofish/cfb+serpent'),
                     ('crypt-headers.arc', '-mlzma -hpSecret'), ('crypt-latin1.arc', '-mlzma -pP\u00e4sswort')]:
    target = os.path.join(work, name)
    passwords = [t[t.index('p') + 1:] for t in switch.split() if t.startswith(('-p', '-hp'))]
    p = subprocess.run([arc, 'a', target, '-i0', '-cfg-', '-ep1', *switch.split(), os.path.join(inputs[0] if inputs else src, '*')],
                       capture_output=True, text=True, errors='replace')
    if p.returncode != 0:
        raise SystemExit(f"{name}: {p.stdout}\n{p.stderr}")
    x = os.path.join(work, 'x-' + name)
    p = subprocess.run([arc, 'x', target, '-i0', '-cfg-', *['-p' + w for w in passwords], '-dp' + x], capture_output=True, text=True, errors='replace')
    if p.returncode != 0:
        raise SystemExit(f"x {name}: {p.stdout}\n{p.stderr}")
    for f in sorted(os.listdir(x)):
        data = open(os.path.join(x, f), 'rb').read()
        rows.append(f"{name}\t{f}\t{len(data)}\t{hashlib.sha256(data).hexdigest()}")
    shutil.copy(target, out)
    q = subprocess.run([arc, 'lt', target, '-i0', '-cfg-', *['-p' + w for w in passwords]], capture_output=True, text=True, errors='replace')
    print(name, os.path.getsize(target), [l.strip() for l in q.stdout.splitlines() if l.strip()[:1].isdigit()])

with open(os.path.join(out, 'filters.tsv'), 'w', newline='\n') as f:
    f.write('\n'.join(rows) + '\n')
print('ok')
