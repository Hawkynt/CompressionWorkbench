#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Tap;

/// <summary>
/// In-place TAP modifier — performs O(touched bytes) random-access I/O against
/// a ZX Spectrum TAP tape image.
///
/// <para><b>AddFile</b>: appends a header block + data block pair at EOF.</para>
/// <para><b>RemoveFile</b>: walks the block chain to find the target, then
/// shifts all trailing bytes forward to fill the gap and truncates the stream.</para>
/// </summary>
public static class TapModifier {

  /// <summary>
  /// Appends a file (header block + data block pair) at the end of an existing
  /// TAP image.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data, byte fileType = 3) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    image.Position = image.Length;

    // Write header block (19 bytes).
    WriteHeaderBlock(image, name, data.Length, fileType);

    // Write data block.
    WriteDataBlock(image, data);
  }

  /// <summary>
  /// Removes the first file matching <paramref name="name"/> from the TAP image.
  /// Walks the block chain to find the header+data pair, shifts trailing bytes
  /// forward, and truncates. Returns false if not found.
  /// </summary>
  public static bool RemoveFile(Stream image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    image.Position = 0;
    var imageData = new byte[image.Length];
    if (imageData.Length > 0)
      image.ReadExactly(imageData);

    var pos = 0;
    while (pos + 2 <= imageData.Length) {
      var blockLength = BinaryPrimitives.ReadUInt16LittleEndian(imageData.AsSpan(pos));
      if (blockLength == 0 || pos + 2 + blockLength > imageData.Length) break;

      var flag = imageData[pos + 2];

      if (flag == 0x00 && blockLength == 19) {
        // Header block — read name.
        var entryName = Encoding.ASCII.GetString(imageData, pos + 2 + 2, 10).TrimEnd(' ');

        if (entryName.Equals(name, StringComparison.OrdinalIgnoreCase)) {
          // Found it. The header block spans [pos .. pos+2+19).
          // The data block immediately follows.
          var headerStart = pos;
          var headerEnd = pos + 2 + blockLength;
          var removeEnd = headerEnd;

          // Check if the next block is a data block (flag=0xFF).
          if (headerEnd + 2 <= imageData.Length) {
            var nextBlockLen = BinaryPrimitives.ReadUInt16LittleEndian(imageData.AsSpan(headerEnd));
            if (nextBlockLen > 0 && headerEnd + 2 + nextBlockLen <= imageData.Length) {
              var nextFlag = imageData[headerEnd + 2];
              if (nextFlag == 0xFF) {
                removeEnd = headerEnd + 2 + nextBlockLen;
              }
            }
          }

          // Remove the range [headerStart..removeEnd) by shifting trailing bytes.
          var tailLength = imageData.Length - removeEnd;
          if (tailLength > 0)
            Array.Copy(imageData, removeEnd, imageData, headerStart, tailLength);

          var newLength = imageData.Length - (removeEnd - headerStart);
          image.Position = 0;
          image.Write(imageData, 0, newLength);
          image.SetLength(newLength);
          return true;
        }
      }

      pos += 2 + blockLength;
    }

    return false;
  }

  // ── Block writers ─────────────────────────────────────────────────────

  private static void WriteHeaderBlock(Stream output, string name, int dataLength, byte fileType) {
    var block = new byte[19];
    block[0] = 0x00; // flag: header
    block[1] = fileType;

    var nameBytes = Encoding.ASCII.GetBytes(name.Length > 10 ? name[..10] : name);
    nameBytes.CopyTo(block, 2);
    for (var i = nameBytes.Length; i < 10; i++)
      block[2 + i] = 0x20;

    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(12), (ushort)dataLength);

    byte cs = 0;
    for (var i = 0; i < 18; i++)
      cs ^= block[i];
    block[18] = cs;

    WriteUInt16LE(output, (ushort)block.Length);
    output.Write(block);
  }

  private static void WriteDataBlock(Stream output, byte[] data) {
    var blockLength = data.Length + 2;
    WriteUInt16LE(output, (ushort)blockLength);

    byte cs = 0xFF;
    output.WriteByte(0xFF);

    foreach (var b in data) {
      cs ^= b;
      output.WriteByte(b);
    }

    output.WriteByte(cs);
  }

  private static void WriteUInt16LE(Stream output, ushort value) {
    Span<byte> buf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
    output.Write(buf);
  }
}
