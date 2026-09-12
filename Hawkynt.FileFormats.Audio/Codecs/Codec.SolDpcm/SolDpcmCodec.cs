#pragma warning disable CS1591
namespace Codec.SolDpcm;

/// <summary>
/// Sierra SOL DPCM, the differential coding used by Sierra's <c>.sol</c> sound effects.
/// Two 8-bit variants exist — an "old" and a "new" delta table, ported from FFmpeg's
/// <c>dpcm.c</c> — plus a 16-bit mode that simply integrates signed byte deltas. Each
/// nibble (low nibble first) indexes the table; bit 3 of the nibble is the sign and
/// bits 0–2 the magnitude. The running predictor is an 8-bit accumulator (wrapped) in
/// the 8-bit modes, surfaced to 16-bit by mapping the unsigned-8 sample as
/// <c>(sample - 128) &lt;&lt; 8</c>.
/// </summary>
public static class SolDpcmCodec {

  /// <summary>Which SOL DPCM variant to decode.</summary>
  public enum Mode {
    /// <summary>8-bit DPCM with the legacy ("old") delta table.</summary>
    Old8,
    /// <summary>8-bit DPCM with the revised ("new") delta table.</summary>
    New8,
    /// <summary>16-bit DPCM: each input byte is a signed delta added to a 16-bit accumulator.</summary>
    Sixteen,
  }

  // FFmpeg dpcm.c sol_table_old / sol_table_new (8 magnitude entries; sign from bit 3).
  private static readonly int[] SolTableOld = [0x0, 0x1, 0x2, 0x3, 0x6, 0xA, 0xF, 0x15];

  private static readonly int[] SolTableNew = [0x0, 0x1, 0x2, 0x3, 0x6, 0xA, 0xF, 0x15];

  // FFmpeg dpcm.c sol_table_16: signed 16-bit step magnitudes indexed by the full byte.
  private static readonly int[] SolTable16 = [
    0x000, 0x008, 0x010, 0x020, 0x030, 0x040, 0x050, 0x060, 0x070, 0x080,
    0x090, 0x0A0, 0x0B0, 0x0C0, 0x0D0, 0x0E0, 0x0F0, 0x100, 0x110, 0x120,
    0x130, 0x140, 0x150, 0x160, 0x170, 0x180, 0x190, 0x1A0, 0x1B0, 0x1C0,
    0x1D0, 0x1E0, 0x1F0, 0x200, 0x208, 0x210, 0x218, 0x220, 0x228, 0x230,
    0x238, 0x240, 0x248, 0x250, 0x258, 0x260, 0x268, 0x270, 0x278, 0x280,
    0x288, 0x290, 0x298, 0x2A0, 0x2A8, 0x2B0, 0x2B8, 0x2C0, 0x2C8, 0x2D0,
    0x2D8, 0x2E0, 0x2E8, 0x2F0, 0x2F8, 0x300, 0x308, 0x310, 0x318, 0x320,
    0x328, 0x330, 0x338,
  ];

  /// <summary>
  /// Decodes a SOL DPCM byte stream into signed 16-bit PCM. In the 8-bit modes each
  /// byte yields two samples (low nibble first); in 16-bit mode each byte yields one
  /// sample.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data, Mode mode) {
    if (mode == Mode.Sixteen)
      return Decode16(data);

    var table = mode == Mode.Old8 ? SolTableOld : SolTableNew;
    var output = new short[data.Length * 2];
    var o = 0;
    var sample = 0x80; // 8-bit accumulator starts centred.
    foreach (var b in data) {
      sample = StepNibble(sample, (byte)(b & 0x0F), table);
      output[o++] = (short)((sample - 0x80) << 8);
      sample = StepNibble(sample, (byte)(b >> 4), table);
      output[o++] = (short)((sample - 0x80) << 8);
    }
    return output;
  }

  private static int StepNibble(int sample, byte nibble, int[] table) {
    var delta = table[nibble & 0x07];
    if ((nibble & 0x08) != 0)
      sample -= delta;
    else
      sample += delta;
    return sample & 0xFF; // wrap as an 8-bit unsigned accumulator
  }

  private static short[] Decode16(ReadOnlySpan<byte> data) {
    var output = new short[data.Length];
    var predictor = 0;
    for (var i = 0; i < data.Length; ++i) {
      var b = data[i];
      var delta = SolTable16[b & 0x7F];
      if ((b & 0x80) != 0)
        predictor -= delta;
      else
        predictor += delta;
      predictor = Math.Clamp(predictor, -32768, 32767);
      output[i] = (short)predictor;
    }
    return output;
  }

  /// <summary>Decodes raw 8-bit unsigned PCM (no DPCM) to signed 16-bit.</summary>
  public static short[] DecodePcm8(ReadOnlySpan<byte> data) {
    var output = new short[data.Length];
    for (var i = 0; i < data.Length; ++i)
      output[i] = (short)((data[i] - 0x80) << 8);
    return output;
  }
}
