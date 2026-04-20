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
  private const int LzxResetInterval = 0x8000; // 32 KB
  private const int LzxWindowBits = 15;
  private const string ResetTablePath = "::DataSpace/Storage/MSCompressed/Transform/{7FC28940-9D31-11D0-9B27-00A0C91E9C7C}/InstanceData/ResetTable";
  private const string ControlDataPath = "::DataSpace/Storage/MSCompressed/ControlData";
  private const string ContentPath = "::DataSpace/Storage/MSCompressed/Content";

  private readonly List<(string name, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    _files.Add((name, data));
  }

  public void WriteTo(Stream output, bool useLzx = false) {
    ArgumentNullException.ThrowIfNull(output);
    if (useLzx) WriteLzx(output); else WriteStored(output);
  }

  // ── Stored mode (section 0) ──

  private void WriteStored(Stream output) {
    var entryRecords = new List<byte[]>();
    long offset = 0;
    foreach (var (name, data) in _files) {
      entryRecords.Add(BuildDirEntry(name, 0, offset, data.Length));
      offset += data.Length;
    }
    var totalContent = offset;
    EmitChm(output, entryRecords, totalContent, _files.Select(f => f.data));
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
    var section0Entries = new List<(string name, byte[] data)> {
      (ControlDataPath, controlData),
      (ResetTablePath, resetTable),
      (ContentPath, compressedBlob),
    };

    var entryRecords = new List<byte[]>();
    long s0Offset = 0;
    foreach (var (name, data) in section0Entries) {
      entryRecords.Add(BuildDirEntry(name, 0, s0Offset, data.Length));
      s0Offset += data.Length;
    }
    // User files go to section 1.
    for (var i = 0; i < _files.Count; i++)
      entryRecords.Add(BuildDirEntry(_files[i].name, 1, fileOffsets[i], fileSizes[i]));

    EmitChm(output, entryRecords, s0Offset, section0Entries.Select(e => e.data));
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
    var intervals = (uncompressed.Length + LzxResetInterval - 1) / LzxResetInterval;
    var resetOffsets = new long[intervals];
    using var ms = new MemoryStream();
    for (var i = 0; i < intervals; i++) {
      resetOffsets[i] = ms.Position;
      var start = i * LzxResetInterval;
      var len = Math.Min(LzxResetInterval, uncompressed.Length - start);
      var compressor = new LzxCompressor(LzxWindowBits, LzxResetInterval);
      var block = compressor.Compress(uncompressed.AsSpan(start, len));
      ms.Write(block);
    }
    return (ms.ToArray(), resetOffsets);
  }

  private static byte[] BuildControlData() {
    // uint32[7]: word count(6), then 5 zeros, then window bits
    var data = new byte[28];
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 6);
    // words 1-5 = 0
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), LzxWindowBits);
    return data;
  }

  private static byte[] BuildResetTable(int uncompressedSize, int compressedSize, long[] resetOffsets) {
    // Reader reads: tableHeaderSize at [12], uncompressedSize at [16],
    // blockSize (uint32) at [28], reset offsets starting at byte [tableHeaderSize].
    const int headerSize = 32;
    var data = new byte[headerSize + resetOffsets.Length * 8];
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 2);                // version
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)resetOffsets.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 8);                // entry size
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), headerSize);      // offset table start
    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), (ulong)uncompressedSize);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), (uint)compressedSize);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), LzxResetInterval); // blockSize
    for (var i = 0; i < resetOffsets.Length; i++)
      BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(headerSize + i * 8), (ulong)resetOffsets[i]);
    return data;
  }

  // ── Shared CHM emission ──

  private static void EmitChm(Stream output, List<byte[]> entryRecords, long totalSection0, IEnumerable<byte[]> section0Data) {
    var chunks = BuildPmglChunks(entryRecords);
    var contentOffset = (long)ItsfHeaderSize;
    var dirSectionOffset = contentOffset + totalSection0;
    var dirSectionLength = ItspHeaderSize + (long)chunks.Count * ChunkSize;

    var hdr = new byte[ItsfHeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0), ItsfMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), 3);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8), ItsfHeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(56), (ulong)dirSectionOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(64), (ulong)dirSectionLength);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(72), (ulong)contentOffset);
    output.Write(hdr);

    foreach (var data in section0Data)
      output.Write(data);

    var itsp = new byte[ItspHeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(0), ItspMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(8), ItspHeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(12), 0x0A);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(16), ChunkSize);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(20), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(24), 1);
    BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(28), -1);
    BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(32), 0);
    BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(36), chunks.Count - 1);
    BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(40), -1);
    BinaryPrimitives.WriteUInt32LittleEndian(itsp.AsSpan(44), (uint)chunks.Count);
    output.Write(itsp);

    foreach (var chunk in chunks) output.Write(chunk);
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
