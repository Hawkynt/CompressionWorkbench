#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using FileFormat.Zlib;

namespace FileFormat.Ewf;

/// <summary>
/// Writer for EnCase Expert Witness Format (EWF / .E01) forensic images.
/// Produces a single-segment EVF image that the reference <c>libewf</c> tools
/// (<c>ewfverify</c>, <c>ewfinfo</c>, <c>ewfexport</c>) accept and reconstruct
/// byte-for-byte.
/// </summary>
/// <remarks>
/// <para>
/// The on-disk layout mirrors what <c>ewfacquire -f encase6</c> emits, verified
/// against a reference image produced by libewf 20140814:
/// </para>
/// <code>
///   EVF file header (13 bytes): "EVF\x09\x0D\x0A\xFF\x00" + 0x01 + segment(u16) + 0x0000
///   header2  section   (zlib-compressed UTF-16LE acquisition metadata, written twice)
///   header   section   (zlib-compressed ASCII acquisition metadata)
///   volume   section   (1052-byte media descriptor + trailing Adler-32)
///   sectors  section   (chunk data: each chunk + trailing Adler-32 when stored)
///   table    section   (chunk offset array: base_offset + 32-bit entries, MSB=compressed)
///   table2   section   (mirror of table)
///   data     section   (duplicate of volume — libewf cross-check copy)
///   hash     section   (MD5 of the acquired media)
///   done     section   (size 0, next == self)
/// </code>
/// <para>
/// Every section descriptor and every payload that carries one ends with an
/// Adler-32 (zlib variant, stored little-endian). Chunks default to STORED
/// (uncompressed); pass a compression level to zlib-deflate each chunk, with the
/// table entry's MSB flagging compressed chunks.
/// </para>
/// </remarks>
public sealed class EwfWriter {

  /// <summary>Sectors per chunk (libewf default).</summary>
  public const int SectorsPerChunk = 64;

  /// <summary>Bytes per sector (libewf default).</summary>
  public const int BytesPerSector = 512;

  /// <summary>Chunk size in bytes (64 * 512 = 32768).</summary>
  public const int ChunkSize = SectorsPerChunk * BytesPerSector;

  private const int FileHeaderSize = 13;
  private const int SectionDescriptorSize = 76;
  private const int VolumePayloadSize = 1052; // 1048 fields + 4-byte trailing Adler-32

  private static readonly byte[] EvfSignature = [0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00];

  /// <summary>When true, each chunk is zlib-compressed (stored uncompressed if it does not shrink).</summary>
  public bool CompressChunks { get; init; }

  /// <summary>Case number recorded in the acquisition header.</summary>
  public string CaseNumber { get; init; } = "";

  /// <summary>Evidence number recorded in the acquisition header.</summary>
  public string EvidenceNumber { get; init; } = "";

  /// <summary>Free-form description recorded in the acquisition header.</summary>
  public string Description { get; init; } = "";

  /// <summary>Examiner name recorded in the acquisition header.</summary>
  public string ExaminerName { get; init; } = "";

  /// <summary>Notes recorded in the acquisition header.</summary>
  public string Notes { get; init; } = "";

  /// <summary>
  /// Builds a single-segment E01 image from the supplied raw media bytes.
  /// </summary>
  /// <param name="media">The raw disk/media image to wrap. Length should be a
  /// multiple of <see cref="BytesPerSector"/>; a trailing partial sector is
  /// padded with zeros to a full sector (matching libewf behaviour).</param>
  /// <returns>The complete .E01 image bytes.</returns>
  public byte[] Build(ReadOnlySpan<byte> media) {
    // libewf works in whole sectors: pad the tail up to a full sector.
    var totalSectors = (media.Length + BytesPerSector - 1) / BytesPerSector;
    var paddedLen = totalSectors * BytesPerSector;
    var data = new byte[paddedLen];
    media.CopyTo(data);

    var md5 = MD5.HashData(data);

    var chunkCount = (paddedLen + ChunkSize - 1) / ChunkSize;
    if (chunkCount == 0) chunkCount = 0; // empty media => no chunks

    using var ms = new MemoryStream();

    // ── EVF file header ────────────────────────────────────────────────
    ms.Write(EvfSignature);
    ms.WriteByte(0x01);                       // fields_start
    WriteU16(ms, 1);                          // segment number
    WriteU16(ms, 0);                          // fields_end

    // ── header2 (UTF-16LE) ×2, then header (ASCII) ─────────────────────
    var header2 = ZlibStream.Compress(BuildHeader2Text());
    var header = ZlibStream.Compress(BuildHeaderText());
    WriteSection(ms, "header2", header2, isLast: false);
    WriteSection(ms, "header2", header2, isLast: false);
    WriteSection(ms, "header", header, isLast: false);

    // ── volume ─────────────────────────────────────────────────────────
    var volume = BuildVolumePayload(chunkCount, totalSectors);
    WriteSection(ms, "volume", volume, isLast: false);

    // ── sectors + table (offsets are relative to the sectors section
    //    descriptor, per libewf base_offset convention) ─────────────────
    var sectorsDescriptorOffset = ms.Position;
    var (sectorsPayload, tableEntries) = BuildSectorsAndTable(data, chunkCount);
    WriteSection(ms, "sectors", sectorsPayload, isLast: false);

    var table = BuildTablePayload(tableEntries, (ulong)sectorsDescriptorOffset);
    WriteSection(ms, "table", table, isLast: false);
    WriteSection(ms, "table2", table, isLast: false);

    // ── data (duplicate of volume), hash, done ─────────────────────────
    WriteSection(ms, "data", volume, isLast: false);
    WriteSection(ms, "hash", BuildHashPayload(md5), isLast: false);
    WriteDoneSection(ms);

    return ms.ToArray();
  }

