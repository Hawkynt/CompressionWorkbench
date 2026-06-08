#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;

namespace FileFormat.Acronis;

/// <summary>
/// Record type identifiers in the Acronis classic .tib record stream, per upstream RE
/// (https://github.com/dennisss/acronis-tib, src/win/record.ts).
/// </summary>
public enum AcronisRecordType : byte {
  /// <summary>XML configuration (key/value pairs).</summary>
  Config = 101,
  /// <summary>Anchor record for a single FileEntry — followed by FileMetaA/B/C blocks. Payload layout not understood upstream.</summary>
  FirstFileMetaRecord = 102,
  /// <summary>Follows FirstFileMetaRecord. Payload layout not understood upstream.</summary>
  FileMetaA = 1,
  /// <summary>Follows FileMetaA. Payload layout not understood upstream.</summary>
  FileMetaB = 2,
  /// <summary>Follows FileMetaB. Payload layout not understood upstream.</summary>
  FileMetaC = 5,
  /// <summary>Directory + file listing for the slice.</summary>
  Listing = 103,
  /// <summary>End-of-stream marker.</summary>
  EndTrailer = 104,
  /// <summary>Per-file record index — points at the Blob records holding the file's data.</summary>
  RecordIndex = 108,
  /// <summary>File data block.</summary>
  Blob = 109,
  /// <summary>Inserted after every blob for a file. Payload (if any) is opaque.</summary>
  BlobSuffix = 110,
}

/// <summary>Entry parsed out of a <see cref="AcronisRecordType.Listing"/> record.</summary>
public sealed record AcronisFileEntry(
  string Path,
  string Name,
  string ShortName,
  DateTime? Time,
  long FileSize,
  long FileSize2,
  long MetaOffset
);

/// <summary>
/// One handle inside a <see cref="AcronisRecordType.RecordIndex"/> payload — points at a
/// <see cref="AcronisRecordType.Blob"/> record that holds (part of) a file's decompressed data.
/// </summary>
/// <param name="StartOffset">
/// Offset (uncompressed bytes) within the destination file at which this blob's decompressed
/// data is positioned. Used to order/concatenate fragments when a file spans multiple blobs.
/// </param>
/// <param name="RecordOffset">
/// Offset of the referenced Blob record relative to the END of the volume header (absolute
/// archive position = <c>HeaderLength + RecordOffset</c>). Layout per upstream RE.
/// </param>
/// <param name="Md5">16-byte MD5 of the decompressed Blob payload — used for integrity checks.</param>
public sealed record AcronisRecordHandle(long StartOffset, long RecordOffset, byte[] Md5);

/// <summary>Parsed <see cref="AcronisRecordType.RecordIndex"/> payload (record type 108).</summary>
/// <param name="TotalSize">
/// Total uncompressed size covered by all handles — equals the file's logical size per upstream RE.
/// </param>
/// <param name="Handles">Per-blob handles in the order they appear in the payload.</param>
public sealed record AcronisRecordIndexInfo(long TotalSize, IReadOnlyList<AcronisRecordHandle> Handles);

/// <summary>Record extents in the archive (absolute byte positions).</summary>
public sealed record AcronisRecord(
  AcronisRecordType Type,
  long Start,
  long End,
  byte[]? Payload,
  IReadOnlyList<AcronisFileEntry>? Files = null,
  IReadOnlyList<AcronisConfigAttribute>? ConfigAttrs = null,
  AcronisRecordIndexInfo? Index = null
);

public sealed record AcronisConfigAttribute(string Key, string Value);

/// <summary>
/// Walks the Acronis record stream starting at an absolute byte offset and yields parsed records.
/// </summary>
/// <remarks>
/// Each record begins with a 1-byte type tag. The remaining payload is either zlib-wrapped
/// (types 108, 109, 110) or raw-deflate with a trailing 4-byte checksum (types 1, 2, 5, 101, 102, 103).
/// Type 104 (EndTrailer) has no payload and terminates the walk.
/// </remarks>
public static class AcronisRecordReader {

  // First 8 bytes of every RecordIndex payload, used as a sanity check.
  private static readonly byte[] RecordIndexMagic = [0x01, 0x02, 0x00, 0x10, 0x01, 0x00, 0x00, 0x00];

