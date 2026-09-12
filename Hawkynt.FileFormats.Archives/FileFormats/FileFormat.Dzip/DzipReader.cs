using System.Text;

namespace FileFormat.Dzip;

/// <summary>
/// Reads entries from a Bloodlines DZIP v2 archive (Vampire: The Masquerade — Bloodlines).
/// Handles both stored and LZSS-compressed entries.
/// </summary>
public sealed class DzipReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets all entries in the DZIP archive.</summary>
  public IReadOnlyList<DzipEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="DzipReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the DZIP archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public DzipReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < DzipConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid DZIP archive.");

    Span<byte> header = stackalloc byte[DzipConstants.HeaderSize];
    this._stream.Position = 0;
    ReadExact(header);

    if (!header[..4].SequenceEqual(DzipConstants.MagicBytes))
      throw new InvalidDataException($"Invalid DZIP magic: expected '{DzipConstants.MagicString}'.");

    var version = BitConverter.ToUInt32(header[4..8]);
    if (version != DzipConstants.SupportedVersion)
      throw new NotSupportedException($"Unsupported DZIP version {version}; only version {DzipConstants.SupportedVersion} is supported.");

    var fileCount = BitConverter.ToUInt32(header[8..12]);
    var tocOffset = BitConverter.ToUInt32(header[12..16]);

    if (tocOffset > stream.Length)
      throw new InvalidDataException($"DZIP TOC offset {tocOffset} exceeds stream length {stream.Length}.");

    this._stream.Position = tocOffset;
    this.Entries = ReadToc((int)fileCount);
  }

  /// <summary>
  /// Extracts the bytes for a given entry. Decompresses LZSS-compressed entries automatically.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The uncompressed entry data.</returns>
  public byte[] Extract(DzipEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.CompressedSize == 0)
      return [];

    this._stream.Position = entry.Offset;
    var disk = new byte[entry.CompressedSize];
    ReadExact(disk);

    if (entry.CompressionFlag == 0) {
      if (entry.CompressedSize != entry.Size)
        throw new InvalidDataException($"Stored DZIP entry '{entry.Name}': compressed size {entry.CompressedSize} != size {entry.Size}.");
      return disk;
    }

    return DzipLzss.Decompress(disk, (int)entry.Size);
  }

  private List<DzipEntry> ReadToc(int count) {
    var entries = new List<DzipEntry>(count);
    Span<byte> tail = stackalloc byte[13];
    Span<byte> pathBuf = stackalloc byte[DzipConstants.MaxPathLength];

    for (var i = 0; i < count; ++i) {
      var pathLen = ReadByte();
      var pathSlice = pathBuf[..pathLen];
      ReadExact(pathSlice);
      var name = Encoding.ASCII.GetString(pathSlice);

      ReadExact(tail);

      var offset = BitConverter.ToUInt32(tail[0..4]);
      var compressedSize = BitConverter.ToUInt32(tail[4..8]);
      var size = BitConverter.ToUInt32(tail[8..12]);
      var flag = tail[12];

      entries.Add(new DzipEntry {
        Name = name,
        Offset = offset,
        CompressedSize = compressedSize,
        Size = size,
        CompressionFlag = flag,
      });
    }

    return entries;
  }

  private byte ReadByte() {
    var b = this._stream.ReadByte();
    if (b < 0)
      throw new EndOfStreamException("Unexpected end of DZIP stream.");
    return (byte)b;
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of DZIP stream.");
      totalRead += read;
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
