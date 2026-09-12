#pragma warning disable CS1591
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Zoo;

/// <summary>
/// Random-access in-place modifier for Zoo archives. Zoo entries are linked
/// together via an explicit <c>nextOffset</c> field in each directory header
/// (the archive header at offset 0 carries <c>firstEntryOffset</c> at byte
/// 24); the chain terminates with <c>nextOffset = 0</c>. Add walks to the
/// tail entry, writes a new Stored entry at end-of-stream, and patches the
/// previous tail's <c>nextOffset</c> (or <c>firstEntryOffset</c> for an
/// empty archive) to point at the new header. Remove walks to find the
/// target, shifts trailing bytes forward to compact, then rewrites all
/// affected <c>nextOffset</c> / <c>dataOffset</c> link fields whose values
/// pointed past the removed region.
/// </summary>
public static class ZooModifier {

  // ── Field offsets within a directory entry header ────────────────────────
  // tag(4) + type(1) + method(1) + nextOff(4) + dataOff(4) + ...
  private const int FieldNextOffset = 4 + 1 + 1; // 6
  private const int FieldDataOffset = FieldNextOffset + 4; // 10
  private const int FieldDeleted = 4 + 1 + 1 + 4 + 4 + 2 + 2 + 2 + 4 + 4 + 1 + 1; // 32

  // Archive-header field offsets.
  // Layout: text(20) + magic(4) + firstEntryOffset(4) + minusOffset(4) + majorVer(1) + minorVer(1) = 34.
  private const int ArchiveFieldMagic = 20;
  private const int ArchiveFieldFirstEntryOffset = 24; // after 20-byte text + 4-byte magic
  private const int ArchiveFieldMinusOffset = 28;

  /// <summary>
  /// Appends a Stored entry to the archive. Walks the directory chain to
  /// find the tail, writes a fresh entry at end-of-stream, and patches the
  /// previous-tail's <c>nextOffset</c> link (or <c>firstEntryOffset</c> if
  /// the archive was empty) to point at the new entry.
  /// </summary>
  public static void AddFile(Stream zoo, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(zoo);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!zoo.CanSeek || !zoo.CanWrite)
      throw new ArgumentException("Stream must be seekable and writable.", nameof(zoo));

    // Locate the last entry header (or detect empty archive).
    var tail = FindTailEntry(zoo);

    // Append the new entry at end-of-stream.
    var newHeaderOffset = zoo.Length;
    zoo.Position = newHeaderOffset;
    WriteStoredEntry(zoo, name, data);
    zoo.SetLength(zoo.Position);

    // Patch the previous tail's nextOffset (or the archive header).
    if (tail.LinkFieldOffset >= 0) {
      zoo.Position = tail.LinkFieldOffset;
      WriteUInt32Le(zoo, (uint)newHeaderOffset);
    }

