#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.Acronis;

/// <summary>
/// Whole-archive writer for Acronis True Image classic <c>.tib</c> Windows-format slices.
/// </summary>
/// <remarks>
/// <para>
/// Builds a complete single-volume file-system <c>.tib</c> slice from scratch — volume header,
/// per-file record chain, single Listing record, EndTrailer, file-system trailer and mirror
/// footer. The byte layout is the exact inverse of <see cref="AcronisReader"/> /
/// <see cref="AcronisRecordReader"/> / <see cref="AcronisSliceTrailer"/> /
/// <see cref="AcronisVolumeHeader"/>, so an archive produced here round-trips through our own
/// reader byte-for-byte on the payload (file content + names + sizes).
/// </para>
/// <para>
/// <b>On-disk layout produced</b> (offsets relative to start of file):
/// </para>
/// <list type="number">
///   <item><description>32-byte volume header (magic <c>CE 24 B9 A2</c>, headerLength 0x20,
///     version 0 = Windows, three 4-byte ids, sequence, Adler-32 slot, blockSize 32).</description></item>
///   <item><description>Per file, in archive order: FirstFileMetaRecord(102) carrying an
///     ItemCommon(0x10) attribute stream with the file name, then FileMetaA/B/C(1/2/5) marker
///     records, then a single Blob(109) holding the file's zlib-compressed content, then a
///     RecordIndex(108) whose single handle points back at the Blob.</description></item>
///   <item><description>One Listing(103) record carrying every entry — each entry's
///     <c>MetaOffset</c> points (relative to the header) at that file's 102 anchor, so the
///     reader's FileMeta chain walk resolves every entry without falling back to the sequential
///     heuristic.</description></item>
///   <item><description>EndTrailer(104) tag, 12-byte file-system trailer (uint64 metadata
///     offset + magic <c>2C 8A E1 94</c>), 48-byte mirror footer (uint64 uncompressed slice
///     size + 8 reserved + 32-byte byte-reversed volume header).</description></item>
/// </list>
/// <para>
/// <b>Record framing.</b> Listing / FirstFileMetaRecord / FileMetaA/B/C bodies are written as
/// 1-byte type tag + raw-deflate body + 4-byte trailing checksum slot (zero-filled; the reader
/// does not validate it). RecordIndex / Blob bodies are written as 1-byte type tag + 2-byte zlib
/// header (<c>0x78 0x9C</c>) + raw-deflate body + 4-byte big-endian Adler-32 trailer. These match
/// the reader's <c>InflateRaw</c> / <c>InflateZlib</c> consumption exactly.
/// </para>
/// <para>
/// <b>Scope.</b> Single-volume Windows file-system slices only — no encryption, no
/// sector-by-sector form, no multi-volume chains, no Mac-format slices (all out of scope for the
/// reader too). The writer never advertises <c>CanCreate</c> on the descriptor until a vendor
/// Acronis restore confirms the bytes; until then this is the honest reader-inverting writer.
/// </para>
/// </remarks>
public static class AcronisWriter {

  /// <summary>Windows-format volume header length in bytes.</summary>
  public const int HeaderLength = 0x20;

  private static ReadOnlySpan<byte> FileSystemTrailerMagic => [0x2C, 0x8A, 0xE1, 0x94];
  private const int FileSystemTrailerLength = 12;
  private const int FooterLength = 48;

  /// <summary>One file to place into a fresh slice.</summary>
  /// <param name="Path">Directory path component (e.g. <c>"sub/"</c> or empty for root).</param>
  /// <param name="Name">Leaf file name.</param>
  /// <param name="Content">Raw file bytes (stored zlib-compressed in a single Blob).</param>
  public sealed record FileSpec(string Path, string Name, byte[] Content);

