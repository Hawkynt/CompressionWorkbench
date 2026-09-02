#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Mp3;

/// <summary>
/// Walks an MP3 file and emits <see cref="DefragBlockInfo"/> tiles for the
/// ID3v2 header, tag frames, padding, audio data region, APEv2 tag, and
/// ID3v1 trailer. Audio frames are not individually decoded -- just the
/// overall region boundaries are located.
/// </summary>
public static class Mp3LayoutMap {

  private static readonly byte[] ApeTagMagic = "APETAGEX"u8.ToArray();

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream file) {
    ArgumentNullException.ThrowIfNull(file);
    if (file.Length < 4)
      yield break;

    file.Position = 0;
    var audioStart = 0L;

    // ── ID3v2 at start ──────────────────────────────────────────────
    var header = new byte[10];
    if (file.Read(header, 0, 10) == 10 &&
        header[0] == 'I' && header[1] == 'D' && header[2] == '3') {
      var tagSize = DecodeSyncSafe(header, 6);
      var totalTagSize = 10 + tagSize;

      // Emit the 10-byte ID3v2 header
      yield return new DefragBlockInfo(0, 10, DefragBlockKind.MetadataReserved,
        FileName: "ID3v2 header");

      if (tagSize > 0) {
        // Find where actual frames end (padding = trailing zeros within the tag body)
        var framesEnd = FindId3v2FramesEnd(file, 10, tagSize);
        var framesSize = framesEnd - 10;
        var paddingSize = totalTagSize - framesEnd;

        if (framesSize > 0) {
          yield return new DefragBlockInfo(10, framesSize, DefragBlockKind.Used,
            FileName: "ID3v2 tag frames", Classification: DefragBlockClass.Hot);
        }

        if (paddingSize > 0) {
          yield return new DefragBlockInfo(framesEnd, paddingSize, DefragBlockKind.Free,
            FileName: "ID3v2 padding");
        }
      }

      audioStart = totalTagSize;
    }

    // ── Trailing tags (ID3v1 + APEv2) ──────────────────────────────
    var audioEnd = file.Length;
    var id3v1Offset = -1L;
    var apeOffset = -1L;
    var apeSize = 0L;

    // Check for ID3v1 (last 128 bytes)
    if (file.Length >= 128) {
      file.Position = file.Length - 128;
      var tag = new byte[3];
      if (file.Read(tag, 0, 3) == 3 && tag[0] == 'T' && tag[1] == 'A' && tag[2] == 'G') {
        id3v1Offset = file.Length - 128;
        audioEnd = id3v1Offset;
      }
    }

    // Check for APEv2 (before ID3v1 if present, otherwise at EOF)
    var apeSearchEnd = id3v1Offset >= 0 ? id3v1Offset : file.Length;
    if (apeSearchEnd >= 32) {
      // APEv2 footer is 32 bytes; read it
      file.Position = apeSearchEnd - 32;
      var footer = new byte[32];
      if (file.Read(footer, 0, 32) == 32 && MatchesMagic(footer, 0, ApeTagMagic)) {
        // APEv2 footer: magic(8) + version(4) + tagSize(4) + itemCount(4) + flags(4) + reserved(8)
        // tagSize includes items + footer (32 bytes)
        var totalApeSize = BitConverter.ToInt32(footer, 12) + 32; // items+footer + header(32)
        // Check if there's a header flag (bit 29 of flags)
        var flags = BitConverter.ToInt32(footer, 20);
        var hasHeader = (flags & (1 << 29)) != 0;
        if (hasHeader)
          totalApeSize = BitConverter.ToInt32(footer, 12) + 32; // tagSize already includes footer, add header
        else
          totalApeSize = BitConverter.ToInt32(footer, 12); // just items + footer

        // The tag size field in APEv2 footer = size of all items + footer (32 bytes).
        // If header present, total on-disk size = tagSize + 32 (header).
        var apeSizeField = BitConverter.ToInt32(footer, 12);
        apeSize = hasHeader ? apeSizeField + 32 : apeSizeField;
        apeOffset = apeSearchEnd - apeSize;
        if (apeOffset < audioStart)
          apeOffset = -1; // invalid, ignore
        else
          audioEnd = apeOffset;
      }
    }

    // ── Audio frames region ─────────────────────────────────────────
    if (audioEnd > audioStart) {
      yield return new DefragBlockInfo(audioStart, audioEnd - audioStart, DefragBlockKind.Used,
        FileName: "Audio frames", Classification: DefragBlockClass.Normal);
    }

    // ── APEv2 tag ───────────────────────────────────────────────────
    if (apeOffset >= 0 && apeSize > 0) {
      yield return new DefragBlockInfo(apeOffset, apeSize, DefragBlockKind.Used,
        FileName: "APEv2 tag", Classification: DefragBlockClass.Cold);
    }

    // ── ID3v1 tag ───────────────────────────────────────────────────
    if (id3v1Offset >= 0) {
      yield return new DefragBlockInfo(id3v1Offset, 128, DefragBlockKind.Used,
        FileName: "ID3v1 tag", Classification: DefragBlockClass.Frozen);
    }
  }

  /// <summary>
  /// Scans the ID3v2 tag body to find where actual frames end. Returns the
  /// absolute file offset of the first padding byte (or tagBodyEnd if no padding).
  /// </summary>
  internal static long FindId3v2FramesEnd(Stream file, long bodyStart, int tagSize) {
    file.Position = bodyStart;
    var pos = 0;

    // Read the tag body into memory for efficient scanning
    var bodyLen = Math.Min(tagSize, (int)(file.Length - bodyStart));
    var body = new byte[bodyLen];
    var totalRead = 0;
    while (totalRead < bodyLen) {
      var n = file.Read(body, totalRead, bodyLen - totalRead);
      if (n == 0) break;
      totalRead += n;
    }

    // Walk ID3v2 frames: each frame = 4-byte ID + 4-byte size + 2-byte flags + data
    while (pos + 10 <= totalRead) {
      // Frame ID must be uppercase ASCII or digits
      if (body[pos] == 0) break; // padding starts here
      if (!IsValidFrameIdByte(body[pos])) break;

      // Parse frame size (syncsafe for v2.4; we assume syncsafe which is the common case)
      var frameSize = DecodeSyncSafe(body, pos + 4);
      if (frameSize < 0 || pos + 10 + frameSize > totalRead)
        break;

      pos += 10 + frameSize;
    }

    return bodyStart + pos;
  }

  private static bool IsValidFrameIdByte(byte b)
    => (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'0' && b <= (byte)'9');

  internal static int DecodeSyncSafe(byte[] data, int offset)
    => (data[offset] & 0x7F) << 21 |
       (data[offset + 1] & 0x7F) << 14 |
       (data[offset + 2] & 0x7F) << 7 |
       (data[offset + 3] & 0x7F);

  private static bool MatchesMagic(byte[] buffer, int offset, byte[] magic) {
    if (offset + magic.Length > buffer.Length) return false;
    for (var i = 0; i < magic.Length; ++i)
      if (buffer[offset + i] != magic[i])
        return false;
    return true;
  }
}
