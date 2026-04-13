using System.Buffers.Binary;

namespace Compression.Core.Entropy.Ans;

/// <summary>
/// Range-variant Asymmetric Numeral Systems (rANS) encoder.
/// Encodes symbols in reverse, producing a compressed bitstream that
/// can be decoded forward. Used in AV1, LZFSE, and other modern codecs.
/// </summary>
public sealed class RansEncoder {
  private const uint RansL = 1u << 23;
  private const int ScaleBits = 12;
  private const uint Scale = 1u << ScaleBits;

  /// <summary>
  /// Compresses data using rANS with an order-0 model.
  /// </summary>
  public byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0) return [];

    // Count frequencies.
    var freq = new uint[256];
    foreach (var b in data)
      freq[b]++;

    // Normalize frequencies to sum to Scale.
    var normFreq = NormalizeFrequencies(freq, data.Length);

    // Compute cumulative frequencies.
    var cumFreq = new uint[257];
    for (var i = 0; i < 256; i++)
      cumFreq[i + 1] = cumFreq[i] + normFreq[i];

    // Encode in reverse.
    var outputBytes = new List<byte>();
    var state = RansL;

    for (var i = data.Length - 1; i >= 0; i--) {
      var sym = data[i];
      var f = normFreq[sym];
      var c = cumFreq[sym];

      // Renormalize: output bytes while state is too large.
      while (state >= f * (RansL >> ScaleBits) * (1u << 8)) {
        outputBytes.Add((byte)(state & 0xFF));
        state >>= 8;
      }

      // Encode step: state = (state / f) * Scale + (state % f) + c
      state = (state / f) * Scale + (state % f) + c;
    }

    // Flush final state (4 bytes, big-endian for easier forward reading).
    outputBytes.Add((byte)(state & 0xFF));
    outputBytes.Add((byte)((state >> 8) & 0xFF));
    outputBytes.Add((byte)((state >> 16) & 0xFF));
    outputBytes.Add((byte)((state >> 24) & 0xFF));

    // Reverse because we encoded backwards.
    outputBytes.Reverse();

    return outputBytes.ToArray();
  }

  /// <summary>
  /// Returns the normalized frequency table for the given data.
  /// </summary>
  public static uint[] NormalizeFrequencies(uint[] freq, int totalCount) {
    var norm = new uint[256];
    uint assigned = 0;
    var used = new List<int>();

    for (var i = 0; i < 256; i++) {
      if (freq[i] == 0) continue;
      used.Add(i);
      var nf = (uint)((long)freq[i] * Scale / totalCount);
      if (nf < 1) nf = 1;
      norm[i] = nf;
      assigned += nf;
    }

    // Adjust to exactly match Scale.
    while (assigned != Scale) {
      if (assigned < Scale) {
        var bestIdx = used[0];
        var bestError = double.MinValue;
        foreach (var idx in used) {
          var ideal = (double)freq[idx] * Scale / totalCount;
          var error = ideal - norm[idx];
          if (error > bestError) { bestError = error; bestIdx = idx; }
        }
        norm[bestIdx]++;
        assigned++;
      } else {
        var bestIdx = used[0];
        var bestError = double.MaxValue;
        foreach (var idx in used) {
          if (norm[idx] <= 1) continue;
          var ideal = (double)freq[idx] * Scale / totalCount;
          var error = ideal - norm[idx];
          if (error < bestError) { bestError = error; bestIdx = idx; }
        }
        if (norm[bestIdx] > 1) { norm[bestIdx]--; assigned--; }
        else break;
      }
    }

    return norm;
  }
}