  /// <summary>
  /// Reads records starting at <paramref name="stream"/>'s current position and ending at
  /// <paramref name="endExclusive"/>. Stops at end-of-stream, after an
  /// <see cref="AcronisRecordType.EndTrailer"/> record, or when an unparseable record is
  /// encountered (partial result is returned in that case rather than thrown).
  /// </summary>
  public static List<AcronisRecord> ReadAll(Stream stream, long endExclusive) {
    ArgumentNullException.ThrowIfNull(stream);
    var records = new List<AcronisRecord>();
    var limit = Math.Min(endExclusive, stream.Length);
    while (stream.Position < limit) {
      AcronisRecord rec;
      try {
        rec = ReadOne(stream, limit);
      } catch (InvalidDataException) {
        break;
      } catch (EndOfStreamException) {
        break;
      }
      records.Add(rec);
      if (rec.Type == AcronisRecordType.EndTrailer) break;
    }
    return records;
  }

  public static AcronisRecord ReadOne(Stream stream, long endExclusive) {
    var start = stream.Position;
    var typeByte = stream.ReadByte();
    if (typeByte < 0) throw new EndOfStreamException("Acronis: unexpected EOF reading record type.");
    var type = (AcronisRecordType)(byte)typeByte;

    if (type == AcronisRecordType.EndTrailer)
      return new AcronisRecord(type, start, stream.Position, null);

    byte[] payload;
    var available = endExclusive - stream.Position;
    switch (type) {
      case AcronisRecordType.Blob:
      case AcronisRecordType.BlobSuffix:
      case AcronisRecordType.RecordIndex:
        payload = InflateZlib(stream, available);
        break;
      case AcronisRecordType.Config:
      case AcronisRecordType.FirstFileMetaRecord:
      case AcronisRecordType.Listing:
      case AcronisRecordType.FileMetaA:
      case AcronisRecordType.FileMetaB:
      case AcronisRecordType.FileMetaC:
        payload = InflateRaw(stream, available);
        // 4-byte trailing checksum.
        Span<byte> sumBuf = stackalloc byte[4];
        var n = stream.Read(sumBuf);
        if (n != 4) throw new EndOfStreamException("Acronis: unexpected EOF reading record checksum.");
        break;
      default:
        throw new InvalidDataException($"Acronis: unknown record type 0x{(byte)type:X2}.");
    }

    var end = stream.Position;

    return type switch {
      AcronisRecordType.Listing => new AcronisRecord(type, start, end, payload, Files: ParseListing(payload)),
      AcronisRecordType.Config => new AcronisRecord(type, start, end, payload, ConfigAttrs: ParseConfig(payload)),
      AcronisRecordType.RecordIndex => ValidateRecordIndex(type, start, end, payload),
      _ => new AcronisRecord(type, start, end, payload),
    };
  }

  private static AcronisRecord ValidateRecordIndex(AcronisRecordType type, long start, long end, byte[] payload) {
    if (payload.Length < 8 || !payload.AsSpan(0, 8).SequenceEqual(RecordIndexMagic))
      throw new InvalidDataException("Acronis: RecordIndex payload missing expected magic.");
    var info = ParseRecordIndex(payload);
    return new AcronisRecord(type, start, end, payload, Index: info);
  }

  /// <summary>
  /// Parses a <see cref="AcronisRecordType.RecordIndex"/> payload (type 108). Per upstream RE
  /// (https://github.com/dennisss/acronis-tib, src/win/record.ts):
  /// <code>
  ///   8  bytes : magic "01 02 00 10 01 00 00 00"
  ///   uint48   : totalSize  (8 bytes consumed — 6-byte LE + 2 padding)
  ///   uint32   : numHandles
  ///   numHandles × {
  ///     uint48 : startOffset    (8 bytes consumed — 6-byte LE + 2 padding)
  ///     uint48 : recordOffset   (8 bytes consumed — 6-byte LE + 2 padding)
  ///     16 b   : MD5 of decompressed blob
  ///   }
  ///   ~204 trailing bytes (last 24 == first 24 per upstream comment; otherwise constant per archive)
  /// </code>
  /// Returns <c>null</c> when the payload is too short or malformed (partial archives shouldn't crash).
  /// </summary>
  public static AcronisRecordIndexInfo? ParseRecordIndex(byte[] payload) {
    ArgumentNullException.ThrowIfNull(payload);
    if (payload.Length < 8 + 8 + 4) return null;
    var p = 8; // skip magic
    var totalSize = (long)ReadUInt48LE(payload, p); p += 8;
    var numHandles = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(p)); p += 4;
    if (numHandles < 0) return null;

