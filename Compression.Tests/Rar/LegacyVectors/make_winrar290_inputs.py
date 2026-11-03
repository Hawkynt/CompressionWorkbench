# Deterministic inputs for the WinRAR 2.90 RAR 2.0 vectors (written to the cwd).
import math, random, struct
r = random.Random(20261030)

def wav(name, channels, bits, frames, rate=22050):
    data = bytearray()
    for i in range(frames):
        for c in range(channels):
            v = math.sin(2 * math.pi * (220 * (c + 1)) * i / rate) * 0.6 + math.sin(2 * math.pi * 3 * i / rate) * 0.2
            v += (r.random() - 0.5) * 0.05
            if bits == 8:
                data.append(max(0, min(255, int(128 + v * 120))))
            else:
                data += struct.pack('<h', max(-32768, min(32767, int(v * 30000))))
    block = channels * bits // 8
    hdr = b'RIFF' + struct.pack('<I', 36 + len(data)) + b'WAVEfmt ' + struct.pack('<IHHIIHH', 16, 1, channels, rate, rate * block, block, bits) + b'data' + struct.pack('<I', len(data))
    open(name, 'wb').write(hdr + data)

wav('mono8.wav', 1, 8, 24000)
wav('stereo16.wav', 2, 16, 12000)
wav('quad8.wav', 4, 8, 6000)

words = "archive solid dictionary window match literal huffman table length offset repeat".split()
lines = []
for i in range(1500):
    lines.append(" ".join(r.choice(words) for _ in range(r.randint(4, 12))) + " %d\n" % (i * 37 % 1009))
open('text.txt', 'w', newline='\n').write("".join(lines))
open('table.bin', 'wb').write(bytes((i * 7 + (i >> 8)) & 0xFF for i in range(40000)))
b = bytearray(r.randrange(256) for _ in range(3000))
for i in range(30000):
    b.append(b[-r.randint(1, 3000)] if r.random() < 0.7 else r.randrange(256))
open('mixed.bin', 'wb').write(bytes(b))
