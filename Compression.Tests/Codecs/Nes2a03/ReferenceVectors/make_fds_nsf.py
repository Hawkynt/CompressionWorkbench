"""Builds small homebrew FDS test NSFs from register-write scripts.

Each NSF carries a tiny 6502 driver (hand-assembled below) that, once per play call, replays
one frame of (register, value) writes from a script table. All registers are $40xx.
"""
import math, struct, sys, os

def driver():
    c = bytearray()
    # INIT at $8000: ($00) = script, $03 = $40
    c += bytes([0xA9, 0x00, 0x85, 0x00,   # LDA #<script ; STA $00
                0xA9, 0x81, 0x85, 0x01,   # LDA #>script ; STA $01
                0xA9, 0x40, 0x85, 0x03,   # LDA #$40 ; STA $03
                0x60])                    # RTS
    while len(c) < 0x10: c.append(0xEA)
    # PLAY at $8010
    p = bytearray()
    p += bytes([0xA0, 0x00])              # LDY #0
    p += bytes([0xB1, 0x00])              # LDA ($00),Y
    p += bytes([0xC9, 0xFF])              # CMP #$FF
    p += bytes([0xF0, 0x00])              # BEQ done (patched)
    beq_done = len(p) - 1
    p += bytes([0xAA, 0xC8])              # TAX ; INY
    loop = len(p)
    p += bytes([0xE0, 0x00])              # CPX #0
    p += bytes([0xF0, 0x00])              # BEQ advance (patched)
    beq_adv = len(p) - 1
    p += bytes([0xB1, 0x00, 0x85, 0x02, 0xC8])   # LDA ($00),Y ; STA $02 ; INY
    p += bytes([0xB1, 0x00, 0xC8])               # LDA ($00),Y ; INY
    p += bytes([0x84, 0x04, 0xA0, 0x00, 0x91, 0x02, 0xA4, 0x04])  # STY $04; LDY #0; STA ($02),Y; LDY $04
    p += bytes([0xCA])                           # DEX
    p += bytes([0x4C]) + struct.pack('<H', 0x8010 + loop)          # JMP loop
    adv = len(p)
    p[beq_adv] = (adv - (beq_adv + 1)) & 0xFF
    p += bytes([0x98, 0x18, 0x65, 0x00, 0x85, 0x00,   # TYA; CLC; ADC $00; STA $00
                0xA5, 0x01, 0x69, 0x00, 0x85, 0x01])  # LDA $01; ADC #0; STA $01
    done = len(p)
    p[beq_done] = (done - (beq_done + 1)) & 0xFF
    p += bytes([0x60])
    c += p
    while len(c) < 0x100: c.append(0xEA)
    return c

def build(frames, title):
    """frames: list of lists of (reg, value) — one inner list per play call."""
    script = bytearray()
    for f in frames:
        assert len(f) < 127
        script.append(len(f))
        for reg, val in f:
            assert 0x4040 <= reg <= 0x408A or reg == 0x4023
            script += bytes([reg & 0xFF, val & 0xFF])
    script.append(0xFF)
    data = driver() + script
    hdr = bytearray(0x80)
    hdr[0:5] = b'NESM\x1A'; hdr[5] = 1; hdr[6] = 1; hdr[7] = 1
    struct.pack_into('<HHH', hdr, 8, 0x8000, 0x8000, 0x8010)
    hdr[0x0E:0x0E + len(title)] = title.encode()
    hdr[0x2E:0x2E + 9] = b'Hawkynt  '[:9]
    hdr[0x4E:0x4E + 15] = b'Public domain  '
    struct.pack_into('<H', hdr, 0x6E, 16639)
    struct.pack_into('<H', hdr, 0x78, 19997)
    hdr[0x7A] = 0; hdr[0x7B] = 0x04
    return bytes(hdr) + bytes(data)

def wave_writes(samples):
    w = [(0x4089, 0x80)] + [(0x4040 + i, s) for i, s in enumerate(samples)] + [(0x4089, 0x00)]
    return w

def sine():
    return [int(round(31.5 + 31.5 * math.sin(2 * math.pi * i / 64))) for i in range(64)]

