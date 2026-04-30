using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileFormat.Ba2;

/// <summary>
/// Reads a BA2 (Bethesda Archive v2) GNRL archive — Fallout 4 / Skyrim SE / Starfield (v1).
/// DX10 texture archives are not supported by this reader.
/// </summary>
public sealed class Ba2Reader : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>BA2 archive version (1 = FO4/SSE; 7/8 are Starfield variants).</summary>
  public uint Version { get; }

  /// <summary>All file records, in archive order.</summary>
  public IReadOnlyList<Ba2Entry> Entries { get; }

  /// <summary>Parses the BA2 header, validates GNRL type, and loads all records and names.</summary>
  /// <param name="stream">Source stream positioned at the start of the BA2 archive.</param>
  /// <param name="leaveOpen">If false, <paramref name="stream"/> is disposed when the reader is disposed.</param>
  public Ba2Reader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < Ba2Constants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid BA2 archive.");

    // Header is fixed-size, fits comfortably on the stack.
    Span<byte> header = stackalloc byte[Ba2Constants.HeaderSize];
    ReadExact(header);

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
    if (magic != Ba2Constants.Magic)
      throw new InvalidDataException("Invalid BA2 magic — expected 'BTDX'.");

    this.Version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);

    var type = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
    if (type == Ba2Constants.TypeDx10)
      throw new NotSupportedException("DX10 texture archives are not yet supported.");
    if (type != Ba2Constants.TypeGnrl)
      throw new InvalidDataException($"Unsupported BA2 archive type: 0x{type:X8} (expected GNRL).");

    var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
    var nameTableOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[16..24]);

    if (fileCount > int.MaxValue)
      throw new InvalidDataException($"Implausible BA2 file count: {fileCount}.");
    if (nameTableOffset < 0 || nameTableOffset > stream.Length)
      throw new InvalidDataException($"Invalid name-table offset: {nameTableOffset}.");

    var entries = ReadRecords((int)fileCount);
    ReadNames(entries, nameTableOffset);
    this.Entries = entries;
  }

  /// <summary>
  /// Returns the decompressed payload of the given entry. When <see cref="Ba2Entry.PackedSize"/> is 0
  /// the file is stored verbatim; otherwise it is a zlib stream of <c>PackedSize</c> bytes.
  /// </summary>
  public byte[] Extract(Ba2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    this._stream.Position = entry.Offset;

    if (entry.PackedSize == 0) {
      var raw = new byte[entry.Size];
      ReadExact(raw);
      return raw;
    }

    // Zlib stream of PackedSize bytes producing exactly Size bytes. We slice the substream
    // so the ZLibStream doesn't read past the entry.
    var compressed = new byte[entry.PackedSize];
    ReadExact(compressed);

    using var compressedMs = new MemoryStream(compressed, writable: false);
    using var zlib = new ZLibStream(compressedMs, CompressionMode.Decompress);
    var output = new byte[entry.Size];
    var read = 0;
    while (read < output.Length) {
      var n = zlib.Read(output, read, output.Length - read);
      if (n == 0)
        throw new InvalidDataException("Truncated zlib stream in BA2 entry.");
      read += n;
    }
    return output;
  }

  private List<Ba2Entry> ReadRecords(int count) {
    var entries = new List<Ba2Entry>(count);
    Span<byte> buf = stackalloc byte[Ba2Constants.RecordSize];

    for (var i = 0; i < count; ++i) {
      ReadExact(buf);

      var nameHash = BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]);
      var ext = ParseExtension(buf[4..8]);
      var dirHash = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..12]);
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(buf[12..16]);
      var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf[16..24]);
      var packedSize = BinaryPrimitives.ReadUInt32LittleEndian(buf[24..28]);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(buf[28..32]);
      // Sentinel at buf[32..36] is informational; not all third-party tools write 0xBAADF00D and we
      // accept anything to stay liberal in what we read.

      entries.Add(new Ba2Entry {
        NameHash = nameHash,
        Ext = ext,
        DirHash = dirHash,
        Flags = flags,
        Offset = offset,
        PackedSize = packedSize,
        Size = size,
      });
    }

    return entries;
  }

  private void ReadNames(List<Ba2Entry> entries, long nameTableOffset) {
    this._stream.Position = nameTableOffset;
    Span<byte> lenBuf = stackalloc byte[2];

    for (var i = 0; i < entries.Count; ++i) {
      ReadExact(lenBuf);
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(lenBuf);

      // Don't stackalloc with attacker-controlled length.
      var nameBytes = new byte[nameLen];
      ReadExact(nameBytes);
      var name = Encoding.UTF8.GetString(nameBytes);

      entries[i] = new Ba2Entry {
        Name = name,
        NameHash = entries[i].NameHash,
        Ext = entries[i].Ext,
        DirHash = entries[i].DirHash,
        Flags = entries[i].Flags,
        Offset = entries[i].Offset,
        PackedSize = entries[i].PackedSize,
        Size = entries[i].Size,
      };
    }
  }

  private static string ParseExtension(ReadOnlySpan<byte> field) {
    var nul = field.IndexOf((byte)0);
    var len = nul < 0 ? field.Length : nul;
    return Encoding.ASCII.GetString(field[..len]);
  }

  private void ReadExact(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var n = this._stream.Read(buffer[total..]);
      if (n == 0)
        throw new EndOfStreamException("Unexpected end of BA2 stream.");
      total += n;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
