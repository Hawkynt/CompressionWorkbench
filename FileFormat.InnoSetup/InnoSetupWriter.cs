#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.InnoSetup;

/// <summary>
/// Writes a minimal Inno Setup header. No PE stub is emitted; the reader scans
/// from offset 0 for the Inno signature when it can't parse a PE.
/// Setup.0 is emitted as an empty compressed block — the reader's LZMA +
/// zlib decompression both fail gracefully on empty input, leaving the entry
/// list empty. This is detection-only WORM; producing a functional installer
/// requires bundling a signed PE stub which is out of scope.
/// </summary>
public sealed class InnoSetupWriter {
  public void WriteTo(Stream output, byte[]? embeddedData = null) {
    // ── Inno signature: "Inno Setup Setup Data (1.0)\0" ──
    var sig = Encoding.ASCII.GetBytes(InnoSetupConstants.SignaturePrefix + "1.0)\0");
    output.Write(sig);

    // ── 4-byte CRC + 4-byte compressedSize (both zero) ──
    Span<byte> hdr = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, 0);             // header CRC (not verified)
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], 0);        // compressedSize = 0 (empty Setup.0)
    output.Write(hdr);

    // Optional embedded payload — reader doesn't parse anything past this point
    // when compressedSize is 0, so the bytes are opaque.
    if (embeddedData != null) output.Write(embeddedData);
  }
}
