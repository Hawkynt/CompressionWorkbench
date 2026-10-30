#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Lzx;

namespace FileFormat.Chm;

/// <summary>
/// Writes a Microsoft Compiled HTML Help (.chm) file. Supports two modes:
/// <list type="bullet">
///   <item><b>Stored</b> (default): all files go into section 0 (uncompressed).</item>
///   <item><b>LZX</b>: files are LZX-compressed into section 1 with a reset table.
///   Internal meta-entries (ControlData, ResetTable, Content) live in section 0.</item>
/// </list>
/// Both modes roundtrip through <see cref="ChmReader"/>.
/// </summary>
public sealed class ChmWriter {
  private const uint ItsfMagic = 0x46535449;
  private const uint ItspMagic = 0x50535449;
  private const uint PmglMagic = 0x4C474D50;
  private const int ItsfHeaderSize = 96;
  private const int ItspHeaderSize = 84;
  private const int PmglHeaderSize = 20;
  private const int ChunkSize = 4096;
  private const int LzxFrameSize = 0x8000; // the LZX frame and, here, the reset interval
  private const int LzxWindowBits = 15;
  private const string ResetTablePath = "::DataSpace/Storage/MSCompressed/Transform/{7FC28940-9D31-11D0-9B27-00A0C91E9C7C}/InstanceData/ResetTable";
  private const string ControlDataPath = "::DataSpace/Storage/MSCompressed/ControlData";
  private const string ContentPath = "::DataSpace/Storage/MSCompressed/Content";
  private const string SpanInfoPath = "::DataSpace/Storage/MSCompressed/SpanInfo";
  private const string NameListPath = "::DataSpace/NameList";

  private readonly List<(string name, byte[] data)> _files = [];

