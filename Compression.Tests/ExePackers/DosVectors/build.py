import subprocess, sys, struct
# build.py <asm> <out> NREL NTEXT NRAND SEED NFAR
asm, out, nrel, ntext, nrand, seed, nfar = sys.argv[1:8]
def nasm(csum):
    subprocess.check_call(['nasm', '-f', 'bin', f'-DNREL={nrel}', f'-DNTEXT={ntext}', f'-DNRAND={nrand}',
                           f'-DSEED={seed}', f'-DNFAR={nfar}', f'-DCSUM={csum}', '-o', out, asm])
nasm(0)
b = open(out, 'rb').read()
hdr = struct.unpack_from('<H', b, 8)[0] * 16
data = b[hdr + 0x1000:]
start = 5 + 6 + 2 + 4 * int(nrel)
blen = int(ntext) * 49 + int(nrand)
blob = data[start:start + min(blen, 60000)]
bx = 0
for c in blob:
    bx = (bx + c) & 0xFFFF
    bx = ((bx << 1) | (bx >> 15)) & 0xFFFF
nasm(bx)
print(out, len(open(out, 'rb').read()), hex(bx))