    var handles = new List<AcronisRecordHandle>(Math.Min(numHandles, 1024));
    for (var i = 0; i < numHandles; i++) {
      if (p + 8 + 8 + 16 > payload.Length) break; // tolerate truncated index — return what we have
      var startOffset = (long)ReadUInt48LE(payload, p); p += 8;
      var recordOffset = (long)ReadUInt48LE(payload, p); p += 8;
      var md5 = new byte[16];
      Buffer.BlockCopy(payload, p, md5, 0, 16); p += 16;
      handles.Add(new AcronisRecordHandle(startOffset, recordOffset, md5));
    }
    return new AcronisRecordIndexInfo(totalSize, handles);
  }

  /// <summary>
  /// Parses a Listing payload (record type 103) into file entries. Per upstream RE
  /// (src/win/record.ts):
  /// <code>
  ///   uint32 numEntries
  ///   foreach entry:
  ///     uint32  pathLen      ; path (UTF-16LE, pathLen chars)
  ///     uint32  ?            ; unknown
  ///     uint32  nameLen      ; name (UTF-16LE, nameLen chars)
  ///     uint32  shortNameLen ; shortName (UTF-16LE, shortNameLen chars)
  ///     uint48  timeRaw      ; FILETIME-ish; 8 bytes consumed (2 padding)
  ///     uint32  ?            ; unknown
  ///     uint48  fileSize     ; 8 bytes consumed
  ///     uint48  fileSize2    ; 8 bytes consumed
  ///     uint48  metaOffset   ; 8 bytes consumed
  ///     38 bytes padding/unknown
  /// </code>
  /// </summary>
  public static List<AcronisFileEntry> ParseListing(byte[] payload) {
    var files = new List<AcronisFileEntry>();
    var p = 0;
    if (payload.Length < 4) return files;
    var numEntries = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(p)); p += 4;

    for (var i = 0; i < numEntries && p < payload.Length; i++) {
      if (p + 4 > payload.Length) break;
      var pathLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(p)); p += 4;
      if (pathLen < 0 || p + pathLen * 2 > payload.Length) break;
      var path = Encoding.Unicode.GetString(payload, p, pathLen * 2); p += pathLen * 2;

      if (p + 4 > payload.Length) break;
      p += 4; // unknown uint32

      if (p + 4 > payload.Length) break;
      var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(p)); p += 4;
      if (nameLen < 0 || p + nameLen * 2 > payload.Length) break;
      var name = Encoding.Unicode.GetString(payload, p, nameLen * 2); p += nameLen * 2;

      if (p + 4 > payload.Length) break;
      var shortLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(p)); p += 4;
      if (shortLen < 0 || p + shortLen * 2 > payload.Length) break;
      var shortName = Encoding.Unicode.GetString(payload, p, shortLen * 2); p += shortLen * 2;

      if (p + 8 > payload.Length) break;
      var timeRaw = ReadUInt48LE(payload, p); p += 8;
      DateTime? time = timeRaw > 0 ? TryToDateTime(timeRaw) : null;

      if (p + 4 > payload.Length) break;
      p += 4; // unknown uint32

      if (p + 8 > payload.Length) break;
      var fileSize = (long)ReadUInt48LE(payload, p); p += 8;
      if (p + 8 > payload.Length) break;
      var fileSize2 = (long)ReadUInt48LE(payload, p); p += 8;
      if (p + 8 > payload.Length) break;
      var metaOffset = (long)ReadUInt48LE(payload, p); p += 8;

      if (p + 38 > payload.Length) {
        // Tolerate truncation by ending here rather than throwing — we still have the entry.
        p = payload.Length;
      } else {
        p += 38;
      }

      files.Add(new AcronisFileEntry(path, name, shortName, time, fileSize, fileSize2, metaOffset));
    }
    return files;
  }

  private static List<AcronisConfigAttribute> ParseConfig(byte[] payload) {
    var attrs = new List<AcronisConfigAttribute>();
    if (payload.Length < 169 + 4) return attrs;
    var p = 165;
    var numAttribs = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(p)); p += 4;
    for (var i = 0; i < numAttribs && p + 4 <= payload.Length; i++) {
      var keyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(p)); p += 4;
      if (keyLen < 0 || p + keyLen * 2 > payload.Length) break;
      var key = Encoding.Unicode.GetString(payload, p, keyLen * 2); p += keyLen * 2;
      if (p + 4 > payload.Length) break;
      var valLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(p)); p += 4;
      if (valLen < 0 || p + valLen * 2 > payload.Length) break;
      var val = Encoding.Unicode.GetString(payload, p, valLen * 2); p += valLen * 2;
      attrs.Add(new AcronisConfigAttribute(key, val));
    }
    return attrs;
  }

  private static ulong ReadUInt48LE(byte[] buf, int offset) {
    ulong v = 0;
    for (var i = 0; i < 6; i++) v |= (ulong)buf[offset + i] << (i * 8);
    return v;
  }

  private static DateTime? TryToDateTime(ulong raw) {
    // Upstream just does `new Date(raw)` (JS ms since epoch). Be defensive: clamp to sane range.
    try {
      var ms = (long)raw;
      if (ms <= 0) return null;
      var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
      return epoch.AddMilliseconds(ms);
    } catch {
      return null;
    }
  }

  /// <summary>
  /// Reads a raw-deflate stream from <paramref name="stream"/>'s current position, leaving the
  /// stream positioned immediately after the deflate end-of-stream marker. The deflate data has
  /// no explicit length prefix, so we copy the candidate region into a <see cref="MemoryStream"/>,
  /// run our in-house <see cref="DeflateDecompressor"/> (which exposes the number of buffered-but-
  /// unconsumed bytes), and use that to compute the exact consumption.
  /// </summary>
  private static byte[] InflateRaw(Stream stream, long limit) {
    if (limit <= 0) throw new InvalidDataException("Acronis: no bytes available for deflate stream.");
    var start = stream.Position;
    var available = (int)Math.Min(limit, int.MaxValue);
    var buf = new byte[available];
    var totalRead = 0;
    while (totalRead < available) {
      var n = stream.Read(buf, totalRead, available - totalRead);
      if (n <= 0) break;
      totalRead += n;
    }
    using var src = new MemoryStream(buf, 0, totalRead, writable: false, publiclyVisible: true);
    var decoder = new DeflateDecompressor(src);
    var output = decoder.DecompressAll();
    var consumed = src.Position - decoder.UnconsumedBytes;
    stream.Position = start + consumed;
    return output;
  }

  /// <summary>
  /// Reads a zlib-wrapped deflate stream from <paramref name="stream"/>'s current position, leaving
  /// the stream positioned immediately after the deflate end-of-stream marker (including the 4-byte
  /// adler-32 trailer).
  /// </summary>
  private static byte[] InflateZlib(Stream stream, long limit) {
    if (limit < 6) throw new InvalidDataException("Acronis: no bytes available for zlib stream.");
    // 2-byte zlib header (CMF + FLG). We consume those, run raw deflate on the rest, then consume
    // the 4-byte adler-32 trailer.
    Span<byte> zhdr = stackalloc byte[2];
    if (stream.Read(zhdr) != 2) throw new EndOfStreamException("Acronis: short zlib header.");
    // Validate: CM (low 4 bits of CMF) must be 8 (deflate). FCHECK ((CMF<<8 | FLG) % 31) must be 0.
    if ((zhdr[0] & 0x0F) != 8) throw new InvalidDataException("Acronis: zlib stream uses non-deflate compression method.");
    var raw = InflateRaw(stream, limit - 2);
    Span<byte> adler = stackalloc byte[4];
    if (stream.Read(adler) != 4) throw new EndOfStreamException("Acronis: short zlib adler trailer.");
    return raw;
  }
}
