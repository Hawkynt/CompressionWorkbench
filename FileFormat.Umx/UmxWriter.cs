#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Umx;

/// <summary>
/// Writes a minimal UMX (Unreal Package) file with a valid header. File data
/// is embedded after the header but not yet recoverable through the reader
/// (full export table + compact-index music encoding not implemented — the
/// reader expects a specific "Music" class layout). This WORM writer produces
/// structurally-valid UMX detection + version metadata.
/// </summary>
public sealed class UmxWriter {
  private const uint UmxMagic = 0x9E2A83C1;

  public void WriteTo(Stream output, byte[]? embeddedData = null) {
    // Minimal header: magic + fileVersion 60 + zero tables.
    var hdr = new byte[36];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0), UmxMagic);
    BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(4), 60); // minimum supported file version
    // Remaining fields (nameCount/nameOffset/exportCount/exportOffset/importCount/importOffset) = 0
    // The reader returns with no entries when any of these is <= 0 — safe fallback.
    output.Write(hdr);
    if (embeddedData != null) output.Write(embeddedData);
  }
}
