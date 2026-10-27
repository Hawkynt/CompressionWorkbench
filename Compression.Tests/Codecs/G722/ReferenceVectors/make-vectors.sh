#!/usr/bin/env bash
# Regenerates the G.722 reference vectors in this directory with FFmpeg as the oracle.
# Usage: FFMPEG=/path/to/ffmpeg ./make-vectors.sh   (defaults to `ffmpeg` on PATH)
set -euo pipefail
cd "$(dirname "$0")"
FFMPEG="${FFMPEG:-ffmpeg}"
PYTHON="${PYTHON:-python3}"

# Synthetic inputs: 16-bit little-endian mono PCM at 16 kHz.
"$PYTHON" - <<'PY'
import math, struct
def write(name, samples):
    with open(name + ".pcm", "wb") as f:
        f.write(struct.pack("<%dh" % len(samples), *samples))
n = 8000                                    # 0.5 s
write("silence", [0] * 16000)               # 1 s
write("sine1k", [int(round(16384 * math.sin(2 * math.pi * 1000 * i / 16000))) for i in range(n)])  # -6 dBFS
write("sine6k", [int(round(16384 * math.sin(2 * math.pi * 6000 * i / 16000))) for i in range(n)])
state = 0x12345678                          # 32-bit LCG (Numerical Recipes constants), top 16 bits
noise = []
for _ in range(n):
    state = (state * 1664525 + 1013904223) & 0xFFFFFFFF
    noise.append(((state >> 16) ^ 0x8000) - 0x8000)
write("noise", noise)
write("square", [32767 if (i // 20) % 2 == 0 else -32768 for i in range(n)])   # 400 Hz full scale
PY

# Speech: FFmpeg's built-in flite synthesiser, resampled to 16 kHz mono s16.
"$FFMPEG" -hide_banner -loglevel error -y -f lavfi -i "flite=text='The quick brown fox jumps over the lazy dog.':voice=slt" \
  -ar 16000 -ac 1 -t 1 -f s16le speech.pcm

# Decoder sweep: octets 0x00..0xFF repeated 64 times.
"$PYTHON" -c "open('sweep.g722','wb').write(bytes(range(256))*64)"

for v in silence sine1k sine6k noise square speech; do
  "$FFMPEG" -hide_banner -loglevel error -y -f s16le -ar 16000 -ac 1 -i "$v.pcm" -c:a g722 -f g722 "$v.g722"
  "$FFMPEG" -hide_banner -loglevel error -y -f g722 -i "$v.g722" -f s16le "$v.dec.pcm"
done
"$FFMPEG" -hide_banner -loglevel error -y -f g722 -i sweep.g722 -f s16le sweep.dec.pcm