    // For an empty archive we also clear the matching minus-offset slot for
    // tidiness, leaving the writer-emitted negative-offset alone otherwise.
    zoo.Flush();
  }

  /// <summary>
  /// Removes the named entry by unlinking it and compacting. Returns true
  /// when the entry was found. Walks the chain, shifts trailing bytes
  /// forward by the removed entry's size, then rewrites every
  /// <c>nextOffset</c> / <c>dataOffset</c> field whose value originally
  /// pointed past the removed region (those targets all moved by the same
  /// fixed delta).
  /// </summary>
  public static bool RemoveFile(Stream zoo, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(zoo);
    ArgumentNullException.ThrowIfNull(name);
    if (!zoo.CanSeek || !zoo.CanWrite)
      throw new ArgumentException("Stream must be seekable and writable.", nameof(zoo));

    var entries = WalkAllEntries(zoo);
    var victimIndex = -1;
    for (var i = 0; i < entries.Count; i++) {
      if (string.Equals(entries[i].Name, name, StringComparison.OrdinalIgnoreCase)) {
        victimIndex = i;
        break;
      }
    }
    if (victimIndex < 0) return false;

    var victim = entries[victimIndex];
    var removedSize = victim.NextHeaderOffset - victim.HeaderOffset;

    if (wipeData)
      ZeroRange(zoo, victim.HeaderOffset, removedSize);

    var afterVictim = victim.HeaderOffset + removedSize;
    ShiftBytesForward(zoo, src: afterVictim, dst: victim.HeaderOffset, length: zoo.Length - afterVictim);
    zoo.SetLength(zoo.Length - removedSize);

    // 1) Patch the link that pointed at the victim so it skips ahead.
    //    All link targets >= afterVictim move backward by removedSize.
    //    Targets in (HeaderOffset, afterVictim) — none, because nothing else
    //    started inside the victim — are not present.
    long PatchValue(long oldValue) =>
      oldValue >= afterVictim ? oldValue - removedSize : oldValue;

    if (victimIndex == 0) {
      // Victim was the head; firstEntryOffset = next entry's old offset (or 0).
      zoo.Position = ArchiveFieldFirstEntryOffset;
      var newFirst = victim.NextOffsetValue == 0 ? 0u : (uint)PatchValue(victim.NextOffsetValue);
      WriteUInt32Le(zoo, newFirst);
    } else {
      // Victim was mid-chain or tail; rewrite the predecessor's nextOffset.
      // Predecessor's header is at entries[victimIndex - 1].HeaderOffset
      // (unchanged by the shift since it's before the victim).
      var prevHeader = entries[victimIndex - 1].HeaderOffset;
      zoo.Position = prevHeader + FieldNextOffset;
      var newNext = victim.NextOffsetValue == 0 ? 0u : (uint)PatchValue(victim.NextOffsetValue);
      WriteUInt32Le(zoo, newNext);
    }

    // 2) For every entry after the victim, rewrite its nextOffset and
    //    dataOffset fields (their stored targets all moved backward).
    //    Their own header offsets are now shifted, but we know the new
    //    locations: oldHeader - removedSize.
    for (var i = victimIndex + 1; i < entries.Count; i++) {
      var e = entries[i];
      var newHeader = e.HeaderOffset - removedSize;

      var newNext = e.NextOffsetValue == 0 ? 0u : (uint)PatchValue(e.NextOffsetValue);
      var newData = (uint)PatchValue(e.DataOffsetValue);

      zoo.Position = newHeader + FieldNextOffset;
      WriteUInt32Le(zoo, newNext);
      zoo.Position = newHeader + FieldDataOffset;
      WriteUInt32Le(zoo, newData);
    }

    zoo.Flush();
    return true;
  }

  // ── Walking ──────────────────────────────────────────────────────────────

  private readonly record struct WalkedEntry(
    string Name,
    long HeaderOffset,
    long NextHeaderOffset, // physical end of this entry's bytes (start of next or stream end)
    long NextOffsetValue,  // value stored in this entry's nextOffset field
    long DataOffsetValue   // value stored in this entry's dataOffset field
  );

  /// <summary>Result of locating the tail (last entry's nextOffset link).</summary>
  private readonly record struct TailInfo(long LinkFieldOffset);

  private static TailInfo FindTailEntry(Stream zoo) {
    EnsureValidArchive(zoo);

    zoo.Position = ArchiveFieldFirstEntryOffset;
    var first = ReadUInt32Le(zoo);
    if (first == 0)
      return new TailInfo(ArchiveFieldFirstEntryOffset); // empty: patch firstEntryOffset.

    var current = (long)first;
    while (true) {
      var (nextOff, _, _) = ReadEntryFields(zoo, current);
      if (nextOff == 0)
        return new TailInfo(current + FieldNextOffset);
      current = nextOff;
    }
  }

  private static List<WalkedEntry> WalkAllEntries(Stream zoo) {
    EnsureValidArchive(zoo);

    zoo.Position = ArchiveFieldFirstEntryOffset;
    var first = ReadUInt32Le(zoo);
    var result = new List<WalkedEntry>();
    if (first == 0) return result;

    var current = (long)first;
    while (current != 0) {
      var (nextOff, dataOff, name) = ReadEntryFields(zoo, current);
      var physicalEnd = ComputePhysicalEnd(zoo, current, dataOff, nextOff);
      result.Add(new WalkedEntry(name, current, physicalEnd, nextOff, dataOff));
      current = nextOff;
    }
    return result;
  }

  /// <summary>
  /// Returns (nextOffset, dataOffset, effectiveName) for the entry whose
  /// header begins at <paramref name="headerOffset"/>.
  /// </summary>
  private static (long NextOffset, long DataOffset, string Name) ReadEntryFields(Stream zoo, long headerOffset) {
    zoo.Position = headerOffset;
    var tag = ReadUInt32Le(zoo);
    if (tag != ZooConstants.Magic)
      throw new InvalidDataException($"Invalid Zoo entry tag at 0x{headerOffset:X}: 0x{tag:X8}.");

    var type = (byte)zoo.ReadByte();
    /*var method =*/
    zoo.ReadByte();
    var nextOff = ReadUInt32Le(zoo);
    var dataOff = ReadUInt32Le(zoo);

    // Skip date+time+crc16+origSize+compSize+majorVer+minorVer+deleted+structure+commentOff+commentLen
    // = 2+2+2+4+4+1+1+1+1+4+2 = 24 bytes  → total fixed = 38, we've read 14 so far.
    zoo.Position = headerOffset + ZooConstants.DirectoryEntryFixedSize;

    // Short filename (13 bytes, null-padded).
    var shortName = ReadFixedString(zoo, 13);

    string name = shortName;
    if (type == ZooConstants.TypeLongName) {
      var longLen = ReadUInt16Le(zoo);
      var longBytes = new byte[longLen];
      ReadFully(zoo, longBytes);
      name = Encoding.Latin1.GetString(longBytes);
    }

    return (nextOff, dataOff, name);
  }

  /// <summary>
  /// Determines the physical end-of-entry. When a successor exists, that's
  /// its header offset; otherwise we fall back to dataOffset + compressedSize
  /// (re-read so we don't need a second pass).
  /// </summary>
  private static long ComputePhysicalEnd(Stream zoo, long headerOffset, long dataOffset, long nextOffset) {
    if (nextOffset != 0) return nextOffset;

    // Re-read compressedSize at fixed slot 4+1+1+4+4+2+2+2+4 = 24.
    zoo.Position = headerOffset + 24;
    var compSize = ReadUInt32Le(zoo);
    return dataOffset + compSize;
  }

  // ── Writing a fresh Stored entry ─────────────────────────────────────────

  private static void WriteStoredEntry(Stream zoo, string name, byte[] data) {
    var headerStart = zoo.Position;

    // Derive short / optional long name (mirrors ZooWriter logic).
    var shortName = MakeShortName(name);
    var needsLong = !string.Equals(shortName, name, StringComparison.Ordinal) || name.Length > ZooConstants.MaxShortNameLength;
    var shortBytes = MakeShortNameBytes(shortName);
    var longBytes = needsLong ? Encoding.Latin1.GetBytes(name) : Array.Empty<byte>();
    var type = needsLong ? ZooConstants.TypeLongName : ZooConstants.TypeFile;

    var dataOffset = headerStart
                     + ZooConstants.DirectoryEntryFixedSize
                     + 13
                     + (needsLong ? 2 + longBytes.Length : 0);

    var (dosDate, dosTime) = ZooEntry.ToMsDosDateTime(DateTime.UtcNow);
    var crc16 = Crc16.Compute(data);

    WriteUInt32Le(zoo, ZooConstants.Magic);
    zoo.WriteByte(type);
    zoo.WriteByte(ZooConstants.MethodStore);
    WriteUInt32Le(zoo, 0u); // nextOffset (tail terminator)
    WriteUInt32Le(zoo, (uint)dataOffset);
    WriteUInt16Le(zoo, dosDate);
    WriteUInt16Le(zoo, dosTime);
    WriteUInt16Le(zoo, crc16);
    WriteUInt32Le(zoo, (uint)data.Length); // origSize
    WriteUInt32Le(zoo, (uint)data.Length); // compSize (Stored)
    zoo.WriteByte(ZooConstants.MajorVersion);
    zoo.WriteByte(ZooConstants.MinorVersion);
    zoo.WriteByte(0); // deleted
    zoo.WriteByte(0); // structure
    WriteUInt32Le(zoo, 0u); // commentOffset
    WriteUInt16Le(zoo, 0); // commentLength

    zoo.Write(shortBytes, 0, shortBytes.Length);
    if (needsLong) {
      WriteUInt16Le(zoo, (ushort)longBytes.Length);
      zoo.Write(longBytes, 0, longBytes.Length);
    }

    if (data.Length > 0) zoo.Write(data, 0, data.Length);
  }

  private static string MakeShortName(string name) {
    var slash = name.LastIndexOfAny(['/', '\\']);
    if (slash >= 0) name = name[(slash + 1)..];
    if (name.Length > ZooConstants.MaxShortNameLength) name = name[..ZooConstants.MaxShortNameLength];
    return name;
  }

  private static byte[] MakeShortNameBytes(string shortName) {
    var buf = new byte[13];
    var src = Encoding.Latin1.GetBytes(shortName);
    var len = Math.Min(src.Length, 12);
    src.AsSpan(0, len).CopyTo(buf);
    return buf;
  }

  // ── Stream helpers ───────────────────────────────────────────────────────

  private static void EnsureValidArchive(Stream zoo) {
    if (zoo.Length < ZooConstants.ArchiveHeaderSize)
      throw new InvalidDataException("Stream is too short to be a Zoo archive.");
    zoo.Position = 20;
    var tag = ReadUInt32Le(zoo);
    if (tag != ZooConstants.Magic)
      throw new InvalidDataException($"Invalid Zoo archive magic: 0x{tag:X8}.");
  }

  private static void ShiftBytesForward(Stream stream, long src, long dst, long length) {
    if (length <= 0) return;
    var buf = new byte[64 * 1024];
    while (length > 0) {
      var chunk = (int)Math.Min(buf.Length, length);
      stream.Position = src;
      var read = 0;
      while (read < chunk) {
        var n = stream.Read(buf, read, chunk - read);
        if (n <= 0) break;
        read += n;
      }
      stream.Position = dst;
      stream.Write(buf, 0, read);
      src += read;
      dst += read;
      length -= read;
    }
  }

  private static void ZeroRange(Stream s, long offset, long length) {
    if (length <= 0) return;
    var buf = new byte[(int)Math.Min(length, 8192)];
    s.Position = offset;
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buf.Length, remaining);
      s.Write(buf, 0, chunk);
      remaining -= chunk;
    }
  }

  private static ushort ReadUInt16Le(Stream s) {
    var b0 = s.ReadByte();
    var b1 = s.ReadByte();
    if (b0 < 0 || b1 < 0) throw new EndOfStreamException();
    return (ushort)(b0 | (b1 << 8));
  }

  private static uint ReadUInt32Le(Stream s) {
    var b0 = s.ReadByte();
    var b1 = s.ReadByte();
    var b2 = s.ReadByte();
    var b3 = s.ReadByte();
    if ((b0 | b1 | b2 | b3) < 0) throw new EndOfStreamException();
    return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
  }

  private static void WriteUInt16Le(Stream s, ushort value) {
    s.WriteByte((byte)(value & 0xFF));
    s.WriteByte((byte)(value >> 8));
  }

  private static void WriteUInt32Le(Stream s, uint value) {
    s.WriteByte((byte)(value & 0xFF));
    s.WriteByte((byte)((value >> 8) & 0xFF));
    s.WriteByte((byte)((value >> 16) & 0xFF));
    s.WriteByte((byte)(value >> 24));
  }

  private static string ReadFixedString(Stream s, int count) {
    var buf = new byte[count];
    ReadFully(s, buf);
    var len = Array.IndexOf(buf, (byte)0);
    if (len < 0) len = buf.Length;
    return Encoding.Latin1.GetString(buf, 0, len);
  }

  private static void ReadFully(Stream s, byte[] buffer) {
    var off = 0;
    while (off < buffer.Length) {
      var n = s.Read(buffer, off, buffer.Length - off);
      if (n <= 0) throw new EndOfStreamException();
      off += n;
    }
  }
}
