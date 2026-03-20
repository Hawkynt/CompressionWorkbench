using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Encodes data using the ZIP Shrink (method 1) algorithm.
/// </summary>
/// <remarks>
/// ZIP Shrink is LZW with 9-13 bit variable-width codes, partial clearing via
/// control code 256 with sub-command 2, and code width increase via sub-command 1.
/// </remarks>
public static class ShrinkEncoder {
  private const int MinBits = 9;
  private const int MaxBits = 13;
  private const int MaxCode = 1 << MaxBits; // 8192
  private const int ControlCode = 256;
  private const byte SubCmdIncrease = 1;
  private const byte SubCmdPartialClear = 2;

  /// <summary>
  /// Compresses data using the ZIP Shrink algorithm.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    var writer = new BitWriter(ms, BitOrder.LsbFirst);

    // Trie for dictionary lookup: (parent code, byte) → child code
    var trie = new Dictionary<(int, byte), int>();

    int currentBits = MinBits;
    int nextCode = 257;

    if (data.IsEmpty) {
      writer.FlushBits();
      return ms.ToArray();
    }

    int currentCode = data[0];
    int i = 1;

    while (i < data.Length) {
      byte nextByte = data[i];
      var key = (currentCode, nextByte);

      if (trie.TryGetValue(key, out int existingCode)) {
        currentCode = existingCode;
        ++i;
      }
      else {
        // Emit current code
        writer.WriteBits((uint)currentCode, currentBits);

        if (nextCode < MaxCode) {
          // Check if we need to increase bit width
          if (nextCode >= (1 << currentBits) && currentBits < MaxBits) {
            writer.WriteBits(ControlCode, currentBits);
            ++currentBits;
            writer.WriteBits(SubCmdIncrease, currentBits);
          }

          trie[key] = nextCode;
          ++nextCode;
        }
        else {
          // Dictionary full — emit partial clear
          writer.WriteBits(ControlCode, currentBits);
          writer.WriteBits(SubCmdPartialClear, currentBits);

          PartialClear(trie, ref nextCode);
        }

        currentCode = nextByte;
        ++i;
      }
    }

    // Emit final code
    writer.WriteBits((uint)currentCode, currentBits);
    writer.FlushBits();

    return ms.ToArray();
  }

  private static void PartialClear(Dictionary<(int, byte), int> trie, ref int nextCode) {
    // Collect which codes are referenced as prefixes
    var referencedAsPrefix = new HashSet<int>();
    foreach (var ((parent, _), child) in trie) {
      if (child >= 257)
        referencedAsPrefix.Add(parent);
    }

    // Remove entries whose codes are not referenced by any other entry
    var toRemove = new List<(int, byte)>();
    foreach (var (key, code) in trie) {
      if (code >= 257 && !referencedAsPrefix.Contains(code))
        toRemove.Add(key);
    }

    foreach (var key in toRemove)
      trie.Remove(key);

    // Find next available code
    var usedCodes = new HashSet<int>(trie.Values);
    nextCode = 257;
    while (nextCode < MaxCode && usedCodes.Contains(nextCode))
      ++nextCode;
  }
}