  /// <summary>
  /// Performs the add file operation.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    _files.Add((name, data));
  }

  /// <summary>
  /// Writes the to to the supplied output.
  /// </summary>
  public void WriteTo(Stream output, bool useLzx = false) {
    ArgumentNullException.ThrowIfNull(output);
    if (useLzx) WriteLzx(output); else WriteStored(output);
  }

  // ── Stored mode (section 0) ──

  private void WriteStored(Stream output) {
    // 7-Zip and HTML Help both look the section names up in ::DataSpace/NameList, even for a file
    // that only has the uncompressed section.
    var section0 = new List<(string Name, byte[] Data)> { (NameListPath, BuildNameList(compressedSection: false)) };
    section0.AddRange(_files);
    var entryRecords = new List<(string Name, byte[] Record)>();
    long offset = 0;
    foreach (var (name, data) in section0) {
      entryRecords.Add((name, BuildDirEntry(name, 0, offset, data.Length)));
      offset += data.Length;
    }
    EmitChm(output, Sorted(entryRecords), offset, section0.Select(f => f.Data));
  }

  // ── LZX mode (section 1 + internal meta-entries in section 0) ──

  private void WriteLzx(Stream output) {
    // Concatenate all file data.
    var uncompressedBlob = ConcatFiles(out var fileOffsets, out var fileSizes);

    // LZX-compress in reset intervals.
    var (compressedBlob, resetOffsets) = CompressLzx(uncompressedBlob);

    // Build internal meta-entries for section 0.
    var controlData = BuildControlData();
    var resetTable = BuildResetTable(uncompressedBlob.Length, compressedBlob.Length, resetOffsets);

    // Section 0 layout: controlData + resetTable + compressedBlob
    var spanInfo = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(spanInfo, (ulong)uncompressedBlob.Length);
    var section0Entries = new List<(string name, byte[] data)> {
      (NameListPath, BuildNameList(compressedSection: true)),
      (ControlDataPath, controlData),
      (SpanInfoPath, spanInfo),
      (ResetTablePath, resetTable),
      (ContentPath, compressedBlob),
    };

    var entryRecords = new List<(string Name, byte[] Record)>();
    long s0Offset = 0;
    foreach (var (name, data) in section0Entries) {
      entryRecords.Add((name, BuildDirEntry(name, 0, s0Offset, data.Length)));
      s0Offset += data.Length;
    }
    // User files go to section 1.
    for (var i = 0; i < _files.Count; i++)
      entryRecords.Add((_files[i].name, BuildDirEntry(_files[i].name, 1, fileOffsets[i], fileSizes[i])));

    EmitChm(output, Sorted(entryRecords), s0Offset, section0Entries.Select(e => e.data));
  }

  private byte[] ConcatFiles(out long[] offsets, out long[] sizes) {
    offsets = new long[_files.Count];
    sizes = new long[_files.Count];
    using var ms = new MemoryStream();
    for (var i = 0; i < _files.Count; i++) {
      offsets[i] = ms.Position;
      sizes[i] = _files[i].data.Length;
      ms.Write(_files[i].data);
    }
    return ms.ToArray();
  }

  private static (byte[] compressed, long[] resetOffsets) CompressLzx(byte[] uncompressed) {
    // Every 32 KiB frame is its own LZX stream (reset interval = one frame), in the cabinet
    // arrangement CHM shares: a leading "no E8 translation" bit, 24-bit block sizes.
    var frames = (uncompressed.Length + LzxFrameSize - 1) / LzxFrameSize;
    var resetOffsets = new long[frames];
    using var ms = new MemoryStream();
    for (var i = 0; i < frames; i++) {
      resetOffsets[i] = ms.Position;
      var start = i * LzxFrameSize;
      // A frame always decodes to the full 32 KiB, as HTML Help Workshop writes it (readers such as
      // 7-Zip ask every frame for a whole block); the last one is padded with zeros beyond the
      // section's length, which the reset table records.
      var frame = new byte[LzxFrameSize];
      uncompressed.AsSpan(start, Math.Min(LzxFrameSize, uncompressed.Length - start)).CopyTo(frame);
      var compressor = new LzxCompressor(LzxWindowBits, LzxFrameSize, LzxCompressionLevel.Normal, LzxStreamFormat.Cab);
      ms.Write(compressor.Compress(frame));
    }
    // The table lists floor(length / 32 KiB) + 1 frame starts (7-Zip insists on that count); when the
    // length is a whole number of frames, the extra entry is the end of the compressed data.
    if (uncompressed.Length % LzxFrameSize == 0)
      resetOffsets = [.. resetOffsets, ms.Position];
    return (ms.ToArray(), resetOffsets);
  }

  /// <summary>Directory entries in the case-insensitive order CHM readers binary-search them in.</summary>
  private static List<byte[]> Sorted(List<(string Name, byte[] Record)> records)
    => [.. records.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => r.Record)];

  /// <summary>::DataSpace/NameList: section names as length-prefixed, NUL-terminated UTF-16LE.</summary>
  private static byte[] BuildNameList(bool compressedSection) {
    string[] names = compressedSection ? ["Uncompressed", "MSCompressed"] : ["Uncompressed"];
    using var ms = new MemoryStream();
    var w = new byte[2];
    void Word(int v) { BinaryPrimitives.WriteUInt16LittleEndian(w, (ushort)v); ms.Write(w); }
    Word(2 + names.Sum(n => n.Length + 2));
    Word(names.Length);
    foreach (var n in names) {
      Word(n.Length);
      ms.Write(Encoding.Unicode.GetBytes(n));
      Word(0);
    }
    return ms.ToArray();
  }

  private static byte[] BuildControlData() {
    // LZXC control data, version 2 (interval and window in 32 KiB units):
    // dword count 6, "LZXC", version 2, reset interval 1, window 1 (2^15), cache size 1, 0.
    var data = new byte[28];
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 6);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x43585A4C); // "LZXC"
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)((1 << LzxWindowBits) / LzxFrameSize));
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 1);
    return data;
  }

  private static byte[] BuildResetTable(int uncompressedSize, int compressedSize, long[] resetOffsets) {
    // version 2, entry count, entry size 8, header size 40, uint64 uncompressed size,
    // uint64 compressed size, uint64 frame size, then the compressed offset of every frame.
    const int headerSize = 40;
    var data = new byte[headerSize + resetOffsets.Length * 8];
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)resetOffsets.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 8);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), headerSize);
    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), (ulong)uncompressedSize);
    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(24), (ulong)compressedSize);
    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(32), LzxFrameSize);
    for (var i = 0; i < resetOffsets.Length; i++)
      BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(headerSize + i * 8), (ulong)resetOffsets[i]);
    return data;
  }

  // ── Shared CHM emission ──

  // ITSF header GUIDs and the ITSP system GUID, as HTML Help Workshop writes them.
  private static ReadOnlySpan<byte> ItsfGuid1 => [0x10, 0xFD, 0x01, 0x7C, 0xAA, 0x7B, 0xD0, 0x11, 0x9E, 0x0C, 0x00, 0xA0, 0xC9, 0x22, 0xE6, 0xEC];
  private static ReadOnlySpan<byte> ItsfGuid2 => [0x11, 0xFD, 0x01, 0x7C, 0xAA, 0x7B, 0xD0, 0x11, 0x9E, 0x0C, 0x00, 0xA0, 0xC9, 0x22, 0xE6, 0xEC];
  private static ReadOnlySpan<byte> ItspGuid => [0x6A, 0x92, 0x02, 0x5D, 0x2E, 0x21, 0xD0, 0x11, 0x9D, 0xF9, 0x00, 0xA0, 0xC9, 0x22, 0xE6, 0xEC];
  private const int HeaderSection0Size = 24;
  private const uint LanguageId = 0x0409;

  /// <summary>
  /// Lays the file out as HTML Help Workshop's version 3 does: ITSF header, header section 0
  /// (carrying the file size), the directory (ITSP header and PMGL chunks), then the content of
  /// section 0, whose offset the header's version-3 field records.
  /// </summary>
  private static void EmitChm(Stream output, List<byte[]> entryRecords, long totalSection0, IEnumerable<byte[]> section0Data) {
    var chunks = BuildPmglChunks(entryRecords);
    var hs0Offset = (long)ItsfHeaderSize;
    var dirSectionOffset = hs0Offset + HeaderSection0Size;
    var dirSectionLength = ItspHeaderSize + (long)chunks.Count * ChunkSize;
    var contentOffset = dirSectionOffset + dirSectionLength;
    var fileSize = contentOffset + totalSection0;

    var hdr = new byte[ItsfHeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0), ItsfMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), 3);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8), ItsfHeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(20), LanguageId);
    ItsfGuid1.CopyTo(hdr.AsSpan(24));
    ItsfGuid2.CopyTo(hdr.AsSpan(40));
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(56), (ulong)hs0Offset);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(64), HeaderSection0Size);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(72), (ulong)dirSectionOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(80), (ulong)dirSectionLength);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(88), (ulong)contentOffset);
    output.Write(hdr);

    var hs0 = new byte[HeaderSection0Size];
    BinaryPrimitives.WriteUInt32LittleEndian(hs0.AsSpan(0), 0x01FE);
    BinaryPrimitives.WriteUInt64LittleEndian(hs0.AsSpan(8), (ulong)fileSize);
    output.Write(hs0);

    var itsp = new byte[ItspHeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(0), ItspMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(8), ItspHeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(12), 0x0A);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(16), ChunkSize);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(20), 2);                    // quickref density
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(24), 1);                    // index depth: PMGL only
    BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(28), -1);                    // no PMGI root
    BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(32), 0);                     // first PMGL
    BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(36), chunks.Count - 1);      // last PMGL
    BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(40), -1);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(44), (uint)chunks.Count);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(48), LanguageId);
    ItspGuid.CopyTo(itsp.AsSpan(52));
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(68), ItspHeaderSize);
    BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(72), -1);
    BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(76), -1);
    BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(80), -1);
    output.Write(itsp);
    foreach (var chunk in chunks) output.Write(chunk);

    foreach (var data in section0Data)
      output.Write(data);
  }

  private static byte[] BuildDirEntry(string name, int section, long offset, long size) {
    using var rec = new MemoryStream();
    var nameBytes = Encoding.UTF8.GetBytes(name);
    WriteEncInt(rec, nameBytes.Length);
    rec.Write(nameBytes);
    WriteEncInt(rec, section);
    WriteEncInt(rec, offset);
    WriteEncInt(rec, size);
    return rec.ToArray();
  }

  private static List<byte[]> BuildPmglChunks(List<byte[]> entryRecords) {
    var chunks = new List<byte[]>();
    var currentChunk = new byte[ChunkSize];
    var pos = PmglHeaderSize;
    foreach (var rec in entryRecords) {
      if (pos + rec.Length > ChunkSize) {
        FinalizePmglChunk(currentChunk, pos);
        chunks.Add(currentChunk);
        currentChunk = new byte[ChunkSize];
        pos = PmglHeaderSize;
      }
      rec.CopyTo(currentChunk, pos);
      pos += rec.Length;
    }
    FinalizePmglChunk(currentChunk, pos);
    chunks.Add(currentChunk);
    for (var i = 0; i < chunks.Count; i++) {
      BinaryPrimitives.WriteInt32LittleEndian(chunks[i].AsSpan(12), i > 0 ? i - 1 : -1);
      BinaryPrimitives.WriteInt32LittleEndian(chunks[i].AsSpan(16), i < chunks.Count - 1 ? i + 1 : -1);
    }
    return chunks;
  }

  private static void FinalizePmglChunk(byte[] chunk, int usedBytes) {
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(0), PmglMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)(ChunkSize - usedBytes));
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(8), 0);
  }

  private static void WriteEncInt(Stream s, long value) {
    if (value < 0) value = 0;
    Span<byte> buf = stackalloc byte[10];
    var pos = 9;
    buf[pos] = (byte)(value & 0x7F);
    value >>= 7;
    while (value > 0) { pos--; buf[pos] = (byte)(0x80 | (value & 0x7F)); value >>= 7; }
    s.Write(buf[pos..]);
  }
}
