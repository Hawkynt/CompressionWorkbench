#pragma warning disable CS1591
namespace Codec.AdpcmX;

/// <summary>
/// Adobe Flash SWF ADPCM (ffmpeg <c>adpcm_swf</c>). The bitstream opens with a 2-bit field giving
/// the code width minus 2, so codes are 2..5 bits wide (ffmpeg also tolerates the documented 6/7
/// widths). Samples are grouped into blocks of 4096: each block restarts with a per-channel 16-bit
/// initial sample (emitted as the block's first sample) and a 6-bit step index, after which every
/// channel contributes one <c>codeWidth</c>-bit code per sample.
/// <para>
/// A code's top bit is the sign; the remaining bits drive the usual IMA <c>vpdiff</c> accumulation
/// (<c>vpdiff = step &gt;&gt; (codeWidth-1)</c>, then for each remaining bit <c>k</c> add
/// <c>step &gt;&gt; k</c> when that bit is set). The signed <c>vpdiff</c> updates the predictor and the
/// magnitude bits index <see cref="IndexTables"/> to walk the step index.
/// </para>
/// </summary>
public static class Swf {

  /// <summary>Samples per SWF block (per channel) before the header repeats.</summary>
  public const int SamplesPerBlock = 4096;

  /// <summary>
  /// Step-index adjustment tables keyed by <c>codeWidth-2</c> (so [0]=2-bit … [3]=5-bit). Ported
  /// verbatim from ffmpeg <c>swf_index_tables</c>; the magnitude bits of a code index the matching
  /// row.
  /// </summary>
  public static readonly int[][] IndexTables = [
    [-1, 2],
    [-1, -1, 2, 4],
    [-1, -1, -1, -1, 2, 4, 6, 8],
    [-1, -1, -1, -1, -1, -1, -1, -1, 1, 2, 4, 6, 8, 10, 13, 16],
  ];

  /// <summary>
  /// Decodes a complete SWF ADPCM stream into interleaved PCM16. <paramref name="channels"/> is
  /// 1 or 2. Decoding stops when the bitstream is exhausted; a trailing partial block is honoured
  /// for as many samples as the remaining bits allow.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data, int channels) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("SWF ADPCM supports 1 or 2 channels.", nameof(channels));
    if (data.Length < 1)
      return [];

    var reader = new BitReader(data);
    var codeWidth = reader.Read(2) + 2;
    var k0 = 1 << (codeWidth - 1);          // sign bit mask
    var indexTable = IndexTables[codeWidth - 2];

    var predictor = new int[channels];
    var index = new int[channels];
    var output = new List<short>();
    var firstSampleOfBlock = true;
    var samplesInBlock = 0;

    while (reader.BitsLeft >= channels * (firstSampleOfBlock ? 16 + 6 : codeWidth)) {
      if (firstSampleOfBlock) {
        for (var c = 0; c < channels; ++c) {
          predictor[c] = (short)reader.Read(16);
          index[c] = reader.Read(6);
          if (index[c] > 88) index[c] = 88;
        }
        // The block's first sample per channel is the literal initial predictor.
        for (var c = 0; c < channels; ++c)
          output.Add((short)predictor[c]);
        firstSampleOfBlock = false;
        samplesInBlock = 1;
        continue;
      }

      for (var c = 0; c < channels; ++c) {
        var code = reader.Read(codeWidth);
        var step = ImaCore.StepTable[index[c]];
        var sign = (code & k0) != 0;
        var magnitude = code & (k0 - 1);

        var vpdiff = step >> (codeWidth - 1);
        for (var bit = 0; bit < codeWidth - 1; ++bit)
          if ((magnitude & (1 << bit)) != 0)
            vpdiff += step >> (codeWidth - 2 - bit);

        predictor[c] += sign ? -vpdiff : vpdiff;
        predictor[c] = ImaCore.Clamp16(predictor[c]);
        index[c] += indexTable[magnitude];
        if (index[c] < 0) index[c] = 0;
        else if (index[c] > 88) index[c] = 88;
        output.Add((short)predictor[c]);
      }

      if (++samplesInBlock >= SamplesPerBlock)
        firstSampleOfBlock = true;
    }

    return [.. output];
  }

  // Big-endian (MSB-first) bit reader, matching ffmpeg's get_bits ordering.
  private ref struct BitReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPos;

    public BitReader(ReadOnlySpan<byte> data) {
      _data = data;
      _bitPos = 0;
    }

    public int BitsLeft => _data.Length * 8 - _bitPos;

    public int Read(int count) {
      var value = 0;
      for (var i = 0; i < count; ++i) {
        var bytePos = _bitPos >> 3;
        var bit = bytePos < _data.Length ? (_data[bytePos] >> (7 - (_bitPos & 7))) & 1 : 0;
        value = (value << 1) | bit;
        ++_bitPos;
      }
      return value;
    }
  }
}
