"""Regenerates the .bcm fixtures with Ilya Muravyov's bcm.

usage: python make-fixtures.py <bcm 1.x> [<other bcm 1.x builds to compare>...] [--v2 <bcm 2.x>]

The input generators mirror BcmReferenceTests; only the inputs' sizes and SHA-256 sums are kept in
manifest.tsv. Every file is decoded again by the first bcm before it is written. With --v2, one
file from BCM 2.x is added, which the reader must refuse.
"""
import filecmp, hashlib, os, subprocess, sys, tempfile

WORDS = b"the quick brown fox jumps over the lazy dog block sorting compressor bcm context mixing".split(b' ')
LINES = [b'line %02d: the quick brown fox jumps over the lazy dog while the block sorter keeps every context' % i + bytes([10]) for i in range(24)]


def lcg(x): return (x * 1103515245 + 12345) & 0x7fffffff


def text(length, seed):
    x, out = seed, bytearray()
    while len(out) < length:
        x = lcg(x); out += WORDS[(x >> 16) % len(WORDS)] + b' '
    return bytes(out[:length])


def repeats(length, seed):
    x, out = seed, bytearray()
    while len(out) < length:
        x = lcg(x); out += LINES[(x >> 16) % len(LINES)]
    return bytes(out[:length])


def noise(length, seed):
    x, out = seed, bytearray(length)
    for i in range(length):
        x = lcg(x); out[i] = (x >> 16) & 0xff
    return bytes(out)


CASES = [
    ('text-20k', lambda: text(20000, 1), []),
    ('text-100k', lambda: text(100000, 2), []),
    ('repeats-2500k-b1', lambda: repeats(2500000, 3), ['-b1']),
    ('noise-3000', lambda: noise(3000, 4), []),
    ('tiny-hello', lambda: b'hello', []),
    ('one-byte', lambda: b'A', []),
    ('empty', lambda: b'', []),
]


def main():
    args = sys.argv[1:]
    v2 = None
    if '--v2' in args:
        i = args.index('--v2'); v2 = args[i + 1]; del args[i:i + 2]
    tools = args
    here = os.path.dirname(os.path.abspath(__file__))
    manifest = []
    with tempfile.TemporaryDirectory() as work:
        for name, gen, opts in CASES:
            data = gen()
            source = os.path.join(work, name + '.in')
            open(source, 'wb').write(data)
            outputs = []
            # Other builds' block-size options differ (1.10: -b#, 1.60: -1..-9), so only the
            # default cases are compared across builds.
            for index, tool in enumerate(tools if not opts else tools[:1]):
                target = os.path.join(work, f'{name}.{index}.bcm')
                subprocess.run([tool] + opts + ['-f', source, target], check=True, capture_output=True)
                outputs.append(target)
            for other in outputs[1:]:
                assert filecmp.cmp(outputs[0], other, shallow=False), name
            back = os.path.join(work, name + '.out')
            subprocess.run([tools[0], '-d', '-f', outputs[0], back], check=True, capture_output=True)
            assert open(back, 'rb').read() == data, name
            blob = open(outputs[0], 'rb').read()
            open(os.path.join(here, name + '.bcm'), 'wb').write(blob)
            manifest.append(f"{name}.bcm\t{len(data)}\t{hashlib.sha256(data).hexdigest()}\t{' '.join(opts) or '-'}\t{len(blob)}")
        if v2:
            data = text(5000, 5)
            source = os.path.join(work, 'v2.in')
            open(source, 'wb').write(data)
            target = os.path.join(here, 'bcm2-text-5000.bcm')
            subprocess.run([v2, '-f', source, target], check=True, capture_output=True)
            assert open(target, 'rb').read(4) == b'BCM2'
    open(os.path.join(here, 'manifest.tsv'), 'w', newline='\n').write('\n'.join(manifest) + '\n')


if __name__ == '__main__':
    main()
