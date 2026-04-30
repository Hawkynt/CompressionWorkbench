using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Gar;

/// <summary>
/// Reads entries from a Nintendo 3DS GAR v5 archive.
/// </summary>
public sealed class GarReader : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets all entries in the archive.</summary>
  public IReadOnlyList<GarEntry> Entries { get; }

  /// <summary>Gets the file-type extension strings indexed by <see cref="GarEntry.TypeIndex"/>.</summary>
  public IReadOnlyList<string> Extensions { get; }

  /// <summary>
  /// Initializes a new <see cref="GarReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the GAR archive.</param>
  /// <param name="leaveOpen">If true, leaves the stream open on dispose.</param>
  public GarReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < GarConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid GAR archive.");

    Span<byte> header = stackalloc byte[GarConstants.HeaderSize];
    stream.Position = 0;
    ReadExact(header);

    // Magic "GAR\x05". The version byte (0x05) is part of the magic; reject other versions
    // here so we don't silently misparse v2 archives whose header layout differs.
    if (!header[..4].SequenceEqual(GarConstants.MagicV5))
      throw new InvalidDataException("Invalid GAR magic — expected \"GAR\\x05\".");

    var headerSize       = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
    // chunkCount is informational on the read path — entries+types are sized by their counts.
    _ = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
    var fileTypeCount    = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
    var fileCount        = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
    var fileTypeOffset   = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
    var fileEntryOffset  = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);

    if (headerSize < GarConstants.HeaderSize)
      throw new InvalidDataException($"Invalid GAR header size: {headerSize}");
    if (fileTypeOffset > stream.Length || fileEntryOffset > stream.Length)
      throw new InvalidDataException("GAR table offset outside stream bounds.");

    var (extensions, typeIndexOffsets) = ReadFileTypeTable(fileTypeOffset, (int)fileTypeCount);
    this.Extensions = extensions;
    this.Entries    = ReadFileEntryTable(fileEntryOffset, (int)fileCount, extensions);

    // typeIndexOffsets is parsed but unused on the read side — entries point to type indices
    // directly, the per-type index list is redundant for sequential reading.
    _ = typeIndexOffsets;
  }

  /// <summary>
  /// Extracts the raw payload bytes for a given entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The file payload (uncompressed; GAR stores data raw).</returns>
  public byte[] Extract(GarEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0)
      return [];

    if (entry.Offset < 0 || entry.Offset + entry.Size > this._stream.Length)
      throw new InvalidDataException($"GAR entry '{entry.Name}' data range is outside stream bounds.");

    this._stream.Position = entry.Offset;
    var buffer = new byte[entry.Size];
    ReadExact(buffer);
    return buffer;
  }

  private (List<string> extensions, List<uint> typeIndexOffsets) ReadFileTypeTable(uint offset, int count) {
    var extensions = new List<string>(count);
    var typeIndexOffsets = new List<uint>(count);

    if (count == 0)
      return (extensions, typeIndexOffsets);

    this._stream.Position = offset;
    Span<byte> buf = stackalloc byte[GarConstants.FileTypeEntrySize];

    // First pass: read the structural fields, defer string-pool reads until after — the
    // string pool may overlap the entry table region in pathological inputs but typically
    // the order is type table → entry table → strings, so a single pass with seeks is safe.
    var extOffsets = new uint[count];
    for (var i = 0; i < count; ++i) {
      ReadExact(buf);
      // typeCount and reserved are not needed for reading; we drive iteration from FileCount.
      _ = BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]);
      typeIndexOffsets.Add(BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]));
      extOffsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..12]);
      _ = BinaryPrimitives.ReadUInt32LittleEndian(buf[12..16]);
    }

    foreach (var extOffset in extOffsets)
      extensions.Add(ReadCStringAt(extOffset));

    return (extensions, typeIndexOffsets);
  }

  private List<GarEntry> ReadFileEntryTable(uint offset, int count, IReadOnlyList<string> extensions) {
    var entries = new List<GarEntry>(count);
    if (count == 0)
      return entries;

    this._stream.Position = offset;
    Span<byte> buf = stackalloc byte[GarConstants.FileEntrySize];

    var dataSizes  = new uint[count];
    var dataOffs   = new uint[count];
    var nameOffs   = new uint[count];
    var typeIdxArr = new uint[count];

    for (var i = 0; i < count; ++i) {
      ReadExact(buf);
      dataSizes[i]  = BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]);
      dataOffs[i]   = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]);
      nameOffs[i]   = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..12]);
      typeIdxArr[i] = BinaryPrimitives.ReadUInt32LittleEndian(buf[12..16]);
    }

    for (var i = 0; i < count; ++i) {
      var typeIndex = (int)typeIdxArr[i];
      var baseName = ReadCStringAt(nameOffs[i]);
      // Empty extension means the original input had no dot — preserve that faithfully so
      // round-tripping a "rawfile" entry doesn't silently become "rawfile.".
      var ext = (typeIndex >= 0 && typeIndex < extensions.Count) ? extensions[typeIndex] : "";
      var fullName = string.IsNullOrEmpty(ext) ? baseName : baseName + "." + ext;

      entries.Add(new GarEntry {
        Name      = fullName,
        Offset    = dataOffs[i],
        Size      = dataSizes[i],
        TypeIndex = typeIndex,
      });
    }

    return entries;
  }

  private string ReadCStringAt(uint offset) {
    this._stream.Position = offset;
    using var sb = new MemoryStream(64);
    while (true) {
      var b = this._stream.ReadByte();
      if (b <= 0)
        break;
      sb.WriteByte((byte)b);
    }
    return Encoding.ASCII.GetString(sb.ToArray());
  }

  private void ReadExact(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = this._stream.Read(buffer[total..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of GAR stream.");
      total += read;
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
