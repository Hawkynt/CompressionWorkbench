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
    // Which code slots are currently occupied. After a partial clear the freed
    // slots are scattered through 257..MaxCode-1, so the next-code search must
    // skip occupied slots in increasing order — the decoder reuses them in that
    // exact same order, which keeps both dictionaries in lock-step.
    var slotUsed = new bool[MaxCode];

    var currentBits = MinBits;
    var nextCode = 257;

    if (data.IsEmpty) {
      writer.FlushBits();
      return ms.ToArray();
    }

    int currentCode = data[0];
    var i = 1;

    while (i < data.Length) {
      var nextByte = data[i];
      var key = (currentCode, nextByte);

      if (trie.TryGetValue(key, out var existingCode)) {
        currentCode = existingCode;
        ++i;
      }
      else {
        // Emit current code
        writer.WriteBits((uint)currentCode, currentBits);

        if (nextCode < MaxCode) {
          // Check if we need to increase bit width
          if (nextCode >= (1 << currentBits) && currentBits < MaxBits) {
            // The decoder reads both the control code and its sub-command at the
            // current width and only widens afterwards, so emit both at the old
            // width and bump last — otherwise the sub-command desyncs by one bit
            // at every 9->10->...->13 boundary.
            writer.WriteBits(ControlCode, currentBits);
            writer.WriteBits(SubCmdIncrease, currentBits);
            ++currentBits;
          }

          trie[key] = nextCode;
          slotUsed[nextCode] = true;
          AdvanceNextCode(slotUsed, ref nextCode);
        }
        else {
          // Dictionary full — emit partial clear
          writer.WriteBits(ControlCode, currentBits);
          writer.WriteBits(SubCmdPartialClear, currentBits);

          PartialClear(trie, slotUsed);
          // Restart the free-slot search from the bottom; the decoder does the
          // same, so the first reusable slot — and every one after it — matches.
          nextCode = 257;
          AdvanceNextCode(slotUsed, ref nextCode);
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

  /// <summary>Advances <paramref name="nextCode"/> to the next free slot (or MaxCode if none remain).</summary>
  private static void AdvanceNextCode(bool[] slotUsed, ref int nextCode) {
    while (nextCode < MaxCode && slotUsed[nextCode])
      ++nextCode;
  }

  private static void PartialClear(Dictionary<(int, byte), int> trie, bool[] slotUsed) {
    // A code survives the clear only if it is used as the prefix of another code
    // (an interior trie node); leaf codes are evicted. This must match the
    // decoder's survivor rule exactly.
    var referencedAsPrefix = new HashSet<int>();
    foreach (var ((parent, _), _) in trie)
      referencedAsPrefix.Add(parent);

    var toRemove = new List<(int, byte)>();
    foreach (var (key, code) in trie) {
      if (code >= 257 && !referencedAsPrefix.Contains(code))
        toRemove.Add(key);
    }

    foreach (var key in toRemove)
      trie.Remove(key);

    // Rebuild the occupancy map from the survivors.
    Array.Clear(slotUsed, 0, slotUsed.Length);
    foreach (var code in trie.Values)
      slotUsed[code] = true;
  }
}
