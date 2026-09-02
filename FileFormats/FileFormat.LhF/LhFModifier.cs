#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Dictionary.Lzh;

namespace FileFormat.LhF;

/// <summary>
/// Random-access in-place modifier for LhF (LhFloppy) Amiga disk archives.
/// LhF differs from LZH/LHA — there is no header chain. The file layout is:
/// <code>
///   "LhF\0"           4 bytes
///   trackCount        uint16 BE
///   flags             uint16 BE
///   per-track repeat:
///     trackNumber     uint16 BE
///     compSize        uint32 BE  (== TrackSize when stored uncompressed)
///     checksum        uint16 BE
///     data            compSize bytes
/// </code>
/// Add appends a new track block before EOF and bumps the trackCount field;
/// Remove walks the track list, locates the target by name (the conventional
/// <c>track_NNN.raw</c> produced by <see cref="LhFReader"/>), and shifts
/// trailing bytes forward to compact, then decrements trackCount.
/// </summary>
public static class LhFModifier {
  private const int HeaderSize = 8;            // "LhF\0" + count + flags
  private const int TrackHeaderSize = 8;       // trackNum + compSize + checksum
  /// <summary>
  /// Defines the track size constant value.
  /// </summary>
public const int TrackSize = LhFWriter.TrackSize;

