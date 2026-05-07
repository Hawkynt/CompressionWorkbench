#pragma warning disable CS1591
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileFormat.AlZip;

/// <summary>
/// Random-access in-place modifier for ALZ archives. Add appends a new entry
/// over the trailing CLZ end-of-archive marker, then rewrites the marker.
/// Remove walks the local-header chain, locates the named entry, and shifts
/// trailing bytes forward to compact (ALZ has no central directory, so
/// compaction is required).
/// </summary>
/// <remarks>
/// ALZ entries are length-prefixed local headers (BLZ\x01) chained together
/// and terminated by a 4-byte CLZ\x02 marker. The format is similar to LHA
/// in that there is no global TOC — header walking is required for both
/// operations.
/// </remarks>
public static class AlZipModifier {

  private const uint LocalSig = 0x015A4C42; // BLZ\x01 LE
  private const uint EndSig = 0x025A4C43;   // CLZ\x02 LE
  private static readonly byte[] AlzMagic = [0x41, 0x4C, 0x5A, 0x01];

  /// <summary>
  /// Appends a file to the archive at the position of the existing CLZ end
  /// marker, then writes a new end marker. Walks the entry chain once to
  /// locate the marker.
  /// </summary>
  public static void AddFile(Stream alz, string name, byte[] data, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(alz);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var endOffset = FindEndMarkerOffset(alz);
    alz.Position = endOffset;

    WriteEntry(alz, name, data, lastModified ?? DateTime.Now);
    WriteUInt32LE(alz, EndSig);
    alz.SetLength(alz.Position);
  }

