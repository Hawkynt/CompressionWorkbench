"""Regenerates the .bsc fixtures with libbsc's bsc tool.

usage: python make-fixtures.py <bsc executable> [<second bsc executable to compare>]

The input generators mirror BscLibbscReferenceTests; only the inputs' sizes and SHA-256 sums are
kept in manifest.tsv. Every file is decoded again by bsc before it is written.
"""
import filecmp, hashlib, os, subprocess, sys, tempfile

WORDS = b"the quick brown fox jumps over the lazy dog block sorting compressor libbsc qlfc lzp context".split(b' ')
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


def records(count, seed):
    x, out = seed, bytearray()
    for i in range(count):
        x = lcg(x); out += ((i * 7 + ((x >> 16) & 3)) & 0xffffffff).to_bytes(4, 'little')
    return bytes(out)


CASES = [
    ('text-20k', lambda: text(20000, 1), []),
    ('text-20k-e0', lambda: text(20000, 1), ['-e0']),
    ('text-20k-e2', lambda: text(20000, 1), ['-e2']),
    ('text-20k-cp', lambda: text(20000, 1), ['-cp']),
    ('text-20k-nolzp', lambda: text(20000, 1), ['-p']),
    ('text-100k', lambda: text(100000, 2), []),
    ('text-300k', lambda: text(300000, 3), []),
    ('repeats-50k', lambda: repeats(50000, 7), []),
    ('repeats-300k', lambda: repeats(300000, 3), []),
    ('records-40k-r', lambda: records(10000, 5), ['-r']),
    ('text-60k-b25000', lambda: text(60000, 4), ['-b25000']),
    ('text-20k-m3', lambda: text(20000, 1), ['-m3']),
    ('text-20k-m4', lambda: text(20000, 1), ['-m4']),
    ('text-20k-m5', lambda: text(20000, 1), ['-m5']),
    ('text-20k-m6', lambda: text(20000, 1), ['-m6']),
    ('repeats-50k-m6', lambda: repeats(50000, 7), ['-m6']),
    ('noise-3000', lambda: noise(3000, 6), []),
    ('noise-3000-cp', lambda: noise(3000, 6), ['-cp']),
    ('tiny-hello', lambda: b'hello', []),
    ('tiny-hello-cp', lambda: b'hello', ['-cp']),
    ('one-byte', lambda: b'A', []),
]


def main():
    tools = sys.argv[1:]
    here = os.path.dirname(os.path.abspath(__file__))
    manifest = []
    with tempfile.TemporaryDirectory() as work:
        for name, gen, opts in CASES:
            data = gen()
            source = os.path.join(work, name + '.in')
            open(source, 'wb').write(data)
            outputs = []
            for index, tool in enumerate(tools):
                target = os.path.join(work, f'{name}.{index}.bsc')
                subprocess.run([tool, 'e', source, target] + opts, check=True, capture_output=True)
                outputs.append(target)
            for other in outputs[1:]:
                assert filecmp.cmp(outputs[0], other, shallow=False), name
            back = os.path.join(work, name + '.out')
            subprocess.run([tools[0], 'd', outputs[0], back], check=True, capture_output=True)
            assert open(back, 'rb').read() == data, name
            blob = open(outputs[0], 'rb').read()
            open(os.path.join(here, name + '.bsc'), 'wb').write(blob)
            manifest.append(f"{name}.bsc\t{len(data)}\t{hashlib.sha256(data).hexdigest()}\t{' '.join(opts) or '-'}\t{len(blob)}")
    open(os.path.join(here, 'manifest.tsv'), 'w', newline='\n').write('\n'.join(manifest) + '\n')


if __name__ == '__main__':
    main()
