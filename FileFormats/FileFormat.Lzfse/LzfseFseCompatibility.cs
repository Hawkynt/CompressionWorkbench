using System.Numerics;

namespace FileFormat.Lzfse;

/// <summary>
/// Builds LZFSE decoder tables using the permissive frequency validation required by
/// Apple's wire format: normalized frequencies may use fewer than the available states,
/// but must never exceed them.
/// </summary>
/// <remarks>
/// Apple's <c>fse_check_freq</c> explicitly accepts <c>sum(freq) &lt;= nstates</c>.
/// Decoder tables therefore leave any unused trailing states zero-initialized. Valid
/// streams only transition through initialized states; malformed streams that wander
/// into unused states remain bounded by the managed state and bitstream checks.
/// </remarks>
internal static class LzfseFseCompatibility {
  internal static LzfseFse.DecoderEntry[] BuildDecoderTable(ReadOnlySpan<ushort> frequencies, int stateCount) {
    if (!BitOperations.IsPow2((uint)stateCount))
      throw new ArgumentOutOfRangeException(nameof(stateCount), "FSE state count must be a power of two.");

    var table = new LzfseFse.DecoderEntry[stateCount];
    var stateLeadingZeros = BitOperations.LeadingZeroCount((uint)stateCount);
    var offset = 0;

    for (var symbol = 0; symbol < frequencies.Length; ++symbol) {
      var frequency = frequencies[symbol];
      if (frequency == 0)
        continue;
      if (offset + frequency > stateCount)
        throw new InvalidDataException("LZFSE FSE frequency table exceeds its state space.");

      var k = BitOperations.LeadingZeroCount((uint)frequency) - stateLeadingZeros;
      var split = (2 * stateCount >> k) - frequency;
      for (var j = 0; j < frequency; ++j) {
        int bitCount;
        int delta;
        if (j < split) {
          bitCount = k;
          delta = ((frequency + j) << k) - stateCount;
        } else {
          bitCount = k - 1;
          delta = (j - split) << (k - 1);
        }

        table[offset + j] = new(
          checked((byte)bitCount),
          checked((byte)symbol),
          checked((short)delta));
      }

      offset += frequency;
    }

    return table;
  }

  internal static LzfseFse.ValueDecoderEntry[] BuildValueDecoderTable(
      ReadOnlySpan<ushort> frequencies,
      int stateCount,
      ReadOnlySpan<byte> extraBits,
      ReadOnlySpan<int> baseValues) {
    if (!BitOperations.IsPow2((uint)stateCount))
      throw new ArgumentOutOfRangeException(nameof(stateCount), "FSE state count must be a power of two.");
    if (frequencies.Length != extraBits.Length || frequencies.Length != baseValues.Length)
      throw new ArgumentException("FSE value-table arrays must have the same length.");

    var table = new LzfseFse.ValueDecoderEntry[stateCount];
    var stateLeadingZeros = BitOperations.LeadingZeroCount((uint)stateCount);
    var offset = 0;

    for (var symbol = 0; symbol < frequencies.Length; ++symbol) {
      var frequency = frequencies[symbol];
      if (frequency == 0)
        continue;
      if (offset + frequency > stateCount)
        throw new InvalidDataException("LZFSE FSE value frequency table exceeds its state space.");

      var k = BitOperations.LeadingZeroCount((uint)frequency) - stateLeadingZeros;
      var split = (2 * stateCount >> k) - frequency;
      var valueBits = extraBits[symbol];
      for (var j = 0; j < frequency; ++j) {
        int stateBits;
        int delta;
        if (j < split) {
          stateBits = k;
          delta = ((frequency + j) << k) - stateCount;
        } else {
          stateBits = k - 1;
          delta = (j - split) << (k - 1);
        }

        table[offset + j] = new(
          checked((byte)(stateBits + valueBits)),
          valueBits,
          checked((short)delta),
          baseValues[symbol]);
      }

      offset += frequency;
    }

    return table;
  }
}
