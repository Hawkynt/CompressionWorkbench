namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// Detects LZW compressed data by looking for the characteristic 9-to-12+ bit code growth
/// pattern where code indices are monotonically increasing from 258/259.
/// </summary>
public sealed class LzwHeuristic : IHeuristic {

  /// <inheritdoc />
  public FingerprintResult? Analyze(ReadOnlySpan<byte> data) {
    if (data.Length < 8) return null;

    // LZW typically starts at 9-bit codes (values 0-511)
    // First codes reference single bytes (0-255), then clear code (256), end code (257)
    // New codes start from 258 and increment

    // Try reading 9-bit codes (LSB first, as in GIF/TIFF LZW)
    var validCodes = 0;
    var nextCode = 258;
    var bitWidth = 9;
    var maxCode = (1 << bitWidth) - 1;
    var bitPos = 0;
    var totalCodes = 0;

    while (bitPos + bitWidth <= data.Length * 8 && totalCodes < 100) {
      var code = ReadBits(data, bitPos, bitWidth);
      bitPos += bitWidth;
      totalCodes++;

      if (code < 256) {
        // Literal byte — always valid
        validCodes++;
      }
      else if (code == 256) {
        // Clear code
        validCodes++;
        nextCode = 258;
        bitWidth = 9;
        maxCode = 511;
      }
      else if (code == 257) {
        // End code
        validCodes++;
        break;
      }
      else if (code <= nextCode) {
        validCodes++;
        nextCode++;
        if (nextCode > maxCode && bitWidth < 12) {
          bitWidth++;
          maxCode = (1 << bitWidth) - 1;
        }
      }
      else {
        // Invalid code reference
        break;
      }
    }

    if (totalCodes < 5) return null;
    var ratio = (double)validCodes / totalCodes;
    if (ratio < 0.6) return null;

    var confidence = Math.Min(0.8, 0.4 + ratio * 0.4);
    return new("LZW", confidence, $"Valid 9-bit+ LZW codes: {validCodes}/{totalCodes} ({ratio:P0})");
  }

  private static int ReadBits(ReadOnlySpan<byte> data, int bitPos, int count) {
    var value = 0;
    for (var i = 0; i < count; i++) {
      var byteIdx = (bitPos + i) / 8;
      var bitIdx = (bitPos + i) % 8;
      if (byteIdx >= data.Length) break;
      if ((data[byteIdx] & (1 << bitIdx)) != 0)
        value |= 1 << i;
    }
    return value;
  }
}