def tri():
    return [ (i * 2) if i < 32 else (127 - i * 2) for i in range(64)]

def setup(wave):
    return [(0x4023, 0x00), (0x4023, 0x83)] + wave_writes(wave) + [(0x4087, 0x80), (0x408A, 0xE8)]

def tone_nsf():
    # pitches held for 30 frames each, volume direct 32; then master volume 0..3 at fixed pitch
    fr = [setup(sine()) + [(0x4080, 0xA0), (0x4084, 0x80), (0x4085, 0x00)]]
    for p in (0x100, 0x2A0, 0x4F3, 0x7FF, 0xC00, 0xFFF):
        fr.append([(0x4082, p & 0xFF), (0x4083, p >> 8)])
        fr += [[] for _ in range(29)]
    for mv in range(4):
        fr.append([(0x4089, mv)])
        fr += [[] for _ in range(29)]
    fr.append([(0x4083, 0x80)])   # halt
    fr += [[] for _ in range(10)]
    return build(fr, 'FDS tone + master volume')

def env_nsf():
    fr = [setup(tri()) + [(0x4080, 0x80), (0x4084, 0x80), (0x4085, 0x00),
                          (0x4082, 0x58), (0x4083, 0x03)]]          # pitch 0x358, gain 0
    fr.append([(0x4080, 0x41)])        # envelope on, increase, speed 1
    fr += [[] for _ in range(59)]
    fr.append([(0x4080, 0x03)])        # decrease, speed 3
    fr += [[] for _ in range(119)]
    fr.append([(0x408A, 0x40), (0x4080, 0x80 | 0x20), (0x4080, 0x00)])  # master speed 0x40, gain 32 then decrease speed 0
    fr += [[] for _ in range(59)]
    fr.append([(0x408A, 0x00), (0x4080, 0x9A)])   # master speed 0 → frozen, gain 26
    fr += [[] for _ in range(30)]
    fr.append([(0x408A, 0xE8), (0x4080, 0x45), (0x4083, 0x43)])  # envelopes halted by $4083 bit6
    fr += [[] for _ in range(30)]
    fr.append([(0x4083, 0x03)])        # release the halt → increase speed 5 from 26
    fr += [[] for _ in range(60)]
    return build(fr, 'FDS volume envelope')

def mod_nsf():
    # vibrato: table = +1 x8, -1 x16, +1 x8  (in 32 entries)
    table = [1] * 8 + [7] * 16 + [1] * 8
    fr = [setup(sine()) + [(0x4080, 0xA0), (0x4082, 0x00), (0x4083, 0x04)]]  # pitch 0x400
    fr.append([(0x4087, 0x80)] + [(0x4088, t) for t in table] + [(0x4085, 0x00), (0x4084, 0x90),
              (0x4086, 0x40), (0x4087, 0x00)])          # mod pitch 0x040, gain 16
    fr += [[] for _ in range(119)]
    fr.append([(0x4084, 0xBF)])        # gain 63 (direct, above 32)
    fr += [[] for _ in range(59)]
    fr.append([(0x4084, 0x80), (0x4084, 0x42)])   # gain 0, then envelope increase speed 2
    fr += [[] for _ in range(119)]
    fr.append([(0x4087, 0x80), (0x4085, 0x3F), (0x4084, 0x9F)])   # halt mod, counter 63, gain 31 (static detune)
    fr += [[] for _ in range(29)]
    fr.append([(0x4085, 0x40), (0x4084, 0xBF)])   # counter -64, gain 63 (wrap case)
    fr += [[] for _ in range(29)]
    # reset-entry table: 4s and +4s, fast mod
    t2 = [3, 3, 3, 4] * 8
    fr.append([(0x4088, t) for t in t2] + [(0x4085, 0x00), (0x4084, 0x88), (0x4086, 0x00), (0x4087, 0x02)])
    fr += [[] for _ in range(59)]
    return build(fr, 'FDS modulation unit')

if __name__ == '__main__':
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    for name, f in (('fds_tone', tone_nsf), ('fds_envelope', env_nsf), ('fds_modulation', mod_nsf)):
        open(os.path.join(out, name + '.nsf'), 'wb').write(f())
