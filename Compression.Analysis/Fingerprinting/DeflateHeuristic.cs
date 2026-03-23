namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// Detects raw Deflate streams by parsing BFINAL+BTYPE header bits and validating
/// dynamic Huffman table structure.
/// </summary>
public sealed class DeflateHeuristic : IHeuristic {

  /// <inheritdoc />
  public FingerprintResult? Analyze(ReadOnlySpan<byte> data) {
    if (data.Length < 3) return null;

    // Try parsing as Deflate block header
    // First byte contains BFINAL (bit 0) and BTYPE (bits 1-2)
    var firstByte = data[0];
    var btype = (firstByte >> 1) & 0x03;

    // BTYPE 3 is reserved/invalid
    if (btype == 3) return null;

    // BTYPE 0: stored block — next 4 bytes are LEN and NLEN (complement)
    if (btype == 0) {
      if (data.Length < 5) return null;
      var len = data[1] | (data[2] << 8);
      var nlen = data[3] | (data[4] << 8);
      if ((len ^ nlen) == 0xFFFF) {
        return new("Deflate", 0.85, "Stored block with valid LEN/NLEN complement");
      }
      return null;
    }

    // BTYPE 1: fixed Huffman — any subsequent data could be valid
    if (btype == 1) {
      // The first few bits after header should form valid Huffman codes
      // Fixed Huffman: literals 0-143 use 8-bit codes, 144-255 use 9-bit, etc.
      // Hard to validate without full decode, but BTYPE=1 is a good signal
      return new("Deflate", 0.55, "Fixed Huffman block type detected");
    }

    // BTYPE 2: dynamic Huffman
    // Next 14 bits: HLIT(5) + HDIST(5) + HCLEN(4)
    if (data.Length < 4) return null;

    // Parse bit by bit (LSB first)
    var bits = (uint)data[0] | ((uint)data[1] << 8) | ((uint)data[2] << 16) | ((uint)data[3] << 24);
    var bitPos = 3; // skip BFINAL + BTYPE

    var hlit = (int)((bits >> bitPos) & 0x1F) + 257;
    bitPos += 5;
    var hdist = (int)((bits >> bitPos) & 0x1F) + 1;
    bitPos += 5;
    var hclen = (int)((bits >> bitPos) & 0x0F) + 4;

    // Validate ranges
    if (hlit > 286 || hdist > 30 || hclen > 19)
      return null;

    // Reasonable HLIT/HDIST values suggest this is a valid dynamic Huffman header
    var confidence = 0.65;
    if (hlit >= 257 && hlit <= 286 && hdist >= 1 && hdist <= 30)
      confidence = 0.80;

    return new("Deflate", confidence, $"Dynamic Huffman: HLIT={hlit}, HDIST={hdist}, HCLEN={hclen}");
  }
}
