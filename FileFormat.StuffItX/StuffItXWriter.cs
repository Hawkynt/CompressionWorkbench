#pragma warning disable CS1591
using System.Text;

namespace FileFormat.StuffItX;

/// <summary>
/// Writes a minimal StuffIt X header. Full element-stream emission with
/// P2 varint encoding and element catalog is not implemented — the reader's
/// element parser is complex and the format is proprietary. This writer
/// produces a valid "StuffIt!" magic envelope that passes detection; file
/// data is embedded but not recoverable through the reader's element parser.
/// </summary>
public sealed class StuffItXWriter {
  private const int MinHeaderSize = 0x60;
  private static readonly byte[] MagicFull = "StuffIt!"u8.ToArray();

  public void WriteTo(Stream output, byte[]? embeddedData = null) {
    var hdr = new byte[MinHeaderSize];
    MagicFull.CopyTo(hdr, 0);
    // Remaining header fields left zero. Reader tolerates this and returns an
    // empty entry list, which is correct WORM behaviour for detection-only.
    output.Write(hdr);
    if (embeddedData != null) output.Write(embeddedData);
  }
}
