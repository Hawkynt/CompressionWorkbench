import subprocess, sys
# buildc.py <asm> <out> NTEXT NRAND SEED
asm, out, ntext, nrand, seed = sys.argv[1:6]
def nasm(csum):
    subprocess.check_call(['nasm', '-f', 'bin', f'-DNTEXT={ntext}', f'-DNRAND={nrand}', f'-DSEED={seed}', f'-DCSUM={csum}', '-o', out, asm])
nasm(0)
b = open(out, 'rb').read()
blen = int(ntext) * 49 + int(nrand)
blob = b[len(b) - blen:]
bx = 0
for c in blob:
    bx = (bx + c) & 0xFFFF
    bx = ((bx << 1) | (bx >> 15)) & 0xFFFF
nasm(bx)
print(out, len(open(out, 'rb').read()), hex(bx))