  /// <summary>
  /// Builds a complete classic <c>.tib</c> Windows file-system slice carrying
  /// <paramref name="files"/> and returns the full archive bytes.
  /// </summary>
  /// <param name="files">Files to place into the slice (archive order is preserved).</param>
  /// <param name="archiveKey">Optional archive identifier (random in real archives).</param>
  /// <param name="sliceKey">Optional slice identifier.</param>
  /// <param name="volumeKey">Optional volume identifier.</param>
  /// <param name="sequence">Volume sequence number (1 for the first/only volume).</param>
  public static byte[] Build(
      IReadOnlyList<FileSpec> files,
      uint archiveKey = 0x11111111,
      uint sliceKey = 0x22222222,
      uint volumeKey = 0x33333333,
      uint sequence = 1) {
    ArgumentNullException.ThrowIfNull(files);

    using var ms = new MemoryStream();

    // 1) Volume header (Adler-32 slot left zero — the reader stores but does not validate it).
    Span<byte> hdr = stackalloc byte[HeaderLength];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, AcronisVolumeHeader.Magic);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], HeaderLength);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[6..], (ushort)AcronisVolumeVersion.Windows);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..], archiveKey);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[12..], sliceKey);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[16..], volumeKey);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[20..], sequence);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[24..], 0); // Adler-32 slot
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[28..], 32); // block size
    ms.Write(hdr);

    var metaStart = (long)ms.Position; // == HeaderLength; canonical metadata origin.

    // 2) Per-file chains: 102 -> 1 -> 2 -> 5 -> 109(Blob) -> 108(RecordIndex). The Listing
    //    follows after all chains so its MetaOffset can anchor each entry to its 102.
    var ffmOffsets = new long[files.Count];
    for (var i = 0; i < files.Count; i++) {
      var f = files[i];
      ffmOffsets[i] = ms.Position - HeaderLength;
      WriteRawDeflateRecord(ms, AcronisRecordType.FirstFileMetaRecord, BuildItemCommonBody(f.Name));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaA, Encoding.ASCII.GetBytes($"cwb-meta1:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaB, Encoding.ASCII.GetBytes($"cwb-meta2:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaC, Encoding.ASCII.GetBytes($"cwb-meta5:{f.Name}"));

      var blobAbsolute = ms.Position;
      WriteZlibRecord(ms, AcronisRecordType.Blob, f.Content);
      var md5 = MD5.HashData(f.Content);

      var indexPayload = BuildRecordIndexPayload(
        totalSize: f.Content.LongLength,
        handles: [(0L, blobAbsolute - HeaderLength, md5)]);
      WriteZlibRecord(ms, AcronisRecordType.RecordIndex, indexPayload);
    }

    // 3) Single Listing record carrying every entry.
    var entries = new List<(string Path, string Name, long FileSize, long MetaOffset)>(files.Count);
    for (var i = 0; i < files.Count; i++)
      entries.Add((files[i].Path, files[i].Name, files[i].Content.LongLength, ffmOffsets[i]));
    WriteRawDeflateRecord(ms, AcronisRecordType.Listing, BuildListingPayload(entries));

    // 4) Closing trio: EndTrailer + 12-byte fs trailer + 48-byte mirror footer.
    ms.WriteByte((byte)AcronisRecordType.EndTrailer);

    Span<byte> trailer = stackalloc byte[FileSystemTrailerLength];
    BinaryPrimitives.WriteInt64LittleEndian(trailer, metaStart);
    FileSystemTrailerMagic.CopyTo(trailer[8..]);
    ms.Write(trailer);

    Span<byte> footer = stackalloc byte[FooterLength];
    BinaryPrimitives.WriteInt64LittleEndian(footer, ms.Length + FooterLength); // uncompressed slice size
    for (var i = 0; i < HeaderLength; i++) footer[16 + (31 - i)] = hdr[i];     // byte-reversed header mirror
    ms.Write(footer);

    return ms.ToArray();
  }

  // ----- record framing (inverse of AcronisRecordReader) -----

  // Canonical empty raw-deflate stream: BFINAL=1, BTYPE=01 (fixed Huffman), end-of-block symbol.
  // DeflateStream emits ZERO bytes for empty input, which the reader's inflate path cannot consume,
  // so empty payloads are written as this 2-byte terminator block instead.
  private static ReadOnlySpan<byte> EmptyDeflateBlock => [0x03, 0x00];

  private static void WriteDeflateBody(Stream s, byte[] payload) {
    if (payload.Length == 0) {
      s.Write(EmptyDeflateBlock);
      return;
    }
    using var def = new DeflateStream(s, CompressionLevel.Fastest, leaveOpen: true);
    def.Write(payload, 0, payload.Length);
  }

  private static void WriteRawDeflateRecord(Stream s, AcronisRecordType type, byte[] payload) {
    s.WriteByte((byte)type);
    WriteDeflateBody(s, payload);
    Span<byte> sum = stackalloc byte[4]; // trailing checksum slot — reader skips, does not validate.
    s.Write(sum);
  }

  private static void WriteZlibRecord(Stream s, AcronisRecordType type, byte[] payload) {
    s.WriteByte((byte)type);
    s.WriteByte(0x78);
    s.WriteByte(0x9C);
    WriteDeflateBody(s, payload);
    Span<byte> adler = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(adler, ComputeAdler32(payload));
    s.Write(adler);
  }

  // ----- payload builders (inverse of AcronisRecordReader parsers) -----

  private static byte[] BuildItemCommonBody(string name) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);

    // Attribute stream: uint32 attributeCount + N x {uint32 idAndFlags, uint16 size, byte[size]}.
    w.Write(1u);
    w.Write((uint)AcronisAttributeId.ItemCommon);
    var nameBytes = Encoding.Unicode.GetBytes(name);
    w.Write(checked((ushort)(44 + nameBytes.Length)));

    // 44-byte fixed ItemCommon header.
    w.Write((ushort)name.Length); // nameLength (UTF-16 code units)
    w.Write((ushort)0);           // altNameLength
    w.Write(0u);                  // dosAttributes
    w.Write(0UL);                 // creationTime
    w.Write(0UL);                 // lastWriteTime
    w.Write(0UL);                 // lastAccessTime
    w.Write(0UL);                 // changeTime
    w.Write(0u);                  // trailer dword

    if (nameBytes.Length > 0) w.Write(nameBytes);
    w.Flush();
    return ms.ToArray();
  }

  private static byte[] BuildListingPayload(IReadOnlyList<(string Path, string Name, long FileSize, long MetaOffset)> entries) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
    w.Write((uint)entries.Count);
    foreach (var (path, name, fileSize, metaOffset) in entries) {
      WriteCountedUtf16(w, path);
      w.Write(0u);                    // unknown uint32
      WriteCountedUtf16(w, name);
      WriteCountedUtf16(w, "");        // shortName
      WriteUInt48(w, 0); w.Write((ushort)0); // time + pad
      w.Write(0u);                    // unknown uint32
      WriteUInt48(w, (ulong)fileSize); w.Write((ushort)0);
      WriteUInt48(w, (ulong)fileSize); w.Write((ushort)0);
      WriteUInt48(w, (ulong)metaOffset); w.Write((ushort)0);
      w.Write(new byte[38]);          // tail of unknown fields
    }
    w.Flush();
    return ms.ToArray();
  }

  private static byte[] BuildRecordIndexPayload(long totalSize, IReadOnlyList<(long startOffset, long recordOffset, byte[] md5)> handles) {
    using var ms = new MemoryStream();
    ms.Write([0x01, 0x02, 0x00, 0x10, 0x01, 0x00, 0x00, 0x00]); // 8-byte magic
    WriteUInt48Bytes(ms, (ulong)totalSize); ms.WriteByte(0); ms.WriteByte(0);
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)handles.Count);
    ms.Write(u32);
    foreach (var h in handles) {
      WriteUInt48Bytes(ms, (ulong)h.startOffset); ms.WriteByte(0); ms.WriteByte(0);
      WriteUInt48Bytes(ms, (ulong)h.recordOffset); ms.WriteByte(0); ms.WriteByte(0);
      ms.Write(h.md5);
    }
    return ms.ToArray();
  }

  // ----- primitives -----

  private static void WriteCountedUtf16(BinaryWriter w, string s) {
    w.Write((uint)s.Length);
    if (s.Length > 0) w.Write(Encoding.Unicode.GetBytes(s));
  }

  private static void WriteUInt48(BinaryWriter w, ulong v) {
    for (var i = 0; i < 6; i++) w.Write((byte)((v >> (i * 8)) & 0xFF));
  }

  private static void WriteUInt48Bytes(MemoryStream s, ulong v) {
    for (var i = 0; i < 6; i++) s.WriteByte((byte)((v >> (i * 8)) & 0xFF));
  }

  private static uint ComputeAdler32(byte[] data) {
    const uint Mod = 65521;
    uint a = 1, b = 0;
    foreach (var x in data) { a = (a + x) % Mod; b = (b + a) % Mod; }
    return (b << 16) | a;
  }
}
