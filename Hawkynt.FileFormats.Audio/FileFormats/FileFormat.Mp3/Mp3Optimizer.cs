#pragma warning disable CS1591
namespace FileFormat.Mp3;

/// <summary>
/// Compacts oversized ID3v2 padding in an MP3 file. If the ID3v2 tag has more
/// than 256 bytes of trailing padding, the audio data is shifted forward and
/// the tag is rewritten with exactly 256 bytes of padding (standard convention
/// to allow future in-place edits without a full file rewrite).
/// </summary>
public static class Mp3Optimizer {

  /// <summary>
  /// The target padding size after optimization. 256 bytes is the
  /// conventional allowance for future in-place ID3v2 edits.
  /// </summary>
  public const int TargetPadding = 256;

  /// <summary>
  /// Optimizes the MP3 file in <paramref name="file"/> by compacting ID3v2 padding.
  /// The stream must be readable, writable, and seekable.
  /// If no ID3v2 tag exists or padding is already &lt;= 256 bytes, this is a no-op.
  /// </summary>
  public static void Optimize(Stream file) => Optimize(file, null);

  /// <summary>
  /// Optimizes the MP3 file with an optional metadata placement profile.
  /// The MP3 format requires ID3v2 at the start per spec, so the profile
  /// does not affect ID3v2 tag position — only the padding compaction is
  /// performed.
  /// </summary>
  public static void Optimize(Stream file, Compression.Registry.MetadataPlacementProfile? profile) {
    ArgumentNullException.ThrowIfNull(file);
    if (file.Length < 10)
      return;

    // Read ID3v2 header
    file.Position = 0;
    var header = new byte[10];
    if (file.Read(header, 0, 10) != 10)
      return;

    if (header[0] != 'I' || header[1] != 'D' || header[2] != '3')
      return;

    var tagSize = DecodeSyncSafe(header, 6);
    var totalTagSize = 10 + tagSize;
    if (totalTagSize > file.Length)
      return;

    // Find where frames end within the tag body
    var framesEnd = Mp3LayoutMap.FindId3v2FramesEnd(file, 10, tagSize);
    var framesSize = framesEnd - 10;
    var currentPadding = totalTagSize - framesEnd;

    if (currentPadding <= TargetPadding)
      return; // already compact enough

    // Calculate the new tag size and shift amount
    var newTagSize = (int)framesSize + TargetPadding;
    var newTotalTagSize = 10 + newTagSize;
    var shiftAmount = totalTagSize - newTotalTagSize; // bytes to remove

    // Shift audio data forward (toward the beginning of the file)
    ShiftData(file, totalTagSize, newTotalTagSize, file.Length - totalTagSize);

    // Truncate the file
    file.SetLength(file.Length - shiftAmount);

    // Write new padding (zeros) after the frames
    file.Position = framesEnd;
    var padding = new byte[TargetPadding];
    file.Write(padding, 0, TargetPadding);

    // Rewrite the syncsafe size in the ID3v2 header
    file.Position = 6;
    var sizeBytes = EncodeSyncSafe(newTagSize);
    file.Write(sizeBytes, 0, 4);

    file.Flush();
  }

  /// <summary>
  /// Shifts <paramref name="count"/> bytes from <paramref name="sourceOffset"/>
  /// to <paramref name="destOffset"/>. Handles forward shifts (dest &lt; source)
  /// by copying in chunks from the beginning.
  /// </summary>
  private static void ShiftData(Stream file, long sourceOffset, long destOffset, long count) {
    const int bufferSize = 81920;
    var buffer = new byte[bufferSize];
    var remaining = count;
    var readPos = sourceOffset;
    var writePos = destOffset;

    while (remaining > 0) {
      var toRead = (int)Math.Min(bufferSize, remaining);
      file.Position = readPos;
      var bytesRead = file.Read(buffer, 0, toRead);
      if (bytesRead == 0) break;

      file.Position = writePos;
      file.Write(buffer, 0, bytesRead);

      readPos += bytesRead;
      writePos += bytesRead;
      remaining -= bytesRead;
    }
  }

  private static int DecodeSyncSafe(byte[] data, int offset)
    => (data[offset] & 0x7F) << 21 |
       (data[offset + 1] & 0x7F) << 14 |
       (data[offset + 2] & 0x7F) << 7 |
       (data[offset + 3] & 0x7F);

  private static byte[] EncodeSyncSafe(int value) => [
    (byte)((value >> 21) & 0x7F),
    (byte)((value >> 14) & 0x7F),
    (byte)((value >> 7) & 0x7F),
    (byte)(value & 0x7F),
  ];
}
