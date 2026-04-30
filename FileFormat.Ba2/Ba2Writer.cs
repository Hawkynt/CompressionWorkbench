using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileFormat.Ba2;

/// <summary>
/// Builds a BA2 GNRL archive (BTDX v1). DX10 texture archives are not produced by this writer.
/// </summary>
public sealed class Ba2Writer : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Path, byte[] Data)> _entries = [];
  private readonly bool _useCompression;
  private bool _finished;
  private bool _disposed;

  /// <param name="stream">Output stream. Must be seekable — header offsets are back-patched after data is written.</param>
  /// <param name="leaveOpen">Leave <paramref name="stream"/> open on Dispose.</param>
  /// <param name="compress">When true, attempt zlib compression per file and only keep it if it shrinks the payload.</param>
  public Ba2Writer(Stream stream, bool leaveOpen = false, bool compress = true) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("BA2 writer requires a seekable stream.", nameof(stream));
    this._leaveOpen = leaveOpen;
    this._useCompression = compress;
  }

  /// <summary>Buffers an entry to be written on Finish/Dispose. <paramref name="path"/> is normalised to backslash separators.</summary>
  public void AddEntry(string path, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);

    var normalised = path.Replace('/', '\\').Trim('\\');
    this._entries.Add((normalised, data));
  }

  /// <summary>Flushes all buffered entries to the output stream and writes the BA2 header, records, payloads, and name table.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    var count = this._entries.Count;

    // Pre-compress every payload up front so we know each record's PackedSize before laying out
    // the header. Storing uncompressed when zlib doesn't shrink (PackedSize = 0) is the standard
    // Bethesda convention — their tools recognise it as "stored".
    var payloads = new byte[count][];
    var packedSizes = new uint[count];
    for (var i = 0; i < count; ++i) {
      var raw = this._entries[i].Data;
      if (this._useCompression && raw.Length > 0) {
        var compressed = Deflate(raw);
        if (compressed.Length < raw.Length) {
          payloads[i] = compressed;
          packedSizes[i] = (uint)compressed.Length;
          continue;
        }
      }
      payloads[i] = raw;
      packedSizes[i] = 0;
    }

    // Layout: header (24) + records (36 * count) + payloads + name table.
    var headerStart = this._stream.Position;
    Span<byte> header = stackalloc byte[Ba2Constants.HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], Ba2Constants.Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], Ba2Constants.Version1);
    BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], Ba2Constants.TypeGnrl);
    BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], (uint)count);
    BinaryPrimitives.WriteUInt64LittleEndian(header[16..24], 0); // backpatched once name table offset is known
    this._stream.Write(header);

    // Reserve the records region so we can write payloads at known offsets, then come back and fill records.
    var recordsStart = this._stream.Position;
    var recordsLength = Ba2Constants.RecordSize * (long)count;
    this._stream.Position = recordsStart + recordsLength;

    // Write payloads, capturing each absolute offset.
    var offsets = new long[count];
    for (var i = 0; i < count; ++i) {
      offsets[i] = this._stream.Position;
      if (payloads[i].Length > 0)
        this._stream.Write(payloads[i]);
    }

    // Name table: each entry is uint16 LE length followed by UTF-8 bytes (no terminator).
    var nameTableOffset = this._stream.Position;
    Span<byte> lenBuf = stackalloc byte[2];
    for (var i = 0; i < count; ++i) {
      var nameBytes = Encoding.UTF8.GetBytes(this._entries[i].Path);
      if (nameBytes.Length > ushort.MaxValue)
        throw new InvalidDataException($"BA2 entry name too long: {nameBytes.Length} bytes.");
      BinaryPrimitives.WriteUInt16LittleEndian(lenBuf, (ushort)nameBytes.Length);
      this._stream.Write(lenBuf);
      this._stream.Write(nameBytes);
    }

    // Backpatch nameTableOffset in the header.
    var endOfArchive = this._stream.Position;
    this._stream.Position = headerStart + 16;
    Span<byte> off = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(off, (ulong)nameTableOffset);
    this._stream.Write(off);

    // Now fill the records section with all hashes/offsets/sizes resolved.
    this._stream.Position = recordsStart;
    Span<byte> rec = stackalloc byte[Ba2Constants.RecordSize];
    for (var i = 0; i < count; ++i) {
      rec.Clear();

      var path = this._entries[i].Path;
      var (dirPart, basePart, extPart) = SplitPath(path);

      var nameHash = BethesdaLookup3.HashLower(basePart);
      var dirHash = BethesdaLookup3.HashLower(dirPart);

      BinaryPrimitives.WriteUInt32LittleEndian(rec[0..4], nameHash);
      WriteExtension(rec[4..8], extPart);
      BinaryPrimitives.WriteUInt32LittleEndian(rec[8..12], dirHash);
      BinaryPrimitives.WriteUInt32LittleEndian(rec[12..16], 0); // flags
      BinaryPrimitives.WriteUInt64LittleEndian(rec[16..24], (ulong)offsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(rec[24..28], packedSizes[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(rec[28..32], (uint)this._entries[i].Data.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(rec[32..36], Ba2Constants.RecordSentinel);
      this._stream.Write(rec);
    }

    this._stream.Position = endOfArchive;
  }

  private static (string Dir, string BaseName, string Ext) SplitPath(string path) {
    // Path components are normalised on AddEntry to backslash separators. We split manually rather
    // than using System.IO.Path because Bethesda paths use '\' regardless of host OS.
    var lastSlash = path.LastIndexOf('\\');
    var dir = lastSlash < 0 ? "" : path[..lastSlash];
    var fileName = lastSlash < 0 ? path : path[(lastSlash + 1)..];

    var lastDot = fileName.LastIndexOf('.');
    var baseName = lastDot < 0 ? fileName : fileName[..lastDot];
    var ext = lastDot < 0 ? "" : fileName[(lastDot + 1)..];

    return (dir, baseName, ext);
  }

  private static void WriteExtension(Span<byte> field, string ext) {
    var lower = ext.ToLowerInvariant();
    var bytes = Encoding.ASCII.GetBytes(lower);
    var n = Math.Min(bytes.Length, field.Length);
    bytes.AsSpan(0, n).CopyTo(field);
    // Remaining bytes in the 4-byte field stay 0 (NUL-padded) thanks to rec.Clear() at the call site.
  }

  private static byte[] Deflate(byte[] raw) {
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      z.Write(raw, 0, raw.Length);
    return ms.ToArray();
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
