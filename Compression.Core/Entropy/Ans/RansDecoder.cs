namespace Compression.Core.Entropy.Ans;

/// <summary>
/// Range-variant Asymmetric Numeral Systems (rANS) decoder.
/// </summary>
public sealed class RansDecoder {
  private const uint RansL = 1u << 23;
  private const int ScaleBits = 12;
  private const uint Scale = 1u << ScaleBits;

  /// <summary>
  /// Decompresses rANS-encoded data.
  /// </summary>
  public byte[] Decode(ReadOnlySpan<byte> compressed, int originalSize, uint[] normFreq) {
    if (originalSize == 0) return [];

    // Compute cumulative frequencies.
    var cumFreq = new uint[257];
    for (var i = 0; i < 256; i++)
      cumFreq[i + 1] = cumFreq[i] + normFreq[i];

    // Build reverse lookup table (cumFreq → symbol).
    var lookup = new byte[Scale];
    for (var sym = 0; sym < 256; sym++) {
      for (var j = cumFreq[sym]; j < cumFreq[sym + 1]; j++)
        lookup[j] = (byte)sym;
    }

    // Read initial state from first 4 bytes (big-endian).
    var pos = 0;
    var state = (uint)compressed[pos++] << 24
              | (uint)compressed[pos++] << 16
              | (uint)compressed[pos++] << 8
              | compressed[pos++];

    var output = new byte[originalSize];

    for (var i = 0; i < originalSize; i++) {
      // Decode step.
      var cumVal = state % Scale;
      var sym = lookup[cumVal];
      output[i] = sym;

      var f = normFreq[sym];
      var c = cumFreq[sym];

      // Advance state.
      state = f * (state / Scale) + (state % Scale) - c;

      // Renormalize: read bytes while state is below threshold.
      while (state < RansL && pos < compressed.Length) {
        state = (state << 8) | compressed[pos++];
      }
    }

    return output;
  }
}
