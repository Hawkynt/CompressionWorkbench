namespace Compression.Core.Dictionary.Lzx;

/// <summary>
/// Constants for the LZX compression algorithm, as used in Microsoft CAB and WIM formats.
/// </summary>
public static class LzxConstants {
  /// <summary>Minimum window size exponent (2^15 = 32 KB).</summary>
  public const int MinWindowBits = 15;

  /// <summary>Maximum window size exponent (2^21 = 2 MB).</summary>
  public const int MaxWindowBits = 21;

  /// <summary>Number of literal symbols (0–255).</summary>
  public const int NumChars = 256;

  /// <summary>Minimum match length in bytes.</summary>
  public const int MinMatch = 2;

  /// <summary>Maximum match length in bytes.</summary>
  public const int MaxMatch = 257;

  /// <summary>Number of length tree symbols (secondary lengths 0–248).</summary>
  public const int NumLengthSymbols = 249;

  /// <summary>Number of aligned offset tree symbols.</summary>
  public const int NumAlignedSymbols = 8;

  /// <summary>Number of pre-tree symbols (used to encode Huffman code lengths).</summary>
  public const int NumPreTreeSymbols = 20;

  /// <summary>Bits per pre-tree entry in the block header.</summary>
  public const int PreTreeBits = 4;

  /// <summary>Number of length header slots encoded in the main tree symbol (0–7).</summary>
  public const int NumLengthHeaders = 8;

  /// <summary>
  /// Minimum match distance that can be encoded as a new (non-repeated) offset.
  /// Position slot 3 has base = 3, and the LZX offset bias is +2, so the smallest
  /// distance expressible as a non-repeat slot is 3 + 0 + 2 = 5.
  /// Distances 2–4 may only be encoded if they equal a current R0/R1/R2 value.
  /// </summary>
  public const int MinNonRepeatDistance = 5;

  /// <summary>Block type value: verbatim block.</summary>
  public const int BlockTypeVerbatim = 1;

  /// <summary>Block type value: aligned offset block.</summary>
  public const int BlockTypeAligned = 2;

  /// <summary>Block type value: uncompressed block.</summary>
  public const int BlockTypeUncompressed = 3;

  /// <summary>Default block size in uncompressed bytes (32 KB).</summary>
  public const int DefaultBlockSize = 32768;

  /// <summary>
  /// Maximum Huffman code length for the main, length, and aligned trees.
  /// LZX uses at most 16-bit codes.
  /// </summary>
  public const int MaxHuffmanBits = 16;

  /// <summary>
  /// Maximum Huffman code length for the pre-tree (4 bits per pre-tree symbol in the stream,
  /// but the pre-tree itself has codes up to 16 bits).
  /// </summary>
  public const int MaxPreTreeBits = 16;

  /// <summary>Number of position slots for a 32 KB (2^15) window.</summary>
  public const int PositionSlots15 = 30;

  /// <summary>Number of position slots for a 64 KB (2^16) window.</summary>
  public const int PositionSlots16 = 32;

  /// <summary>Number of position slots for a 128 KB (2^17) window.</summary>
  public const int PositionSlots17 = 34;

  /// <summary>Number of position slots for a 256 KB (2^18) window.</summary>
  public const int PositionSlots18 = 36;

  /// <summary>Number of position slots for a 512 KB (2^19) window.</summary>
  public const int PositionSlots19 = 38;

  /// <summary>Number of position slots for a 1 MB (2^20) window.</summary>
  public const int PositionSlots20 = 42;

  /// <summary>Number of position slots for a 2 MB (2^21) window.</summary>
  public const int PositionSlots21 = 50;

  /// <summary>
  /// Returns the number of position slots for a given window size exponent.
  /// </summary>
  /// <param name="windowBits">Window size exponent (15–21).</param>
  /// <returns>Number of position slots.</returns>
  /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="windowBits"/> is out of range.</exception>
  public static int GetPositionSlotCount(int windowBits) => windowBits switch {
    15 => LzxConstants.PositionSlots15,
    16 => LzxConstants.PositionSlots16,
    17 => LzxConstants.PositionSlots17,
    18 => LzxConstants.PositionSlots18,
    19 => LzxConstants.PositionSlots19,
    20 => LzxConstants.PositionSlots20,
    21 => LzxConstants.PositionSlots21,
    _ => throw new ArgumentOutOfRangeException(nameof(windowBits), windowBits,
           $"Window bits must be between {LzxConstants.MinWindowBits} and {LzxConstants.MaxWindowBits}.")
  };