  // ── Section framing ──────────────────────────────────────────────────

  /// <summary>
  /// Writes a section descriptor + payload. The descriptor's next-offset is the
  /// absolute offset of the following section descriptor; it equals the current
  /// position plus the section size.
  /// </summary>
  private static void WriteSection(MemoryStream ms, string type, ReadOnlySpan<byte> payload, bool isLast) {
    var descriptorOffset = ms.Position;
    var sectionSize = (ulong)(SectionDescriptorSize + payload.Length);
    var next = isLast ? (ulong)descriptorOffset : (ulong)descriptorOffset + sectionSize;
    WriteDescriptor(ms, type, next, sectionSize);
    ms.Write(payload);
  }

  /// <summary>The terminal "done" section: size 0, next == its own offset.</summary>
  private static void WriteDoneSection(MemoryStream ms) {
    var descriptorOffset = (ulong)ms.Position;
    WriteDescriptor(ms, "done", descriptorOffset, 0);
  }

  private static void WriteDescriptor(MemoryStream ms, string type, ulong next, ulong size) {
    Span<byte> desc = stackalloc byte[SectionDescriptorSize];
    desc.Clear();
    var typeBytes = Encoding.ASCII.GetBytes(type);
    typeBytes.AsSpan(0, Math.Min(typeBytes.Length, 16)).CopyTo(desc);
    BinaryPrimitives.WriteUInt64LittleEndian(desc[16..], next);
    BinaryPrimitives.WriteUInt64LittleEndian(desc[24..], size);
    // bytes 32..71 are zero padding
    var checksum = Adler32.Compute(desc[..72]);
    BinaryPrimitives.WriteUInt32LittleEndian(desc[72..], checksum);
    ms.Write(desc);
  }

  // ── Volume / data section ────────────────────────────────────────────

