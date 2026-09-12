#pragma warning disable CS1591
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Ha;

/// <summary>
/// Random-access in-place modifier for HA (Harri Hirvola) archives. The HA
/// format is a 2-byte "HA" magic followed by chained per-entry blocks; there
/// is no central directory and no explicit end-of-archive marker — the
/// reader simply walks until EOF. Add appends a new Stored entry at the end
/// of the file; Remove walks the chain, locates the target by name, and
/// shifts trailing bytes forward to compact.
/// </summary>
public static class HaModifier {

  // Per-entry layout (HA, method 0/Store):
  //   byte 0:    version<<4 | method (low nibble)
  //   bytes 1..4 (4):   compressed_size (uint32 LE)
  //   bytes 5..8 (4):   original_size  (uint32 LE)
  //   bytes 9..12 (4):  crc32          (uint32 LE)
  //   bytes 13..16 (4): ms-dos date/time (uint32 LE)
  //   bytes 17..N: filename, NUL-terminated (Latin-1)
  //   bytes N+1..N+1+compressed_size: data
  // Total entry size = 17 + nameBytes + 1 + compressed_size.

  private const int FixedHeaderSize = 17;

  /// <summary>
  /// Appends a Stored entry to the archive. Walks the existing entry chain
  /// to find the EOF position, writes a new entry at that offset, and
  /// truncates. I/O cost is one full sequential header walk plus the new
  /// entry's bytes.
  /// </summary>
  public static void AddFile(Stream ha, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(ha);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var eofOffset = FindEofOffset(ha);
    ha.Position = eofOffset;
    WriteStoredEntry(ha, name, data);
    ha.SetLength(ha.Position);
  }

  /// <summary>
  /// Removes the named entry. Returns true if found. Walks the chain to
  /// locate the entry, then shifts trailing bytes forward to compact (HA
  /// has no central directory, so compaction is required). The "HA" magic
  /// at offset 0 is preserved.
  /// </summary>
  public static bool RemoveFile(Stream ha, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(ha);
    ArgumentNullException.ThrowIfNull(name);

    var locator = LocateEntry(ha, name);
    if (!locator.Found) return false;

    if (wipeData)
      ZeroRange(ha, locator.BlockOffset, locator.BlockSize);

    var afterEntry = locator.BlockOffset + locator.BlockSize;
    var bytesToShift = ha.Length - afterEntry;
    if (bytesToShift > 0) {
      var buf = new byte[64 * 1024];
      var src = afterEntry;
      var dst = locator.BlockOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buf.Length, bytesToShift);
        ha.Position = src;
        var read = 0;
        while (read < chunk) {
          var n = ha.Read(buf, read, chunk - read);
          if (n <= 0) break;
          read += n;
        }
        ha.Position = dst;
        ha.Write(buf, 0, read);
        src += read;
        dst += read;
        bytesToShift -= read;
      }
    }
    ha.SetLength(ha.Length - locator.BlockSize);
    return true;
  }

  // ── Chain walking ─────────────────────────────────────────────────────

  private static long FindEofOffset(Stream ha) {
    if (ha.Length < HaConstants.Magic.Length) {
      // Empty/uninitialized stream: write magic now so subsequent entries follow it.
      ha.Position = 0;
      ha.Write(HaConstants.Magic, 0, HaConstants.Magic.Length);
      return ha.Position;
    }

    ha.Position = 0;
    if (ha.ReadByte() != HaConstants.Magic[0] || ha.ReadByte() != HaConstants.Magic[1])
      throw new InvalidDataException("Stream does not begin with HA magic.");

    while (ha.Position < ha.Length) {
      if (!TryReadEntryHeader(ha, out _, out var compressedSize, out var dataOffset))
        return ha.Position; // truncated — treat current pos as EOF
      var nextStart = dataOffset + compressedSize;
      if (nextStart > ha.Length) return ha.Length; // overrun — clamp to EOF
      ha.Position = nextStart;
    }
    return ha.Length;
  }

  private readonly record struct EntryLocator(bool Found, long BlockOffset, long BlockSize);

  private static EntryLocator LocateEntry(Stream ha, string targetName) {
    if (ha.Length < HaConstants.Magic.Length) return new EntryLocator(false, 0, 0);

    ha.Position = 0;
    if (ha.ReadByte() != HaConstants.Magic[0] || ha.ReadByte() != HaConstants.Magic[1])
      return new EntryLocator(false, 0, 0);

    while (ha.Position < ha.Length) {
      var blockStart = ha.Position;
      if (!TryReadEntryHeader(ha, out var name, out var compressedSize, out var dataOffset))
        break;
      var nextStart = dataOffset + compressedSize;
      if (nextStart > ha.Length) break;

      if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
        return new EntryLocator(true, blockStart, nextStart - blockStart);

      ha.Position = nextStart;
    }
    return new EntryLocator(false, 0, 0);
  }

  private static bool TryReadEntryHeader(Stream ha, out string fileName, out uint compressedSize, out long dataOffset) {
    fileName = string.Empty;
    compressedSize = 0;
    dataOffset = 0;

    if (ha.Position + FixedHeaderSize > ha.Length) return false;

    Span<byte> hdr = stackalloc byte[FixedHeaderSize];
    var read = 0;
    while (read < hdr.Length) {
      var n = ha.Read(hdr[read..]);
      if (n <= 0) return false;
      read += n;
    }

    // hdr[0] = version<<4 | method (we don't care which here)
    compressedSize = (uint)(hdr[1] | hdr[2] << 8 | hdr[3] << 16 | hdr[4] << 24);
    // hdr[5..8] originalSize, hdr[9..12] crc32, hdr[13..16] dosDateTime — unused for chain walking.

    // Read NUL-terminated filename.
    var sb = new StringBuilder();
    while (true) {
      var b = ha.ReadByte();
      if (b < 0) return false;
      if (b == 0) break;
      sb.Append((char)b);
    }
    fileName = sb.ToString();
    dataOffset = ha.Position;
    return true;
  }

  // ── Block writing ─────────────────────────────────────────────────────

  private static void WriteStoredEntry(Stream ha, string name, byte[] data) {
    var crc = data.Length > 0 ? Crc32.Compute(data) : 0u;
    var dosDateTime = HaEntry.EncodeMsDosDateTime(DateTime.Now);
    var nameBytes = Encoding.Latin1.GetBytes(name);

    var w = new BinaryWriter(ha, Encoding.Latin1, leaveOpen: true);
    w.Write((byte)(HaConstants.MethodStore & 0x0F)); // version 0, method 0
    w.Write((uint)data.Length); // compressed size
    w.Write((uint)data.Length); // original size
    w.Write(crc);
    w.Write(dosDateTime);
    w.Write(nameBytes);
    w.Write((byte)0); // NUL terminator

    if (data.Length > 0) ha.Write(data, 0, data.Length);
  }

  // ── Helpers ───────────────────────────────────────────────────────────

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
