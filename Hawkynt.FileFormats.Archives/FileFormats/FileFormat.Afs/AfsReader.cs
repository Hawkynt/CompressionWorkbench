using System.Text;

namespace FileFormat.Afs;

/// <summary>
/// Reads entries from a Sega AFS (Athena File System) archive — Dreamcast/PS2/GameCube games.
/// </summary>
public sealed class AfsReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets all entries discovered in the archive.</summary>
  public IReadOnlyList<AfsEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="AfsReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the AFS archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public AfsReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < AfsConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid AFS archive.");

    Span<byte> header = stackalloc byte[AfsConstants.HeaderSize];
    this._stream.Position = 0;
    ReadExact(header);

    if (!header[..4].SequenceEqual(AfsConstants.Magic))
      throw new InvalidDataException("Invalid AFS magic — expected \"AFS\\0\".");

    var fileCount = (int)BitConverter.ToUInt32(header[4..8]);
    if (fileCount < 0)
      throw new InvalidDataException($"Invalid AFS file count: {fileCount}");

    var (offsets, sizes) = ReadFileIndex(fileCount);

    // Metadata pointer is optional. If absent, file_count entries fully consume the index region
    // and the next 8 bytes may belong to the first file's data — only follow the pointer if it
    // points to a plausible region within the archive.
    var (metadataOffset, metadataSize) = ReadMetadataPointer();
    var metadata = ReadMetadataBlock(metadataOffset, metadataSize, fileCount);

    var entries = new List<AfsEntry>(fileCount);
    for (var i = 0; i < fileCount; ++i) {
      var (name, lastModified) = metadata != null
        ? metadata[i]
        : ($"file_{i + 1:D4}.bin", (DateTime?)null);
      entries.Add(new AfsEntry {
        Name = name,
        Offset = offsets[i],
        Size = sizes[i],
        LastModified = lastModified,
      });
    }
    this.Entries = entries;
  }

  /// <summary>
  /// Extracts the raw bytes for a single entry.
  /// </summary>
  /// <param name="entry">The entry to read.</param>
  /// <returns>The entry's data.</returns>
  public byte[] Extract(AfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    if (entry.Offset < 0 || entry.Size < 0 || entry.Offset + entry.Size > this._stream.Length)
      throw new InvalidDataException($"Entry '{entry.Name}' offset/size out of bounds.");

    this._stream.Position = entry.Offset;
    var data = new byte[entry.Size];
    ReadExact(data);
    return data;
  }

  private (uint[] Offsets, uint[] Sizes) ReadFileIndex(int count) {
    var offsets = new uint[count];
    var sizes = new uint[count];
    Span<byte> buf = stackalloc byte[AfsConstants.IndexEntrySize];
    for (var i = 0; i < count; ++i) {
      ReadExact(buf);
      offsets[i] = BitConverter.ToUInt32(buf[0..4]);
      sizes[i]   = BitConverter.ToUInt32(buf[4..8]);
    }
    return (offsets, sizes);
  }

  private (uint Offset, uint Size) ReadMetadataPointer() {
    // The pointer sits directly after the file index. If the stream is too short
    // to hold even those 8 bytes, treat as no metadata (legal per spec).
    if (this._stream.Position + AfsConstants.MetadataPointerSize > this._stream.Length)
      return (0, 0);

    Span<byte> buf = stackalloc byte[AfsConstants.MetadataPointerSize];
    ReadExact(buf);
    var offset = BitConverter.ToUInt32(buf[0..4]);
    var size   = BitConverter.ToUInt32(buf[4..8]);
    return (offset, size);
  }

  private List<(string Name, DateTime? LastModified)>? ReadMetadataBlock(uint metadataOffset, uint metadataSize, int fileCount) {
    if (metadataOffset == 0 || metadataSize == 0 || fileCount == 0)
      return null;

    if (metadataOffset + metadataSize > this._stream.Length)
      return null;

    var minNeeded = (long)fileCount * AfsConstants.MetadataRecordSize;
    if (metadataSize < minNeeded)
      return null;

    this._stream.Position = metadataOffset;
    var result = new List<(string, DateTime?)>(fileCount);
    Span<byte> rec = stackalloc byte[AfsConstants.MetadataRecordSize];

    for (var i = 0; i < fileCount; ++i) {
      ReadExact(rec);

      var nameSpan = rec[..AfsConstants.MetadataNameSize];
      var nullIdx = nameSpan.IndexOf((byte)0);
      var nameLen = nullIdx < 0 ? nameSpan.Length : nullIdx;
      var name = nameLen > 0
        ? Encoding.ASCII.GetString(nameSpan[..nameLen])
        : $"file_{i + 1:D4}.bin";

      var year   = BitConverter.ToUInt16(rec[32..34]);
      var month  = BitConverter.ToUInt16(rec[34..36]);
      var day    = BitConverter.ToUInt16(rec[36..38]);
      var hour   = BitConverter.ToUInt16(rec[38..40]);
      var minute = BitConverter.ToUInt16(rec[40..42]);
      var second = BitConverter.ToUInt16(rec[42..44]);
      // rec[44..48] = redundant size copy, deliberately ignored — index is authoritative.

      DateTime? lastModified = null;
      if (year >= 1 && year <= 9999 && month is >= 1 and <= 12 && day is >= 1 and <= 31
          && hour <= 23 && minute <= 59 && second <= 59) {
        try {
          lastModified = new DateTime(year, month, day, hour, minute, second);
        } catch (ArgumentOutOfRangeException) {
          lastModified = null;
        }
      }

      result.Add((name, lastModified));
    }
    return result;
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of AFS stream.");
      totalRead += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