  private static byte[] BuildVolumePayload(int chunkCount, int totalSectors) {
    var p = new byte[VolumePayloadSize];
    p[0] = 0x01;                                                  // media_type = fixed disk
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), (uint)chunkCount);
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), SectorsPerChunk);
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), BytesPerSector);
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16), (uint)totalSectors);
    // Reserved fields libewf populates with fixed values.
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(36), 3);    // reserved (libewf constant)
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(56), 64);   // error granularity (sectors)
    // Trailing Adler-32 over the first 1048 bytes (stored little-endian).
    var adler = Adler32.Compute(p.AsSpan(0, VolumePayloadSize - 4));
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(VolumePayloadSize - 4), adler);
    return p;
  }

  // ── Sectors + table ──────────────────────────────────────────────────

  private (byte[] SectorsPayload, List<uint> TableEntries) BuildSectorsAndTable(byte[] data, int chunkCount) {
    using var sectors = new MemoryStream();
    var entries = new List<uint>(chunkCount);

    // base_offset for table entries is the sectors-section descriptor offset.
    // The first chunk therefore sits at relative offset 76 (just past the
    // descriptor). Entry values are offsets relative to the descriptor.
    var relativeOffset = (long)SectionDescriptorSize;

    for (var i = 0; i < chunkCount; i++) {
      var start = i * ChunkSize;
      var len = Math.Min(ChunkSize, data.Length - start);
      var chunk = data.AsSpan(start, len);

      var stored = true;
      byte[] toWrite;
      if (this.CompressChunks) {
        var compressed = ZlibStream.Compress(chunk);
        // Only adopt compression when it actually shrinks the chunk; libewf's
        // table MSB flags compressed chunks. Compressed chunks carry their own
        // Adler-32 inside the zlib trailer, so no extra checksum is appended.
        if (compressed.Length < len) {
          toWrite = compressed;
          stored = false;
        } else {
          toWrite = AppendAdler(chunk);
        }
      } else {
        toWrite = AppendAdler(chunk);
      }

      // Table entry: offset relative to base, MSB set when compressed.
      var entry = (uint)relativeOffset & 0x7FFFFFFF;
      if (!stored) entry |= 0x80000000;
      entries.Add(entry);

      sectors.Write(toWrite);
      relativeOffset += toWrite.Length;
    }

    return (sectors.ToArray(), entries);
  }

  /// <summary>Appends a zlib Adler-32 (little-endian) to a stored chunk.</summary>
  private static byte[] AppendAdler(ReadOnlySpan<byte> chunk) {
    var result = new byte[chunk.Length + 4];
    chunk.CopyTo(result);
    var adler = Adler32.Compute(chunk);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(chunk.Length), adler);
    return result;
  }

  private static byte[] BuildTablePayload(List<uint> entries, ulong baseOffset) {
    // Layout: entry_count(u32) pad(4) base_offset(u64) pad(4) header_adler(u32)
    //         then entries[u32]..  then trailing Adler-32 over the entry block.
    var headerLen = 24;
    var entriesLen = entries.Count * 4;
    var p = new byte[headerLen + entriesLen + 4];

    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), (uint)entries.Count);
    // bytes 4..7 padding
    BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(8), baseOffset);
    // bytes 16..19 padding
    var headerAdler = Adler32.Compute(p.AsSpan(0, 20));
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(20), headerAdler);

    for (var i = 0; i < entries.Count; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(headerLen + i * 4), entries[i]);

    // Trailing Adler-32 over the entry block (offset 24 .. end-4).
    var trailerAdler = Adler32.Compute(p.AsSpan(headerLen, entriesLen));
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(headerLen + entriesLen), trailerAdler);
    return p;
  }

  // ── Hash section ─────────────────────────────────────────────────────

  private static byte[] BuildHashPayload(byte[] md5) {
    // 16-byte MD5 + 16 reserved bytes + 4-byte trailing Adler-32.
    var p = new byte[36];
    md5.AsSpan(0, 16).CopyTo(p);
    var adler = Adler32.Compute(p.AsSpan(0, 32));
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(32), adler);
    return p;
  }

  // ── Acquisition headers ──────────────────────────────────────────────

  /// <summary>
  /// Builds the ASCII "header" payload: a tab-separated record describing the
  /// acquisition. Format matches libewf's EnCase1 header (category "main").
  /// </summary>
  private byte[] BuildHeaderText() {
    var now = DateTime.Now;
    var ts = string.Create(CultureInfo.InvariantCulture,
      $"{now.Year} {now.Month} {now.Day} {now.Hour} {now.Minute} {now.Second}");
    var sb = new StringBuilder();
    sb.Append("1\r\nmain\r\n");
    sb.Append("c\tn\ta\te\tt\tav\tov\tm\tu\tp\r\n");
    sb.Append(this.CaseNumber).Append('\t')
      .Append(this.EvidenceNumber).Append('\t')
      .Append(this.Description).Append('\t')
      .Append(this.ExaminerName).Append('\t')
      .Append(this.Notes).Append('\t')
      .Append("CompressionWorkbench").Append('\t')   // av: acquisition tool version
      .Append("Windows").Append('\t')                // ov: operating system
      .Append(ts).Append('\t')                       // m: acquisition date
      .Append(ts).Append('\t')                       // u: system date
      .Append('0').Append("\r\n\r\n");               // p: password hash
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  /// <summary>
  /// Builds the UTF-16LE "header2" payload (BOM-prefixed), libewf's richer
  /// EnCase4+ header carrying the same acquisition fields.
  /// </summary>
  private byte[] BuildHeader2Text() {
    var now = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    var sb = new StringBuilder();
    sb.Append("3\nmain\n");
    sb.Append("a\tc\tn\te\tt\tav\tov\tm\tu\tp\tdc\n");
    sb.Append(this.Description).Append('\t')
      .Append(this.CaseNumber).Append('\t')
      .Append(this.EvidenceNumber).Append('\t')
      .Append(this.ExaminerName).Append('\t')
      .Append(this.Notes).Append('\t')
      .Append("CompressionWorkbench").Append('\t')
      .Append("Windows").Append('\t')
      .Append(now).Append('\t')
      .Append(now).Append('\t')
      .Append('0').Append('\t')
      .Append('\n');
    var text = sb.ToString();
    // UTF-16LE with BOM.
    var bom = new byte[] { 0xFF, 0xFE };
    var body = Encoding.Unicode.GetBytes(text);
    var result = new byte[bom.Length + body.Length];
    bom.CopyTo(result, 0);
    body.CopyTo(result, bom.Length);
    return result;
  }

  // ── Little-endian helpers ────────────────────────────────────────────

  private static void WriteU16(Stream s, ushort v) {
    Span<byte> b = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(b, v);
    s.Write(b);
  }
}