  /// <summary>
  /// Computes the position slot for a given offset (0-based distance into the window).
  /// Slot 0 = offset 0, slot 1 = offset 1, slots 2–3 = offsets 2–3, then slot k covers
  /// the range [base_k, base_{k+1}).
  /// </summary>
  /// <param name="offset">The match offset (0-based, i.e. distance − 1 for repeats).</param>
  /// <returns>Position slot index.</returns>
  public static int OffsetToSlot(int offset) {
    if (offset < 4) return offset;
    // Each pair of slots adds one footer bit. Slot 4 starts at offset 4.
    var bits = 1;
    var v = offset >> 1;
    while (v > 1) { 
      v >>= 1; 
      ++bits;
    }
    // slot = 2 * bits + ((offset >> (bits-1)) & 1) ... but simpler:
    // base for slot (2*bits) is (1 << bits), base for slot (2*bits+1) is (3 << (bits-1))
    // Use integer log2 to find the slot pair.
    var log2 = 0;
    var tmp = offset;
    while (tmp > 1) {
      tmp >>= 1;
      ++log2;
    }
    // slot = 2*(log2-1) + ((offset >> (log2-1)) & 1) + 2
    // simplified: slot = 2*log2 + ((offset >> log2) & ... no.
    // Correct formula: slot 2k has base 2^k, slot 2k+1 has base 3*2^(k-1) for k>=2.
    // offset in [2^log2, 2^log2 + 2^(log2-1)) → slot 2*log2
    // offset in [2^log2 + 2^(log2-1), 2^(log2+1)) → slot 2*log2+1
    var halfBit = (offset >> (log2 - 1)) & 1;
    return 2 * log2 + halfBit;
  }

  /// <summary>
  /// Returns the base offset and footer bits for a given position slot.
  /// For slots 0–3 the footer is 0. For each subsequent pair the footer grows by 1.
  /// </summary>
  /// <param name="slot">Position slot index.</param>
  /// <param name="baseOffset">The base offset value for this slot.</param>
  /// <param name="footerBits">The number of footer bits for this slot.</param>
  public static void GetSlotInfo(int slot, out int baseOffset, out int footerBits) {
    if (slot < 4) {
      baseOffset = slot;
      footerBits = 0;
      return;
    }

    // slot 4 → footer=1, base=4
    // slot 5 → footer=1, base=6
    // slot 6 → footer=2, base=8
    // slot 7 → footer=2, base=12
    // ...
    // For slot >= 4: footerBits = (slot / 2) - 1
    // baseOffset determined by summing previous ranges.
    footerBits = (slot / 2) - 1;
    // base for slot k: slots 0–3 each cover 1, then each pair of slots covers 2^footerBits offsets
    // base = 2 + sum_{i=1}^{footerBits-1} 2 * 2^i + (slot is even ? 0 : 2^footerBits)
    // Simplification using the pattern:
    // base(slot 2k)   = 2^k         (k >= 2)
    // base(slot 2k+1) = 3 * 2^(k-1) (k >= 2)
    var k = slot / 2;  // k >= 2
    if (slot % 2 == 0)
      baseOffset = 1 << k;
    else
      baseOffset = 3 << (k - 1);
  }

  /// <summary>
  /// Builds and returns the pre-computed position slot lookup table.
  /// Index is the offset value; value is the slot number.
  /// Valid for offsets 0 to 65535 (covers all window sizes up to 2 MB via the split approach).
  /// </summary>
  /// <returns>Array of length 65536 mapping offset → slot.</returns>
  public static int[] BuildPositionSlotTable() {
    // The maximum slot-base fits in 65536 for the small-offset table.
    // For offsets beyond 65535 (large windows), the slot is determined at runtime.
    var table = new int[65536];
    for (var i = 0; i < table.Length; ++i)
      table[i] = OffsetToSlot(i);

    return table;
  }
}
