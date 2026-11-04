"""Captures libgme's render of the FDS test NSFs as comparison features.

usage: python capture_gme_features.py <dir-with-nsf> <out-dir>

Renders each NSF with ffmpeg's libgme demuxer (mono, 44100 Hz, 12 s) and writes per analysis
window (4096-sample Hann, hop 2205, mean removed):
  <tune>.gme.tsv   time-centre (s) and RMS
  <tune>.gme.spec  the window's FFT magnitudes for bins 1..463 (≈11 Hz..5 kHz), scaled so the
                   window's largest bin is 255 and stored as one unsigned byte per bin
"""
import math, os, subprocess, sys, tempfile
import numpy as np

RATE, WIN, HOP, SECONDS = 44100, 4096, 2205, 12
LO, HI = int(20 * WIN / RATE), int(5000 * WIN / RATE)

def render(nsf, raw):
    subprocess.run(['ffmpeg', '-hide_banner', '-v', 'error', '-y', '-sample_rate', str(RATE), '-i', nsf,
                    '-t', str(SECONDS), '-ac', '1', '-f', 's16le', '-acodec', 'pcm_s16le', raw], check=True)

def windows(raw):
    x = np.fromfile(raw, dtype='<i2').astype(np.float64)
    w = 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(WIN) / (WIN - 1))
    for s in range(0, len(x) - WIN, HOP):
        seg = x[s:s + WIN] - x[s:s + WIN].mean()
        yield (s + WIN / 2) / RATE, math.sqrt(float((seg * seg).mean())), np.abs(np.fft.rfft(seg * w))[LO:HI]

if __name__ == '__main__':
    src, dst = sys.argv[1], sys.argv[2]
    version = subprocess.run(['ffmpeg', '-version'], capture_output=True, text=True).stdout.splitlines()[0]
    for tune in ('fds_tone', 'fds_envelope', 'fds_modulation'):
        with tempfile.TemporaryDirectory() as tmp:
            raw = os.path.join(tmp, tune + '.raw')
            render(os.path.join(src, tune + '.nsf'), raw)
            rows, spec = [], bytearray()
            for t, rms, mag in windows(raw):
                rows.append(f'{t:.4f}\t{rms:.3f}')
                peak = mag.max()
                spec += bytes(np.round(mag / peak * 255).astype(np.uint8)) if peak > 0 else bytes(len(mag))
        with open(os.path.join(dst, tune + '.gme.tsv'), 'w', newline='\n') as f:
            f.write(f'# {version}; libgme demuxer, mono 44100 Hz; window {WIN} Hann, hop {HOP}; columns: time rms\n')
            f.write('\n'.join(rows) + '\n')
        with open(os.path.join(dst, tune + '.gme.spec'), 'wb') as f:
            f.write(spec)
        print(tune, len(rows), 'windows')