  /// <summary>
  /// Removes the named entry. Returns true if found. Walks the chain to
  /// locate the entry, optionally wipes its bytes, then shifts trailing
  /// bytes forward to compact.
  /// </summary>
  public static bool RemoveFile(Stream alz, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(alz);
    ArgumentNullException.ThrowIfNull(name);

    var locator = LocateEntry(alz, name);
    if (!locator.Found) return false;

    if (wipeData)
      ZeroRange(alz, locator.HeaderOffset, locator.TotalEntrySize);

    var afterEntry = locator.HeaderOffset + locator.TotalEntrySize;
    var bytesToShift = alz.Length - afterEntry;
    if (bytesToShift > 0) {
      var buf = new byte[64 * 1024];
      var src = afterEntry;
      var dst = locator.HeaderOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buf.Length, bytesToShift);
        alz.Position = src;
        var read = 0;
        while (read < chunk) {
          var n = alz.Read(buf, read, chunk - read);
          if (n <= 0) break;
          read += n;
        }
        alz.Position = dst;
        alz.Write(buf, 0, read);
        src += read;
        dst += read;
        bytesToShift -= read;
      }
    }
    alz.SetLength(alz.Length - locator.TotalEntrySize);
    return true;
  }

  // ── Header walking ──────────────────────────────────────────────────────

  private readonly record struct EntryLocator(bool Found, long HeaderOffset, long TotalEntrySize);

  private static long FindEndMarkerOffset(Stream alz) {
    SkipMagic(alz);
    Span<byte> sig = stackalloc byte[4];
    while (alz.Position < alz.Length) {
      var sigOffset = alz.Position;
      if (!ReadFully(alz, sig)) return sigOffset;
      var s = ReadUInt32LE(sig);
      if (s == EndSig) return sigOffset;
      if (s != LocalSig) return sigOffset; // treat as terminator
      var entrySize = ReadEntryBodySize(alz);
      alz.Position = sigOffset + 4 + entrySize;
    }
    return alz.Length;
  }

  private static EntryLocator LocateEntry(Stream alz, string targetName) {
    SkipMagic(alz);
    Span<byte> sig = stackalloc byte[4];
    while (alz.Position < alz.Length) {
      var entryStart = alz.Position;
      if (!ReadFully(alz, sig)) break;
      var s = ReadUInt32LE(sig);
      if (s != LocalSig) break;

      var headerStart = alz.Position;
      var (entryBodySize, name) = ReadHeaderForLocate(alz);
      var totalEntrySize = 4 + entryBodySize;

      if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
        return new EntryLocator(true, entryStart, totalEntrySize);

      alz.Position = headerStart + entryBodySize;
    }
    return new EntryLocator(false, 0, 0);
  }

  private static void SkipMagic(Stream alz) {
    alz.Position = 0;
    Span<byte> magic = stackalloc byte[4];
    if (!ReadFully(alz, magic) || !magic.SequenceEqual(AlzMagic))
      throw new InvalidDataException("Not a valid ALZ archive.");
  }

  /// <summary>
  /// Reads an entry body starting at the current stream position (just past
  /// the 4-byte LocalSig) and returns the total body size in bytes
  /// (header bytes + filename bytes + compressed data).
  /// </summary>
  private static long ReadEntryBodySize(Stream alz) {
    var headerStart = alz.Position;
    var (bodySize, _) = ReadHeaderForLocate(alz);
    alz.Position = headerStart;
    return bodySize;
  }

  /// <summary>
  /// Reads the variable-width entry header at the current position and
  /// returns (totalBodySize, filename). After this call the stream is
  /// positioned at the start of compressed data.
  /// </summary>
  private static (long BodySize, string Name) ReadHeaderForLocate(Stream alz) {
    var headerStart = alz.Position;
    Span<byte> buf2 = stackalloc byte[2];
    if (!ReadFully(alz, buf2)) return (0, "");
    var filenameLen = (ushort)(buf2[0] | (buf2[1] << 8));

    var attr = (byte)alz.ReadByte();
    Span<byte> buf4 = stackalloc byte[4];
    if (!ReadFully(alz, buf4)) return (0, "");           // dosTime
    var descriptor = (byte)alz.ReadByte();
    var sizeWidth = (descriptor & 0xF0) switch {
      0x10 => 2,
      0x20 => 4,
      0x40 => 8,
      _ => 4,
    };
    alz.ReadByte(); // reserved
    alz.ReadByte(); // method
    if (!ReadFully(alz, buf4)) return (0, "");           // crc32

    Span<byte> sizeBuf = stackalloc byte[8];
    if (!ReadFully(alz, sizeBuf[..sizeWidth])) return (0, "");
    var compressedSize = ReadSize(sizeBuf, sizeWidth);
    if (!ReadFully(alz, sizeBuf[..sizeWidth])) return (0, "");
    // uncompressed not needed here
    _ = ReadSize(sizeBuf, sizeWidth);

    var nameBuf = new byte[filenameLen];
    if (!ReadFully(alz, nameBuf)) return (0, "");
    var name = Encoding.UTF8.GetString(nameBuf).Replace('\\', '/');

    var headerBytes = alz.Position - headerStart;
    var bodySize = headerBytes + compressedSize;

    // Skip past compressed payload to set stream to next-entry boundary.
    alz.Position = headerStart + bodySize;
    _ = attr; // suppress unused
    return (bodySize, name);
  }

  // ── Writing one entry ──────────────────────────────────────────────────

  private static void WriteEntry(Stream alz, string name, byte[] data, DateTime lastModified) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var crc = Crc32.Compute(data);
    var deflated = data.Length > 0 ? DeflateCompressor.Compress(data) : [];
    byte method;
    byte[] payload;
    if (deflated.Length > 0 && deflated.Length < data.Length) {
      method = 2;
      payload = deflated;
    } else {
      method = 0;
      payload = data;
    }

    WriteUInt32LE(alz, LocalSig);
    WriteUInt16LE(alz, (ushort)nameBytes.Length);
    alz.WriteByte(0x20);                            // attr: archive
    WriteUInt32LE(alz, AlZipReader.DateTimeToDosTime(lastModified));
    alz.WriteByte(0x20);                            // descriptor: 4-byte sizes
    alz.WriteByte(0);                               // reserved
    alz.WriteByte(method);
    WriteUInt32LE(alz, crc);
    WriteUInt32LE(alz, (uint)payload.Length);
    WriteUInt32LE(alz, (uint)data.Length);
    alz.Write(nameBytes);
    alz.Write(payload);
  }

  // ── Helpers ─────────────────────────────────────────────────────────────

  private static long ReadSize(ReadOnlySpan<byte> buf, int width) => width switch {
    2 => buf[0] | (buf[1] << 8),
    4 => buf[0] | ((long)buf[1] << 8) | ((long)buf[2] << 16) | ((long)buf[3] << 24),
    8 => buf[0] | ((long)buf[1] << 8) | ((long)buf[2] << 16) | ((long)buf[3] << 24) |
         ((long)buf[4] << 32) | ((long)buf[5] << 40) | ((long)buf[6] << 48) | ((long)buf[7] << 56),
    _ => throw new InvalidDataException($"Invalid ALZ size width {width}"),
  };

  private static uint ReadUInt32LE(ReadOnlySpan<byte> b) =>
    (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));

  private static void WriteUInt16LE(Stream s, ushort v) {
    Span<byte> b = stackalloc byte[2];
    b[0] = (byte)v; b[1] = (byte)(v >> 8);
    s.Write(b);
  }

  private static void WriteUInt32LE(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    b[0] = (byte)v; b[1] = (byte)(v >> 8); b[2] = (byte)(v >> 16); b[3] = (byte)(v >> 24);
    s.Write(b);
  }

  private static bool ReadFully(Stream s, Span<byte> buf) {
    var read = 0;
    while (read < buf.Length) {
      var n = s.Read(buf[read..]);
      if (n <= 0) return false;
      read += n;
    }
    return true;
  }

  private static bool ReadFully(Stream s, byte[] buf) {
    var read = 0;
    while (read < buf.Length) {
      var n = s.Read(buf, read, buf.Length - read);
      if (n <= 0) return false;
      read += n;
    }
    return true;
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
