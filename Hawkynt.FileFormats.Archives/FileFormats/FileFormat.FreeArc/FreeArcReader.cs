#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.FreeArc;

/// <summary>
/// Reads FreeArc archives (.arc).
/// <para>
/// This implementation parses a well-defined binary subset of the FreeArc container
/// format that is produced by <see cref="FreeArcWriter"/> and understood by this reader.
/// </para>
/// <para>
/// Binary layout (all integers are little-endian):
/// <list type="bullet">
///   <item><description>4 bytes  — magic "ArC\x01"</description></item>
///   <item><description>4 bytes  — uint32 archive flags (reserved, currently 0)</description></item>
///   <item><description>One or more blocks, each preceded by a 1-byte block type:
///     <list type="bullet">
///       <item><description>0x01 = directory block</description></item>
///       <item><description>0x02 = data block</description></item>
///       <item><description>0x00 = end-of-archive marker</description></item>
///     </list>
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Directory block payload:
/// <list type="bullet">
///   <item><description>uint32 — number of file entries</description></item>
///   <item><description>Per entry: uint16 nameLen + UTF-8 name + uint64 size + uint64 compressedSize + uint64 dataOffset + uint16 methodLen + ASCII method</description></item>
/// </list>
/// </para>
/// <para>
/// Data block payload:
/// <list type="bullet">
///   <item><description>uint32 — payload length in bytes</description></item>
///   <item><description>payload bytes (concatenated raw file data)</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class FreeArcReader : IDisposable {
  /// <summary>Magic bytes at the start of every FreeArc archive.</summary>
  public static readonly byte[] Magic = [(byte)'A', (byte)'r', (byte)'C', 0x01];

  private const byte BlockTypeEnd  = 0x00;
  private const byte BlockTypeDir  = 0x01;
  private const byte BlockTypeData = 0x02;

  private readonly Stream _stream;
  private readonly bool   _leaveOpen;
  private readonly byte[] _dataBuf; // full concatenated data payload
  private bool _disposed;

  /// <summary>Gets all file entries found in the archive directory.</summary>
  public IReadOnlyList<FreeArcEntry> Entries { get; }

  /// <summary>
  /// Initialises a new <see cref="FreeArcReader"/> and parses the archive.
  /// </summary>
  /// <param name="stream">A readable stream positioned at the start of a FreeArc archive.</param>
  /// <param name="leaveOpen">
  /// <see langword="true"/> to leave <paramref name="stream"/> open after this reader is disposed;
  /// <see langword="false"/> to close it.
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <see langword="null"/>.</exception>
  /// <exception cref="InvalidDataException">Thrown when the archive signature is missing or the structure is malformed.</exception>
  public FreeArcReader(Stream stream, bool leaveOpen = false) {
    _stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    _leaveOpen = leaveOpen;

    // Validate magic.
    Span<byte> hdr = stackalloc byte[4];
    ReadExact(hdr);
    if (hdr[0] != 'A' || hdr[1] != 'r' || hdr[2] != 'C' || hdr[3] != 0x01)
      throw new InvalidDataException("Invalid FreeArc magic bytes.");

    // Archive flags (reserved).
    Span<byte> flagBuf = stackalloc byte[4];
    ReadExact(flagBuf);
    // flags currently unused

    // Read blocks sequentially.
    var entries  = new List<FreeArcEntry>();
    var dataBufs = new List<byte[]>();

    while (true) {
      var b = _stream.ReadByte();
      if (b < 0 || b == BlockTypeEnd) break;

      switch ((byte)b) {
        case BlockTypeDir:
          ParseDirectoryBlock(entries);
          break;
        case BlockTypeData:
          dataBufs.Add(ReadDataBlock());
          break;
        default:
          throw new InvalidDataException($"Unknown FreeArc block type 0x{b:X2}.");
      }
    }

    // Merge all data blocks into a single flat buffer so Extract() can index by offset.
    var totalData = dataBufs.Sum(d => d.Length);
    _dataBuf = new byte[totalData];
    var pos = 0;
    foreach (var chunk in dataBufs) {
      chunk.CopyTo(_dataBuf, pos);
      pos += chunk.Length;
    }

    Entries = entries;
  }

  // ── Extraction ───────────────────────────────────────────────────────────

  /// <summary>
  /// Extracts and returns the raw (uncompressed) bytes for the specified entry.
  /// </summary>
  /// <param name="entry">The <see cref="FreeArcEntry"/> to extract.</param>
  /// <returns>
  /// The file data as a byte array, or an empty array when the entry has zero size.
  /// </returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is <see langword="null"/>.</exception>
  /// <exception cref="InvalidDataException">Thrown when the data offset or size is out of range.</exception>
  public byte[] Extract(FreeArcEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0) return [];

    var offset = entry.DataOffset;
    var size   = entry.Size;

    if (offset < 0 || offset + size > _dataBuf.Length)
      throw new InvalidDataException(
        $"Entry '{entry.Name}': data offset {offset} + size {size} exceeds data buffer ({_dataBuf.Length} bytes).");

    var result = new byte[size];
    _dataBuf.AsSpan((int)offset, (int)size).CopyTo(result);
    return result;
  }

  // ── Block parsers ────────────────────────────────────────────────────────

  private void ParseDirectoryBlock(List<FreeArcEntry> entries) {
    // Hoist fixed-size scratch buffers outside the loop to satisfy CA2014.
    Span<byte> buf2 = stackalloc byte[2];
    Span<byte> buf4 = stackalloc byte[4];
    Span<byte> buf8 = stackalloc byte[8];

    // uint32 LE — entry count
    ReadExact(buf4);
    var count = BinaryPrimitives.ReadUInt32LittleEndian(buf4);

    for (var i = 0; i < count; i++) {
      // uint16 LE name length
      ReadExact(buf2);
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(buf2);
      if (nameLen > 4096) throw new InvalidDataException("FreeArc directory: name length too large.");

      var nameBytes = new byte[nameLen];
      ReadExact(nameBytes);
      var name = Encoding.UTF8.GetString(nameBytes);

      // uint64 LE uncompressed size
      ReadExact(buf8);
      var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf8);

      // uint64 LE compressed size
      ReadExact(buf8);
      var compressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf8);

      // uint64 LE data offset (byte offset within the concatenated data payload)
      ReadExact(buf8);
      var dataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf8);

      // uint16 LE method length + ASCII method string
      ReadExact(buf2);
      var methodLen   = BinaryPrimitives.ReadUInt16LittleEndian(buf2);
      var methodBytes = new byte[methodLen];
      ReadExact(methodBytes);
      var method = Encoding.ASCII.GetString(methodBytes);

      entries.Add(new FreeArcEntry {
        Name           = name,
        Size           = size,
        CompressedSize = compressedSize,
        DataOffset     = dataOffset,
        Method         = method,
      });
    }
  }

  private byte[] ReadDataBlock() {
    // uint32 LE payload length
    Span<byte> buf4 = stackalloc byte[4];
    ReadExact(buf4);
    var len = BinaryPrimitives.ReadUInt32LittleEndian(buf4);

    var data = new byte[len];
    ReadExact(data);
    return data;
  }

  // ── Low-level I/O ────────────────────────────────────────────────────────

  private void ReadExact(Span<byte> buf) {
    var read = 0;
    while (read < buf.Length) {
      var n = _stream.Read(buf[read..]);
      if (n == 0)
        throw new InvalidDataException(
          $"FreeArc: unexpected end of stream (needed {buf.Length} bytes, got {read}).");
      read += n;
    }
  }

  /// <inheritdoc/>
  public void Dispose() {
    if (!_disposed) {
      _disposed = true;
      if (!_leaveOpen) _stream.Dispose();
    }
  }
}
