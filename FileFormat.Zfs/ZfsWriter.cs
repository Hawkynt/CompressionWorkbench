#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Zfs;

/// <summary>
/// Writes a minimal ZFS pool image with a valid uberblock. The current reader
/// is detection-only (no file listing/extraction), so this WORM writer produces
/// a structurally-valid ZFS label that passes the reader's parse without
/// throwing. File data is embedded after the label area but isn't currently
/// recoverable through the reader's ZAP/dnode traversal path.
/// </summary>
public sealed class ZfsWriter {
  private const int LabelSize = 256 * 1024;
  private const int UberblockArrayOffset = 128 * 1024;
  private const int UberblockSize = 1024;
  private const ulong UberblockMagic = 0x00BAB10C;

  public void WriteTo(Stream output, byte[]? embeddedData = null) {
    var image = new byte[LabelSize + (embeddedData?.Length ?? 0)];

    // Write one valid uberblock at the start of the uberblock array.
    var ubOff = UberblockArrayOffset;
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(ubOff), UberblockMagic);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(ubOff + 8), 1);  // version
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(ubOff + 16), 1); // txg = 1
    // guid_sum, timestamp, rootbp: left zero (reader only uses magic + txg)

    embeddedData?.CopyTo(image, LabelSize);
    output.Write(image);
  }
}
