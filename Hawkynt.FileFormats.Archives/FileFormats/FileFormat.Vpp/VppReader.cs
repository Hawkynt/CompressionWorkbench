using System.Text;

namespace FileFormat.Vpp;

/// <summary>
/// Reads entries from a Volition Package (VPP_PC v1) archive — Red Faction 1 / Summoner era.
/// </summary>
/// <remarks>
/// VPP_PC v1 stores no per-file offset; payloads are concatenated in index order, each padded to
/// a 2048-byte boundary. The reader walks the index linearly to compute each entry's offset.
/// Versions 2/3/4 (later Volition titles) use a different on-disk layout and are explicitly rejected.
/// </remarks>
public sealed class VppReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets all entries in the archive in declaration order.</summary>
  public IReadOnlyList<VppEntry> Entries { get; }

  /// <summary>Gets the total file size declared in the header (not the actual stream length).</summary>
  public long DeclaredTotalSize { get; }

  /// <summary>
  /// Initializes a new <see cref="VppReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the VPP_PC v1 archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public VppReader(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < VppConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid VPP_PC archive.");

    Span<byte> headerHead = stackalloc byte[16];
    this._stream.Position = 0;
    ReadExact(headerHead);

    var magic = BitConverter.ToUInt32(headerHead[0..4]);
    if (magic != VppConstants.Magic)
      throw new InvalidDataException($"Invalid VPP_PC magic: 0x{magic:X8} (expected 0x{VppConstants.Magic:X8}).");

    var version = BitConverter.ToUInt32(headerHead[4..8]);
    if (version != VppConstants.SupportedVersion)
      throw new NotSupportedException($"VPP_PC version {version} is not supported (only v1 is implemented).");

    var fileCount     = BitConverter.ToUInt32(headerHead[8..12]);
    var totalFileSize = BitConverter.ToUInt32(headerHead[12..16]);
    this.DeclaredTotalSize = totalFileSize;

    if (fileCount > int.MaxValue / VppConstants.IndexEntrySize)
      throw new InvalidDataException($"Implausible VPP file count: {fileCount}.");

    this.Entries = ReadIndex((int)fileCount);
  }

  /// <summary>
  /// Extracts the raw bytes for a given entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The raw payload (entry.Size bytes).</returns>
  public byte[] Extract(VppEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    if (entry.Size > int.MaxValue)
      throw new NotSupportedException($"Entry '{entry.Name}' is too large to extract into a single byte array ({entry.Size} bytes).");

    this._stream.Position = entry.Offset;
    var data = new byte[entry.Size];
    ReadExact(data);
    return data;
  }

  private List<VppEntry> ReadIndex(int count) {
    // The index follows the 2048-byte header and is itself padded to a 2048-byte boundary;
    // file payloads then start immediately after that padded index block.
    this._stream.Position = VppConstants.HeaderSize;

    var indexBlockSize = AlignUp(count * VppConstants.IndexEntrySize, VppConstants.Alignment);
    var firstDataOffset = (long)VppConstants.HeaderSize + indexBlockSize;

    var entries = new List<VppEntry>(count);
    Span<byte> buf = stackalloc byte[VppConstants.IndexEntrySize];
    var cursor = firstDataOffset;

    for (var i = 0; i < count; ++i) {
      ReadExact(buf);

      var name = ParseName(buf[..VppConstants.NameFieldSize]);
      var size = BitConverter.ToUInt32(buf[VppConstants.NameFieldSize..VppConstants.IndexEntrySize]);

      entries.Add(new VppEntry {
        Name   = name,
        Offset = cursor,
        Size   = size,
      });

      // Each payload is padded to the alignment boundary, so the next file always starts aligned.
      cursor += AlignUp((long)size, VppConstants.Alignment);
    }

    return entries;
  }

  private static string ParseName(ReadOnlySpan<byte> nameBytes) {
    var length = nameBytes.IndexOf((byte)0);
    if (length < 0)
      length = nameBytes.Length;
    return Encoding.ASCII.GetString(nameBytes[..length]);
  }

  private static long AlignUp(long value, long alignment) {
    var remainder = value % alignment;
    return remainder == 0 ? value : value + (alignment - remainder);
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of VPP_PC stream.");
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
