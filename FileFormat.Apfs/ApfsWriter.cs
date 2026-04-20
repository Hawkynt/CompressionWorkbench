#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Apfs;

/// <summary>
/// Writes a minimal APFS container image with a valid NX superblock ("NXSB").
/// The reader is listing-only (Extract returns empty), so WORM creation produces
/// a structurally-valid container that passes detection + listing.
/// </summary>
public sealed class ApfsWriter {
  private const int BlockSize = 4096;
  private const int ObjHeaderSize = 32;
  private const uint NxMagic = 0x4253584E; // "NXSB"

  public void WriteTo(Stream output) {
    // Minimal: one block for NX superblock.
    var image = new byte[BlockSize];
    // NX superblock magic at offset 32 (after object header).
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ObjHeaderSize), NxMagic);
    // Block size at offset 36.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ObjHeaderSize + 4), BlockSize);
    // Block count at offset 40.
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(ObjHeaderSize + 8), 1);
    output.Write(image);
  }
}
