"""Regenerates the .ctw fixtures with the CTW 0.1 reference utility.

usage: python make-fixtures.py <ctw executable> [<another ctw build to compare>]

The input generators mirror CtwReferenceTests; only the inputs' sizes and SHA-256 sums are kept in
manifest.tsv. Every file is decoded again by the first ctw before it is written.
"""
import filecmp, hashlib, os, subprocess, sys, tempfile

WORDS = b"the quick brown fox jumps over the lazy dog context tree weighting estimator".split(b' ')


def lcg(x): return (x * 1103515245 + 12345) & 0x7fffffff


def text(length, seed):
    x, out = seed, bytearray()
    while len(out) < length:
        x = lcg(x); out += WORDS[(x >> 16) % len(WORDS)] + b' '
    return bytes(out[:length])


def noise(length, seed):
    x, out = seed, bytearray(length)
    for i in range(length):
        x = lcg(x); out[i] = (x >> 16) & 0xff
    return bytes(out)


def runs(length):
    return bytes((i // 300) % 7 for i in range(length))


CASES = [
    ('text-20k', lambda: text(20000, 1), []),
    ('text-20k-d2', lambda: text(20000, 1), ['-d2']),
    ('text-20k-d10', lambda: text(20000, 1), ['-d10']),
    ('text-20k-k', lambda: text(20000, 1), ['-k']),
    ('text-20k-r', lambda: text(20000, 1), ['-r']),
    ('text-20k-s', lambda: text(20000, 1), ['-s']),
    ('text-20k-n1k-t2', lambda: text(20000, 1), ['-n1K', '-t2']),
    ('text-20k-f512', lambda: text(20000, 1), ['-f512']),
    ('text-20k-b16', lambda: text(20000, 1), ['-b16']),
    ('runs-60k', lambda: runs(60000), []),
    ('noise-3000', lambda: noise(3000, 2), []),
    ('short-5', lambda: b'hello', []),
    ('one-byte', lambda: b'A', []),
    ('empty', lambda: b'', []),
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
                target = os.path.join(work, f'{name}.{index}.ctw')
                subprocess.run([tool, 'e', '-y'] + opts + [source, target], check=True, capture_output=True)
                outputs.append(target)
            for other in outputs[1:]:
                assert filecmp.cmp(outputs[0], other, shallow=False), name
            back = os.path.join(work, name + '.out')
            subprocess.run([tools[0], 'd', '-y', outputs[0], back], check=True, capture_output=True)
            assert open(back, 'rb').read() == data, name
            blob = open(outputs[0], 'rb').read()
            open(os.path.join(here, name + '.ctw'), 'wb').write(blob)
            manifest.append(f"{name}.ctw\t{len(data)}\t{hashlib.sha256(data).hexdigest()}\t{' '.join(opts) or '-'}\t{len(blob)}")
    open(os.path.join(here, 'manifest.tsv'), 'w', newline='\n').write('\n'.join(manifest) + '\n')


if __name__ == '__main__':
    main()
