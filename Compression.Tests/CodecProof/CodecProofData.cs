using System.Text;

namespace Compression.Tests.CodecProof;

/// <summary>
/// Deterministic inputs shared by the codec proof tests. A Python twin generates the same bytes,
/// so oracle output captured outside the test run can be checked against them.
/// </summary>
internal static class CodecProofData {
  /// <summary>Word-salad text with numbers: compressible, varied.</summary>
  public static byte[] Text(int size) {
    string[] words = ["the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "model", "context", "order", "escape", "symbol", "range"];
    var state = 0x2545F491u;
    var sb = new StringBuilder(size + 64);
    for (var line = 0; sb.Length < size; ++line) {
      var count = 3 + (int)(Next(ref state) % 12);
      for (var w = 0; w < count; ++w)
        sb.Append(words[Next(ref state) % (uint)words.Length]).Append(' ');
      sb.Append(line * 7919 % 100003).Append('\n');
    }

    return Encoding.ASCII.GetBytes(sb.ToString(0, size));
  }

  /// <summary>Random bytes, runs and long-distance repeats interleaved.</summary>
  public static byte[] Mixed(int size) {
    var state = 0x9E3779B9u;
    var b = new byte[size];
    for (var i = 0; i < size; ++i)
      b[i] = (i % 7) switch {
        < 3 => (byte)Next(ref state),
        < 5 => (byte)(i * 31),
        _ => i == 0 ? (byte)0 : b[i - 1 - (int)(Next(ref state) % (uint)Math.Min(i, 5000))],
      };

    return b;
  }

  /// <summary>Incompressible bytes.</summary>
  public static byte[] Random(int size) {
    var state = 0x1234567u;
    var b = new byte[size];
    for (var i = 0; i < size; ++i)
      b[i] = (byte)Next(ref state);

    return b;
  }

  private static uint Next(ref uint state) {
    state ^= state << 13;
    state ^= state >> 17;
    state ^= state << 5;
    return state;
  }
}
