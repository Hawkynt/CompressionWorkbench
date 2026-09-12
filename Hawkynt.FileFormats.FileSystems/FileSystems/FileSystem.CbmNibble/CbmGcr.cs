#pragma warning disable CS1591
namespace FileSystem.CbmNibble;

/// <summary>
/// Group-Coded Recording (GCR) codec for Commodore 1541 disks. The 1541
/// records data in 5-bit GCR codes: every four data bits (a nibble) map to a
/// five-bit code chosen so that no code contains more than two consecutive
/// zero bits and never starts/ends with the long run of ones used for the
/// sync mark (0xFF). Four data bytes (eight nibbles) therefore become five
/// GCR bytes (40 bits).
/// </summary>
/// <remarks>
/// Encoding/decoding here is the canonical 1541 scheme as documented in the
/// VICE source and the <c>1541 ROM</c>: see the standard GCR nibble table.
/// </remarks>
internal static class CbmGcr {

  /// <summary>4-bit nibble → 5-bit GCR code.</summary>
  private static readonly byte[] NibbleToGcr = [
    0b01010, 0b01011, 0b10010, 0b10011,
    0b01110, 0b01111, 0b10110, 0b10111,
    0b01001, 0b11001, 0b11010, 0b11011,
    0b01101, 0b11101, 0b11110, 0b10101,
  ];

  /// <summary>5-bit GCR code → 4-bit nibble, or 0xFF when the code is invalid.</summary>
  private static readonly byte[] GcrToNibble = BuildDecodeTable();

  private static byte[] BuildDecodeTable() {
    var table = new byte[32];
    Array.Fill(table, (byte)0xFF);
    for (var nibble = 0; nibble < NibbleToGcr.Length; nibble++)
      table[NibbleToGcr[nibble]] = (byte)nibble;
    return table;
  }

  /// <summary>
  /// Encodes <paramref name="source"/> (a multiple of 4 bytes) into GCR.
  /// Every four input bytes yield five output bytes.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<byte> source) {
    if (source.Length % 4 != 0)
      throw new ArgumentException("GCR encoding works on whole 4-byte groups.", nameof(source));

    var output = new byte[source.Length / 4 * 5];
    var outPos = 0;
    for (var i = 0; i < source.Length; i += 4) {
      // Build a 40-bit value from eight 5-bit GCR codes (two per byte).
      ulong bits = 0;
      for (var b = 0; b < 4; b++) {
        var value = source[i + b];
        bits = (bits << 5) | NibbleToGcr[value >> 4];
        bits = (bits << 5) | NibbleToGcr[value & 0x0F];
      }
      // Emit the 40 bits as five bytes, most-significant first.
      for (var b = 4; b >= 0; b--)
        output[outPos++] = (byte)((bits >> (b * 8)) & 0xFF);
    }
    return output;
  }

  /// <summary>
  /// Decodes <paramref name="source"/> (a multiple of 5 bytes) back to raw
  /// bytes. Every five input bytes yield four output bytes. Invalid GCR codes
  /// throw, which lets callers reject corrupt or mis-synchronised input.
  /// </summary>
  public static byte[] Decode(ReadOnlySpan<byte> source) {
    if (source.Length % 5 != 0)
      throw new ArgumentException("GCR decoding works on whole 5-byte groups.", nameof(source));

    var output = new byte[source.Length / 5 * 4];
    var outPos = 0;
    for (var i = 0; i < source.Length; i += 5) {
      ulong bits = 0;
      for (var b = 0; b < 5; b++)
        bits = (bits << 8) | source[i + b];

      for (var b = 0; b < 4; b++) {
        var hiCode = (int)((bits >> (35 - b * 10)) & 0x1F);
        var loCode = (int)((bits >> (30 - b * 10)) & 0x1F);
        var hi = GcrToNibble[hiCode];
        var lo = GcrToNibble[loCode];
        if (hi == 0xFF || lo == 0xFF)
          throw new InvalidDataException("GCR decode: invalid 5-bit code (not a valid 1541 GCR stream).");
        output[outPos++] = (byte)((hi << 4) | lo);
      }
    }
    return output;
  }
}
