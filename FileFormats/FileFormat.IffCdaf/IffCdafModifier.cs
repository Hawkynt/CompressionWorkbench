#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.IffCdaf;

/// <summary>
/// Random-access in-place modifier for IFF-CDAF archives. Each "entry" is
/// a pair of FNAM (filename) and FDAT (file data) chunks under the FORM/CDAF
/// container. Add appends a new FNAM+FDAT pair just before the FORM body
/// ends and updates the FORM size. Remove locates the chunk pair, shifts
/// trailing bytes forward, and updates the FORM size.
/// </summary>
public static class IffCdafModifier {

  /// <summary>
  /// Appends a (FNAM, FDAT) chunk pair to the archive and updates the FORM
  /// size header.
  /// </summary>
  public static void AddFile(Stream cdaf, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(cdaf);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    ValidateForm(cdaf);

    // FORM body ends at min(8 + formSize, file length). New chunks go there.
    var formBodyEnd = ReadFormBodyEnd(cdaf);

    cdaf.Position = formBodyEnd;
    WriteChunk(cdaf, "FNAM"u8, Encoding.ASCII.GetBytes(name + '\0'));
    WriteChunk(cdaf, "FDAT"u8, data);
    var newEnd = cdaf.Position;
    cdaf.SetLength(newEnd);

    // Update FORM size = newEnd - 8.
    UpdateFormSize(cdaf, newEnd - 8);
  }

  /// <summary>
  /// Removes a named entry. Returns true if found. Removes both the FNAM
  /// chunk and its following FDAT chunk, shifts trailing bytes, and updates
  /// the FORM size.
  /// </summary>
  public static bool RemoveFile(Stream cdaf, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(cdaf);
    ArgumentNullException.ThrowIfNull(name);

    ValidateForm(cdaf);
    var formBodyEnd = ReadFormBodyEnd(cdaf);

    var locator = LocateEntry(cdaf, formBodyEnd, name);
    if (!locator.Found) return false;

    if (wipeData)
      ZeroRange(cdaf, locator.StartOffset, locator.TotalSize);

    var afterEntry = locator.StartOffset + locator.TotalSize;
    var bytesToShift = cdaf.Length - afterEntry;
    if (bytesToShift > 0) {
      var buf = new byte[64 * 1024];
      var src = afterEntry;
      var dst = locator.StartOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buf.Length, bytesToShift);
        cdaf.Position = src;
        var read = 0;
        while (read < chunk) {
          var n = cdaf.Read(buf, read, chunk - read);
          if (n <= 0) break;
          read += n;
        }
        cdaf.Position = dst;
        cdaf.Write(buf, 0, read);
        src += read;
        dst += read;
        bytesToShift -= read;
      }
    }

    var newLength = cdaf.Length - locator.TotalSize;
    cdaf.SetLength(newLength);
    UpdateFormSize(cdaf, newLength - 8);
    return true;
  }

  // ── Header helpers ──────────────────────────────────────────────────────

  private readonly record struct EntryLocator(bool Found, long StartOffset, long TotalSize);

  private static void ValidateForm(Stream cdaf) {
    cdaf.Position = 0;
    Span<byte> hdr = stackalloc byte[12];
    if (!ReadFully(cdaf, hdr) ||
        hdr[0] != (byte)'F' || hdr[1] != (byte)'O' || hdr[2] != (byte)'R' || hdr[3] != (byte)'M' ||
        hdr[8] != (byte)'C' || hdr[9] != (byte)'D' || hdr[10] != (byte)'A' || hdr[11] != (byte)'F')
      throw new InvalidDataException("Not a valid IFF-CDAF archive.");
  }

  private static long ReadFormBodyEnd(Stream cdaf) {
    cdaf.Position = 4;
    Span<byte> sizeBuf = stackalloc byte[4];
    ReadFully(cdaf, sizeBuf);
    var formSize = BinaryPrimitives.ReadInt32BigEndian(sizeBuf);
    return Math.Min(8 + (long)formSize, cdaf.Length);
  }

  private static void UpdateFormSize(Stream cdaf, long newFormSize) {
    if (newFormSize < 4) newFormSize = 4; // FORM size must include CDAF type
    cdaf.Position = 4;
    Span<byte> sizeBuf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(sizeBuf, (int)newFormSize);
    cdaf.Write(sizeBuf);
  }

  private static EntryLocator LocateEntry(Stream cdaf, long limit, string targetName) {
    var pos = 12L;
    string? currentName = null;
    long currentNameStart = 0;
    long currentNameTotal = 0;
    Span<byte> hdr = stackalloc byte[8];

    while (pos + 8 <= limit) {
      var chunkStart = pos;
      cdaf.Position = pos;
      if (!ReadFully(cdaf, hdr)) break;
      var chunkId = Encoding.ASCII.GetString(hdr[..4]);
      var chunkSize = BinaryPrimitives.ReadInt32BigEndian(hdr[4..]);
      if (chunkSize < 0) break;
      var paddedSize = chunkSize + (chunkSize & 1);
      var chunkTotal = 8L + paddedSize;
      if (pos + chunkTotal > limit + 1) break;

      if (chunkId == "FNAM") {
        var nameBytes = new byte[chunkSize];
        cdaf.Position = pos + 8;
        ReadFully(cdaf, nameBytes);
        var nullTerm = Array.IndexOf(nameBytes, (byte)0);
        var nameLen = nullTerm >= 0 ? nullTerm : nameBytes.Length;
        currentName = Encoding.ASCII.GetString(nameBytes, 0, nameLen);
        currentNameStart = chunkStart;
        currentNameTotal = chunkTotal;
      } else if (chunkId == "FDAT") {
        if (currentName != null &&
            string.Equals(currentName, targetName, StringComparison.OrdinalIgnoreCase)) {
          // The "entry" spans both FNAM and FDAT.
          return new EntryLocator(true, currentNameStart, currentNameTotal + chunkTotal);
        }
        currentName = null;
      }
      pos += chunkTotal;
    }
    return new EntryLocator(false, 0, 0);
  }

  // ── Chunk write ─────────────────────────────────────────────────────────

  private static void WriteChunk(Stream s, ReadOnlySpan<byte> id4, byte[] data) {
    s.Write(id4);
    Span<byte> sizeBuf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(sizeBuf, data.Length);
    s.Write(sizeBuf);
    s.Write(data);
    if ((data.Length & 1) != 0) s.WriteByte(0); // word-align
  }

  // ── Common ──────────────────────────────────────────────────────────────

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
