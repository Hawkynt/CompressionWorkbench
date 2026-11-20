"""Writes the inputs the split ALZ and EGG fixtures were made from (no third-party content).

usage: python3 -I make-split-inputs.py <output dir>

text.txt is pseudo-text over a large random vocabulary, sized so every method still needs two
64 KiB volumes; sub/table.csv and small.txt add a folder and a tiny member.
"""
import os, random, sys

out = sys.argv[1]
os.makedirs(os.path.join(out, 'sub'), exist_ok=True)
r = random.Random(2126)
vocab = [''.join(r.choice('abcdefghijklmnopqrstuvwxyz') for _ in range(r.randint(3, 9))) for _ in range(6000)]
words, size = [], 0
while size < 180000:
    w = r.choice(vocab)
    words.append(w)
    size += len(w) + 1
open(os.path.join(out, 'text.txt'), 'wb').write(' '.join(words).encode()[:180000])
rows = ['%d,%s,%d' % (i, r.choice(vocab), r.randrange(1000000)) for i in range(2500)]
open(os.path.join(out, 'sub', 'table.csv'), 'wb').write('\n'.join(rows).encode())
open(os.path.join(out, 'small.txt'), 'wb').write(b'hello from a split archive\n')
