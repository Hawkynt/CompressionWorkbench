"""Regenerates the .lzham fixtures with LZHAM's lzhamtest.

usage: python make-fixtures.py <lzhamtest executable>

The input generators mirror LzhamReferenceTests; only the inputs' sizes and SHA-256 sums are kept
in manifest.tsv. lzhamtest runs with -t0 (no helper threads, so its parse is deterministic) and
decodes every file again before it is written.
"""
import hashlib, os, subprocess, sys, tempfile

WORDS = b"the quick brown fox jumps over the lazy dog lzham huffman arithmetic context".split(b' ')
LINES = [b'line %02d: the quick brown fox jumps over the lazy dog while lzham keeps its tables adaptive' % i + bytes([10]) for i in range(24)]


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


def binary(length, seed):
    x, value, out = seed, 0, bytearray(length)
    for i in range(0, length - 3, 4):
        x = lcg(x); value = (value + ((x >> 16) & 7)) & 0xffffffff
        out[i:i + 4] = value.to_bytes(4, 'little')
    return bytes(out)


def mixed(seed):
    return repeats(60000, seed) + noise(8000, seed + 1) + text(60000, seed + 2) + binary(60000, seed + 3)


# name, generator, lzhamtest options, table update rate the file needs to decode
CASES = [
    ('text-20k-m4-d20', lambda: text(20000, 1), ['-m4', '-d20'], 8),
    ('text-20k-m0-d20', lambda: text(20000, 1), ['-m0', '-d20'], 8),
    ('text-20k-m2-d15', lambda: text(20000, 1), ['-m2', '-d15'], 8),
    ('text-20k-m4-d20-h1', lambda: text(20000, 1), ['-m4', '-d20', '-h1'], 1),
    ('text-20k-m4-d20-h20', lambda: text(20000, 1), ['-m4', '-d20', '-h20'], 20),
    ('repeats-300k-m4-d26', lambda: repeats(300000, 3), ['-m4', '-d26'], 8),
    ('repeats-60k-m3-d15', lambda: repeats(60000, 4), ['-m3', '-d15'], 8),
    ('zeros-200k-m4-d20', lambda: bytes(200000), ['-m4', '-d20'], 8),
    ('noise-3000-m4-d20', lambda: noise(3000, 5), ['-m4', '-d20'], 8),
    ('text-50k-m4-d28', lambda: text(50000, 6), ['-m4', '-d28', '-x'], 8),
    ('one-byte-m4-d20', lambda: b'A', ['-m4', '-d20'], 8),
    ('empty-m4-d20', lambda: b'', ['-m4', '-d20'], 8),
    ('mixed-188k-m0-d15', lambda: mixed(7), ['-m0', '-d15'], 8),
    ('mixed-188k-m1-d15', lambda: mixed(7), ['-m1', '-d15'], 8),
    ('mixed-188k-m2-d16', lambda: mixed(7), ['-m2', '-d16'], 8),
    ('mixed-188k-m3-d15', lambda: mixed(7), ['-m3', '-d15'], 8),
    ('mixed-188k-m4-d15', lambda: mixed(7), ['-m4', '-d15'], 8),
    ('mixed-188k-m4-d16-x', lambda: mixed(7), ['-m4', '-d16', '-x'], 8),
    ('mixed-188k-m1-d20', lambda: mixed(7), ['-m1', '-d20'], 8),
    ('mixed-188k-m4-d18-h3', lambda: mixed(7), ['-m4', '-d18', '-h3'], 3),
]


def main():
    tool = sys.argv[1]
    here = os.path.dirname(os.path.abspath(__file__))
    manifest = []
    with tempfile.TemporaryDirectory() as work:
        for name, gen, opts, rate in CASES:
            data = gen()
            source = os.path.join(work, name + '.in')
            open(source, 'wb').write(data)
            target = os.path.join(here, name + '.lzham')
            subprocess.run([tool, '-t0'] + opts + ['c', source, target], check=True, capture_output=True)
            back = os.path.join(work, name + '.out')
            subprocess.run([tool, '-h%d' % rate, 'd', target, back], check=True, capture_output=True)
            assert open(back, 'rb').read() == data, name
            blob = open(target, 'rb').read()
            manifest.append(f"{name}.lzham\t{len(data)}\t{hashlib.sha256(data).hexdigest()}\t{' '.join(opts)}\t{rate}\t{len(blob)}")
    open(os.path.join(here, 'manifest.tsv'), 'w', newline='\n').write('\n'.join(manifest) + '\n')


if __name__ == '__main__':
    main()
