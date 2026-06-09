#pragma warning disable CS1591
using Codec.Ym2413;

namespace Codec.Nes2a03.Expansion;

/// <summary>
/// The Konami VRC7 expansion sound: a 6-channel FM synthesiser built on the same OPLL operator
/// die as the Yamaha YM2413, with no rhythm mode and a substitute 15-voice instrument patch ROM.
/// It is a thin adapter over <see cref="Ym2413Codec"/>, which accepts the VRC7 patch table and
/// reuses its entire operator/envelope/log-sine core.
/// <para>The NSF register interface is a two-stage latch: a write to <c>$9010</c> selects the OPLL
/// register address, and a write to <c>$9030</c> stores the data byte. Only the six melodic
/// channels exist (OPLL channel registers <c>$10-$15</c>, <c>$20-$25</c>, <c>$30-$35</c>);
/// the rhythm register <c>$0E</c> is inert because no rhythm patches are loaded.</para>
/// <para>References: NESdev wiki <i>VRC7 audio</i>; the VRC7 instrument patch set is transcribed
/// from the Nuke.YKT VRC7 die-dump (as adopted by emu2413's <c>OPLL_VRC7_TONE</c> table). The
/// patch-row layout matches <see cref="Ym2413Codec"/>. Output is pre-scaled into the 2A03 mixer's
/// ~0..1 domain.</para>
/// </summary>
internal sealed class Vrc7Audio : IExpansionAudio {

  /// <summary>
  /// VRC7 instrument patch ROM (row 0 user template + 15 melodic voices). Source: Nuke.YKT VRC7
  /// die dump / emu2413 VRC7 tone table. Rows 16..18 are zero (the VRC7 has no rhythm mode).
  /// Each row encodes <c>[mod flags/MUL, car flags/MUL, mod KSL/TL, car KSL/FB/wave, mod AR/DR,
  /// car AR/DR, mod SL/RR, car SL/RR]</c> per the OPLL register format.
  /// </summary>
  private static readonly byte[][] Vrc7Instruments = [
    [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], //  0 user
    [0x03, 0x21, 0x05, 0x06, 0xe8, 0x81, 0x42, 0x27], //  1
    [0x13, 0x41, 0x14, 0x0d, 0xd8, 0xf6, 0x23, 0x12], //  2
    [0x11, 0x11, 0x08, 0x08, 0xfa, 0xb2, 0x20, 0x12], //  3
    [0x31, 0x61, 0x0c, 0x07, 0xa8, 0x64, 0x61, 0x27], //  4
    [0x32, 0x21, 0x1e, 0x06, 0xe1, 0x76, 0x01, 0x28], //  5
    [0x02, 0x01, 0x06, 0x00, 0xa3, 0xe2, 0xf4, 0xf4], //  6
    [0x21, 0x61, 0x1d, 0x07, 0x82, 0x81, 0x11, 0x07], //  7
    [0x23, 0x21, 0x22, 0x17, 0xa2, 0x72, 0x01, 0x17], //  8
    [0x35, 0x11, 0x25, 0x00, 0x40, 0x73, 0x72, 0x01], //  9
    [0xb5, 0x01, 0x0f, 0x0f, 0xa8, 0xa5, 0x51, 0x02], // 10
    [0x17, 0xc1, 0x24, 0x07, 0xf8, 0xf8, 0x22, 0x12], // 11
    [0x71, 0x23, 0x11, 0x06, 0x65, 0x74, 0x18, 0x16], // 12
    [0x01, 0x02, 0xd3, 0x05, 0xc9, 0x95, 0x03, 0x02], // 13
    [0x61, 0x63, 0x0c, 0x00, 0x94, 0xc0, 0x33, 0xf6], // 14
    [0x21, 0x72, 0x0d, 0x00, 0xc1, 0xd5, 0x56, 0x06], // 15
    [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], // 16 (unused — no rhythm)
    [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], // 17 (unused)
    [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], // 18 (unused)
  ];

  private readonly Ym2413Codec _opll;
  private int _latchedAddress;

  // The OPLL emits one slot per clock/72 tick; the expansion mixer clocks this once per CPU cycle,
  // so accumulate native frames and hold the last one between them.
  private readonly double _nativeCyclesPerCpu;
  private double _cycleAccumulator;
  private float _lastSample;

  public Vrc7Audio(double clockHz) {
    // The VRC7 runs the OPLL core from the NES CPU clock.
    this._opll = new Ym2413Codec(clockHz, Vrc7Instruments);
    this._nativeCyclesPerCpu = this._opll.NativeSampleRate / clockHz;
  }

  public bool HandlesWrite(ushort addr) => addr is 0x9010 or 0x9030;

  public void Write(ushort addr, byte value) {
    if (addr == 0x9010)
      this._latchedAddress = value;
    else if (addr == 0x9030)
      this._opll.WriteRegister(this._latchedAddress, value);
  }

  public bool TryRead(ushort addr, out byte value) {
    value = 0;
    return false;
  }

  public void ClockOneCpuCycle() {
    this._cycleAccumulator += this._nativeCyclesPerCpu;
    if (this._cycleAccumulator < 1.0)
      return;
    this._cycleAccumulator -= 1.0;
    this._lastSample = this._opll.RenderSample();
  }

  // The OPLL RenderSample returns a signed ~16-bit value; six VRC7 channels peak well below full
  // 16-bit scale. NSFPlay places the VRC7 a little below a 2A03 pulse pair; ~0.5 of full scale at
  // a comfortable level.
  private const float MixScale = 0.5f / 32768.0f;

  public float Output() => this._lastSample * MixScale;
}
