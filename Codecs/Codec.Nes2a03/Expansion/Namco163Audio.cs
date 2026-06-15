#pragma warning disable CS1591
namespace Codec.Nes2a03.Expansion;

/// <summary>
/// The Namco 163 expansion sound: 1-8 wavetable channels synthesised from 128 bytes of internal
/// sound RAM.
/// <list type="bullet">
///   <item><b>$F800</b> (address port): selects the RAM address; bit 7 enables post-increment.</item>
///   <item><b>$4800</b> (data port): reads/writes the selected RAM byte, auto-incrementing the
///     address afterwards when bit 7 of the last <c>$F800</c> write was set.</item>
///   <item><b>Channel registers</b> live in high RAM, 8 bytes each (channel 8 at $78-$7F down to
///     channel 1 at $40-$47): +0/+2/+4 the 18-bit frequency, +1/+3/+5 the 24-bit phase, +4 bits
///     7-2 the wave length (256 − 4·L samples), +6 the wave start address, +7 bits 3-0 the 4-bit
///     volume and bits 6-4 the active-channel count (1 + value).</item>
/// </list>
/// <para>Synthesis (NESdev): every 15 CPU cycles one active channel updates —
/// <c>phase = (phase + freq) mod (length·2^16)</c>; the sample index <c>((phase&gt;&gt;16)+addr)&amp;0xFF</c>
/// fetches a packed 4-bit nibble from RAM (two samples per byte, low nibble first); the channel
/// output is <c>(sample − 8) · volume</c>. The active channels are the highest-numbered ones
/// (channel 8 first).</para>
/// <para><b>Multiplex approximation.</b> Real hardware time-multiplexes a single DAC, cycling
/// through the active channels so only one is driven at a time; the per-channel outputs are
/// summed here (the standard NSFPlay/Mesen emulation), which reproduces the perceived mix but not
/// the multiplex ripple. Mix level follows NSFPlay's N163 master (each added channel quieter, so
/// the sum is normalised by the active count). References: NESdev wiki <i>Namco 163 audio</i>.</para>
/// </summary>
internal sealed class Namco163Audio : IExpansionAudio {

  private readonly byte[] _ram = new byte[0x80];
  private int _ramAddress;
  private bool _autoIncrement;

  // Per-channel last output (for the summed-multiplex approximation).
  private readonly int[] _channelOut = new int[8];

  // Round-robin update state: one channel updates every 15 CPU cycles.
  private int _updateDivider;
  private int _updateChannel;       // 0..7 index into the active set

  private const int CyclesPerUpdate = 15;

  public bool HandlesWrite(ushort addr) => addr is >= 0x4800 and <= 0x4FFF or >= 0xF800;

  public void Write(ushort addr, byte value) {
    if (addr is >= 0xF800) {
      this._ramAddress = value & 0x7F;
      this._autoIncrement = (value & 0x80) != 0;
      return;
    }
    if (addr is >= 0x4800 and <= 0x4FFF) {
      this._ram[this._ramAddress] = value;
      if (this._autoIncrement)
        this._ramAddress = (this._ramAddress + 1) & 0x7F;
    }
  }

  public bool TryRead(ushort addr, out byte value) {
    if (addr is >= 0x4800 and <= 0x4FFF) {
      value = this._ram[this._ramAddress];
      if (this._autoIncrement)
        this._ramAddress = (this._ramAddress + 1) & 0x7F;
      return true;
    }
    value = 0;
    return false;
  }

  private int ActiveChannels => ((this._ram[0x7F] >> 4) & 0x07) + 1;

  public void ClockOneCpuCycle() {
    if (++this._updateDivider < CyclesPerUpdate)
      return;
    this._updateDivider = 0;

    var active = this.ActiveChannels;
    // Cycle through the active channels; channel 8 (highest) is the first active one.
    this._updateChannel %= active;
    var channel = 7 - this._updateChannel;      // channel 8 → register base $78
    this.UpdateChannel(channel);
    this._updateChannel = (this._updateChannel + 1) % active;
  }

  private void UpdateChannel(int channel) {
    var b = 0x40 + channel * 8;                 // register base for this channel

    var freq = this._ram[b + 0] | (this._ram[b + 2] << 8) | ((this._ram[b + 4] & 0x03) << 16);
    var phase = this._ram[b + 1] | (this._ram[b + 3] << 8) | (this._ram[b + 5] << 16);
    var length = 256 - (this._ram[b + 4] & 0xFC);
    var waveAddr = this._ram[b + 6];
    var volume = this._ram[b + 7] & 0x0F;

    var modulus = length << 16;
    if (modulus <= 0)
      return;
    phase = (phase + freq) % modulus;

    // Write the 24-bit phase back into the channel registers.
    this._ram[b + 1] = (byte)(phase & 0xFF);
    this._ram[b + 3] = (byte)((phase >> 8) & 0xFF);
    this._ram[b + 5] = (byte)((phase >> 16) & 0xFF);

    var sampleIndex = ((phase >> 16) + waveAddr) & 0xFF;
    var packed = this._ram[(sampleIndex >> 1) & 0x7F];
    var sample = (sampleIndex & 1) != 0 ? (packed >> 4) & 0x0F : packed & 0x0F;
    this._channelOut[channel] = (sample - 8) * volume;
  }

  // Per-channel peak: |sample-8|·volume = 8·15 = 120. NSFPlay attenuates each added channel so the
  // sum stays bounded; normalise by the active count and place a full N163 near 0.5 of full scale.
  public float Output() {
    var active = this.ActiveChannels;
    var sum = 0;
    for (var c = 7; c >= 8 - active; --c)
      sum += this._channelOut[c];
    // sum spans roughly ±(active·120); divide by active to track the multiplexed average.
    return (float)(sum / (double)active * (0.5 / 120.0));
  }

  // ── test hooks ───────────────────────────────────────────────────────────────
  internal byte ReadRam(int addr) => this._ram[addr & 0x7F];
  internal int ActiveChannelCount => this.ActiveChannels;
  internal int ChannelOutput(int channel) => this._channelOut[channel];
}