  /// <summary>
  /// Appends a new track to an existing LhF archive. The track number is parsed
  /// from <paramref name="name"/> (e.g., <c>track_007.raw</c>); if the name does
  /// not encode a track number, one past the highest existing track is used.
  /// I/O cost is one full sequential walk to find EOF + the new track's bytes.
  /// </summary>
  public static void AddFile(Stream lhf, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(lhf);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    ValidateHeader(lhf);

    var (eofOffset, trackCount, maxTrackNum) = WalkTracks(lhf);
    var trackNumber = ParseTrackNumber(name) ?? (maxTrackNum + 1);

    // Pad / truncate to TrackSize, just like LhFWriter.AddTrack.
    var trackBuf = new byte[TrackSize];
    var copyLen = Math.Min(data.Length, TrackSize);
    Array.Copy(data, 0, trackBuf, 0, copyLen);

    // Try to compress; mirror writer's "stored when not smaller" shortcut.
    byte[] payload;
    try {
      var encoder = new LzhEncoder(positionBits: 13);
      var compressed = encoder.Encode(trackBuf);
      payload = compressed.Length >= trackBuf.Length ? trackBuf : compressed;
    } catch {
      payload = trackBuf;
    }

    var sum = 0;
    foreach (var b in trackBuf) sum = (sum + b) & 0xFFFF;

    lhf.Position = eofOffset;
    Span<byte> u16 = stackalloc byte[2];
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)trackNumber);
    lhf.Write(u16);
    BinaryPrimitives.WriteInt32BigEndian(u32, payload.Length);
    lhf.Write(u32);
    BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)sum);
    lhf.Write(u16);
    lhf.Write(payload);
    lhf.SetLength(lhf.Position);

    WriteTrackCount(lhf, trackCount + 1);
  }

  /// <summary>
  /// Removes the named track. Returns true if found. Walks the track list,
  /// shifts trailing bytes forward to compact, then truncates and decrements
  /// the trackCount field.
  /// </summary>
  public static bool RemoveFile(Stream lhf, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(lhf);
    ArgumentNullException.ThrowIfNull(name);

    ValidateHeader(lhf);

    var locator = LocateTrack(lhf, name);
    if (!locator.Found) return false;

    if (wipeData)
      ZeroRange(lhf, locator.BlockOffset, locator.BlockSize);

    var afterEntry = locator.BlockOffset + locator.BlockSize;
    var bytesToShift = lhf.Length - afterEntry;
    if (bytesToShift > 0) {
      var buf = new byte[64 * 1024];
      var src = afterEntry;
      var dst = locator.BlockOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buf.Length, bytesToShift);
        lhf.Position = src;
        var read = 0;
        while (read < chunk) {
          var n = lhf.Read(buf, read, chunk - read);
          if (n <= 0) break;
          read += n;
        }
        lhf.Position = dst;
        lhf.Write(buf, 0, read);
        src += read;
        dst += read;
        bytesToShift -= read;
      }
    }
    lhf.SetLength(lhf.Length - locator.BlockSize);

    // Re-read and decrement trackCount.
    var (_, trackCount, _) = WalkTracks(lhf);
    // WalkTracks naturally yields the *new* count by walking what's left, but
    // the stored count field still holds the old value. Recompute by walking,
    // then write that value back.
    WriteTrackCount(lhf, trackCount);
    return true;
  }

  // ── Internals ─────────────────────────────────────────────────────────

  private readonly record struct TrackLocator(bool Found, long BlockOffset, long BlockSize);

  private static void ValidateHeader(Stream lhf) {
    if (lhf.Length < HeaderSize)
      throw new InvalidDataException("LhF: file too small.");
    Span<byte> magic = stackalloc byte[4];
    lhf.Position = 0;
    var read = ReadFully(lhf, magic);
    if (read < 4 || magic[0] != (byte)'L' || magic[1] != (byte)'h' || magic[2] != (byte)'F' || magic[3] != 0)
      throw new InvalidDataException("LhF: invalid magic.");
  }

  private static (long EofOffset, int TrackCount, int MaxTrackNum) WalkTracks(Stream lhf) {
    lhf.Position = HeaderSize;
    Span<byte> trackHdr = stackalloc byte[TrackHeaderSize];
    var trackCount = 0;
    var maxTrackNum = -1;

    while (lhf.Position < lhf.Length) {
      var entryStart = lhf.Position;
      var read = ReadFully(lhf, trackHdr);
      if (read < trackHdr.Length) {
        // truncated → treat as EOF at the malformed entry start
        return (entryStart, trackCount, maxTrackNum);
      }
      var trackNum = BinaryPrimitives.ReadUInt16BigEndian(trackHdr[..2]);
      var compSize = BinaryPrimitives.ReadInt32BigEndian(trackHdr.Slice(2, 4));
      if (compSize < 0 || lhf.Position + compSize > lhf.Length)
        return (entryStart, trackCount, maxTrackNum);

      lhf.Position += compSize;
      trackCount++;
      if (trackNum > maxTrackNum) maxTrackNum = trackNum;
    }
    return (lhf.Length, trackCount, maxTrackNum);
  }

  private static TrackLocator LocateTrack(Stream lhf, string targetName) {
    lhf.Position = HeaderSize;
    Span<byte> trackHdr = stackalloc byte[TrackHeaderSize];

    while (lhf.Position < lhf.Length) {
      var blockStart = lhf.Position;
      var read = ReadFully(lhf, trackHdr);
      if (read < trackHdr.Length) break;
      var trackNum = BinaryPrimitives.ReadUInt16BigEndian(trackHdr[..2]);
      var compSize = BinaryPrimitives.ReadInt32BigEndian(trackHdr.Slice(2, 4));
      if (compSize < 0 || lhf.Position + compSize > lhf.Length) break;

      var blockSize = (long)TrackHeaderSize + compSize;
      var name = $"track_{trackNum:D3}.raw";
      if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
        return new TrackLocator(true, blockStart, blockSize);

      lhf.Position = blockStart + blockSize;
    }
    return new TrackLocator(false, 0, 0);
  }

  private static int? ParseTrackNumber(string archiveName) {
    var stem = Path.GetFileNameWithoutExtension(archiveName);
    var underscore = stem.LastIndexOf('_');
    if (underscore < 0) return null;
    return int.TryParse(stem[(underscore + 1)..], out var n) ? n : null;
  }

  private static void WriteTrackCount(Stream lhf, int trackCount) {
    if (trackCount > ushort.MaxValue)
      throw new InvalidOperationException($"LhF supports at most {ushort.MaxValue} tracks.");
    Span<byte> u16 = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)trackCount);
    lhf.Position = 4; // immediately after "LhF\0"
    lhf.Write(u16);
  }

  private static int ReadFully(Stream s, Span<byte> buf) {
    var read = 0;
    while (read < buf.Length) {
      var n = s.Read(buf[read..]);
      if (n <= 0) break;
      read += n;
    }
    return read;
  }

  private static void ZeroRange(Stream s, long offset, long length) {
    var buf = new byte[(int)Math.Min(length, 8192)];
    s.Position = offset;
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buf.Length, remaining);
      s.Write(buf, 0, chunk);
      remaining -= chunk;
    }
  }
}
