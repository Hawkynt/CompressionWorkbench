using System.Buffers.Binary;
using System.Text;

namespace FileFormat.CompactPro;

/// <summary>
/// Reads entries from a Compact Pro (.cpt) archive.
/// </summary>
/// <remarks>
/// Supports the classic Compact Pro format (Bill Goodman, 1990-1998) with Store (method 0)
/// and RLE (method 1) decompression. Methods 2 (LZ+RLE) and 3 (LZ+Huffman) throw
/// <see cref="NotSupportedException"/>. Resource forks are available via
/// <see cref="ExtractResourceFork"/>.
/// </remarks>
public sealed class CompactProReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<CompactProEntry> _entries = [];
  private bool _disposed;

  /// <summary>
  /// Opens a Compact Pro archive from the given stream and parses the entry directory.
  /// </summary>
  /// <param name="stream">A seekable stream containing a .cpt archive.</param>
  /// <param name="leaveOpen">
  /// <see langword="true"/> to leave <paramref name="stream"/> open after this reader is disposed.
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="InvalidDataException">Thrown when the stream does not contain a valid Compact Pro archive.</exception>
  public CompactProReader(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this.ParseArchive();
  }

  /// <summary>Gets the list of file entries found in the archive.</summary>
  public IReadOnlyList<CompactProEntry> Entries => this._entries;

  /// <summary>
  /// Extracts and decompresses the data fork of the specified entry.
  /// </summary>
  /// <param name="entry">An entry obtained from <see cref="Entries"/>.</param>
  /// <returns>The decompressed data fork bytes.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
  /// <exception cref="InvalidOperationException">Thrown when the entry is a directory.</exception>
  /// <exception cref="NotSupportedException">Thrown when the compression method is not supported.</exception>
  /// <exception cref="InvalidDataException">Thrown when the CRC-16 does not match.</exception>
  public byte[] Extract(CompactProEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (entry.IsDirectory)
      throw new InvalidOperationException("Cannot extract a directory entry.");

    if (entry.DataForkCompressedSize == 0 && entry.DataForkSize == 0)
      return [];

    this._stream.Position = entry.DataOffset;
    byte[] compressed = this.ReadExact((int)entry.DataForkCompressedSize);
    byte[] decompressed = Decompress(entry.DataForkMethod, compressed, (int)entry.DataForkSize);

    VerifyCrc16(decompressed, entry.DataForkCrc, entry.FileName, "data fork");
    return decompressed;
  }

  /// <summary>
  /// Extracts and decompresses the resource fork of the specified entry.
  /// </summary>
  /// <param name="entry">An entry obtained from <see cref="Entries"/>.</param>
  /// <returns>The decompressed resource fork bytes.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
  /// <exception cref="InvalidOperationException">Thrown when the entry is a directory.</exception>
  /// <exception cref="NotSupportedException">Thrown when the compression method is not supported.</exception>
  /// <exception cref="InvalidDataException">Thrown when the CRC-16 does not match.</exception>
  public byte[] ExtractResourceFork(CompactProEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (entry.IsDirectory)
      throw new InvalidOperationException("Cannot extract a directory entry.");

    if (entry.ResourceForkCompressedSize == 0 && entry.ResourceForkSize == 0)
      return [];

    this._stream.Position = entry.ResourceOffset;
    byte[] compressed = this.ReadExact((int)entry.ResourceForkCompressedSize);
    byte[] decompressed = Decompress(entry.ResourceForkMethod, compressed, (int)entry.ResourceForkSize);

    VerifyCrc16(decompressed, entry.ResourceForkCrc, entry.FileName, "resource fork");
    return decompressed;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  // ── Parsing ──────────────────────────────────────────────────────────────────

  private void ParseArchive() {
    Span<byte> header = stackalloc byte[CompactProConstants.VolumeHeaderSize];
    this.ReadExact(header);

    if (header[0] != CompactProConstants.Magic)
      throw new InvalidDataException(
        $"Not a Compact Pro archive. Expected magic 0x{CompactProConstants.Magic:X2} at offset 0, found 0x{header[0]:X2}.");

    ushort entryCount = BinaryPrimitives.ReadUInt16BigEndian(header[1..]);

    // Track where compressed data will start (after all headers).
    // We do a two-pass approach: first read all headers to know sizes,
    // then compute offsets from the accumulated compressed sizes.
    var rawEntries = new List<RawEntry>(entryCount);
    this.ReadEntries(rawEntries, entryCount);

    // Compute data offsets: compressed data follows immediately after all headers.
    long dataPosition = this._stream.Position;
    foreach (RawEntry raw in rawEntries) {
      if (raw.IsDirectory)
        continue;

      long dataOffset = dataPosition;
      long resourceOffset = dataOffset + raw.DataForkCompressedSize;
      dataPosition = resourceOffset + raw.ResourceForkCompressedSize;

      this._entries.Add(new CompactProEntry {
        FileName                  = raw.FileName,
        IsDirectory               = false,
        DataForkSize              = raw.DataForkSize,
        ResourceForkSize          = raw.ResourceForkSize,
        DataForkCompressedSize    = raw.DataForkCompressedSize,
        ResourceForkCompressedSize = raw.ResourceForkCompressedSize,
        DataForkMethod            = raw.DataForkMethod,
        ResourceForkMethod        = raw.ResourceForkMethod,
        DataForkCrc               = raw.DataForkCrc,
        ResourceForkCrc           = raw.ResourceForkCrc,
        FileType                  = raw.FileType,
        FileCreator               = raw.FileCreator,
        CreatedDate               = raw.CreatedDate,
        ModifiedDate              = raw.ModifiedDate,
        DataOffset                = dataOffset,
        ResourceOffset            = resourceOffset,
      });
    }
  }

  private void ReadEntries(List<RawEntry> entries, int count) {
    for (int i = 0; i < count; ++i) {
      byte entryType = this.ReadByte();

      switch (entryType) {
        case CompactProConstants.EntryTypeFile:
          entries.Add(this.ReadFileEntry());
          break;

        case CompactProConstants.EntryTypeFolder:
          this.ReadFolderEntry(entries);
          break;

        case CompactProConstants.EntryTypeEnd:
          // End-of-folder marker — caller handles this via count.
          return;

        default:
          throw new InvalidDataException(
            $"Unknown Compact Pro entry type: 0x{entryType:X2}.");
      }
    }
  }

  private RawEntry ReadFileEntry() {
    byte nameLength = this.ReadByte();
    if (nameLength > CompactProConstants.FileNameMaxLength)
      nameLength = CompactProConstants.FileNameMaxLength;

    byte[] nameBytes = this.ReadExact(nameLength);
    string fileName = Encoding.Latin1.GetString(nameBytes);

    // Read compression methods.
    byte dataMethod = this.ReadByte();
    byte resourceMethod = this.ReadByte();

    // Read sizes and CRCs (all big-endian).
    Span<byte> buf = stackalloc byte[4];

    this.ReadExact(buf);
    uint dataForkSize = BinaryPrimitives.ReadUInt32BigEndian(buf);

    this.ReadExact(buf);
    uint dataForkCompressedSize = BinaryPrimitives.ReadUInt32BigEndian(buf);

    Span<byte> crcBuf = stackalloc byte[2];
    this.ReadExact(crcBuf);
    ushort dataForkCrc = BinaryPrimitives.ReadUInt16BigEndian(crcBuf);

    this.ReadExact(buf);
    uint resourceForkSize = BinaryPrimitives.ReadUInt32BigEndian(buf);

    this.ReadExact(buf);
    uint resourceForkCompressedSize = BinaryPrimitives.ReadUInt32BigEndian(buf);

    this.ReadExact(crcBuf);
    ushort resourceForkCrc = BinaryPrimitives.ReadUInt16BigEndian(crcBuf);

    // File type + creator (4 bytes each).
    this.ReadExact(buf);
    uint fileType = BinaryPrimitives.ReadUInt32BigEndian(buf);

    this.ReadExact(buf);
    uint fileCreator = BinaryPrimitives.ReadUInt32BigEndian(buf);

    // Creation date (Mac epoch seconds, uint32 BE).
    this.ReadExact(buf);
    uint createdMac = BinaryPrimitives.ReadUInt32BigEndian(buf);

    // Modification date (Mac epoch seconds, uint32 BE).
    this.ReadExact(buf);
    uint modifiedMac = BinaryPrimitives.ReadUInt32BigEndian(buf);

    return new RawEntry {
      FileName                   = fileName,
      IsDirectory                = false,
      DataForkMethod             = dataMethod,
      ResourceForkMethod         = resourceMethod,
      DataForkSize               = dataForkSize,
      DataForkCompressedSize     = dataForkCompressedSize,
      DataForkCrc                = dataForkCrc,
      ResourceForkSize           = resourceForkSize,
      ResourceForkCompressedSize = resourceForkCompressedSize,
      ResourceForkCrc            = resourceForkCrc,
      FileType                   = fileType,
      FileCreator                = fileCreator,
      CreatedDate                = MacToDateTime(createdMac),
      ModifiedDate               = MacToDateTime(modifiedMac),
    };
  }

  private void ReadFolderEntry(List<RawEntry> entries) {
    byte nameLength = this.ReadByte();
    if (nameLength > CompactProConstants.FileNameMaxLength)
      nameLength = CompactProConstants.FileNameMaxLength;

    byte[] nameBytes = this.ReadExact(nameLength);
    // string folderName = Encoding.Latin1.GetString(nameBytes); // Available if needed.

    Span<byte> countBuf = stackalloc byte[2];
    this.ReadExact(countBuf);
    ushort itemCount = BinaryPrimitives.ReadUInt16BigEndian(countBuf);

    // Folders are not added to the flat file list, but we recurse into them.
    this.ReadEntries(entries, itemCount);

    // Consume the end-of-folder marker (0x02) that follows the folder's children.
    this.ReadByte();
  }

  // ── Decompression ─────────────────────────────────────────────────────────────

  private static byte[] Decompress(int method, byte[] compressed, int uncompressedSize) =>
    method switch {
      CompactProConstants.MethodStored => DecompressStored(compressed, uncompressedSize),
      CompactProConstants.MethodRle    => DecompressRle(compressed, uncompressedSize),
      CompactProConstants.MethodLzRle  => throw new NotSupportedException(
                                           "Compact Pro LZ+RLE compression (method 2) is not supported."),
      CompactProConstants.MethodLzHuff => throw new NotSupportedException(
                                           "Compact Pro LZ+Huffman compression (method 3) is not supported."),
      _                               => throw new NotSupportedException(
                                           $"Compact Pro compression method {method} is not supported."),
    };

  private static byte[] DecompressStored(byte[] compressed, int uncompressedSize) {
    if (compressed.Length == uncompressedSize)
      return compressed;

    // If lengths differ, copy only the expected amount.
    byte[] result = new byte[uncompressedSize];
    Buffer.BlockCopy(compressed, 0, result, 0, Math.Min(compressed.Length, uncompressedSize));
    return result;
  }

  /// <summary>
  /// Decodes RLE data using the escape byte approach:
  /// - If byte is 0x90 followed by 0x00 → emit literal 0x90
  /// - If byte is 0x90 followed by count N (1-255) → repeat previous byte N times
  /// - Otherwise → emit byte literally
  /// </summary>
  private static byte[] DecompressRle(byte[] compressed, int uncompressedSize) {
    byte[] output = new byte[uncompressedSize];
    int src = 0;
    int dst = 0;
    byte lastByte = 0;

    while (src < compressed.Length && dst < uncompressedSize) {
      byte b = compressed[src++];

      if (b == CompactProConstants.RleEscape) {
        if (src >= compressed.Length)
          break;

        byte count = compressed[src++];
        if (count == 0) {
          // Literal 0x90.
          lastByte = CompactProConstants.RleEscape;
          if (dst < uncompressedSize)
            output[dst++] = lastByte;
        } else {
          // Repeat previous byte 'count' times.
          for (int i = 0; i < count && dst < uncompressedSize; ++i)
            output[dst++] = lastByte;
        }
      } else {
        lastByte = b;
        output[dst++] = b;
      }
    }

    return output;
  }

  // ── CRC-16/CCITT (forward, non-reflected, init=0) ─────────────────────────────

  private static readonly ushort[] Crc16Table = BuildCrc16Table();

  private static ushort[] BuildCrc16Table() {
    const ushort poly = CompactProConstants.Crc16Polynomial;
    var table = new ushort[256];
    for (int i = 0; i < 256; ++i) {
      ushort crc = (ushort)(i << 8);
      for (int j = 0; j < 8; ++j)
        crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ poly) : (ushort)(crc << 1);
      table[i] = crc;
    }
    return table;
  }

  private static ushort ComputeCrc16(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (byte b in data)
      crc = (ushort)((crc << 8) ^ Crc16Table[(byte)(crc >> 8) ^ b]);
    return crc;
  }

  private static void VerifyCrc16(byte[] data, ushort expected, string fileName, string forkName) {
    if (expected == 0)
      return; // zero means no checksum stored

    ushort actual = ComputeCrc16(data);
    if (actual != expected)
      throw new InvalidDataException(
        $"CRC-16 mismatch for '{fileName}' ({forkName}): expected 0x{expected:X4}, computed 0x{actual:X4}.");
  }

  // ── Mac epoch conversion ───────────────────────────────────────────────────────

  private static DateTime MacToDateTime(uint macSeconds) =>
    macSeconds == 0 ? DateTime.MinValue : CompactProConstants.MacEpoch.AddSeconds(macSeconds);

  // ── Stream helpers ─────────────────────────────────────────────────────────────

  private byte ReadByte() {
    int b = this._stream.ReadByte();
    if (b < 0)
      throw new EndOfStreamException("Unexpected end of stream reading Compact Pro data.");
    return (byte)b;
  }

  private byte[] ReadExact(int count) {
    byte[] buf = new byte[count];
    this.ReadExact(buf.AsSpan());
    return buf;
  }

  private void ReadExact(Span<byte> buffer) {
    int total = 0;
    while (total < buffer.Length) {
      int read = this._stream.Read(buffer[total..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of stream reading Compact Pro data.");
      total += read;
    }
  }

  // ── Internal raw entry ─────────────────────────────────────────────────────────

  private sealed class RawEntry {
    public string FileName { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public byte DataForkMethod { get; init; }
    public byte ResourceForkMethod { get; init; }
    public uint DataForkSize { get; init; }
    public uint DataForkCompressedSize { get; init; }
    public ushort DataForkCrc { get; init; }
    public uint ResourceForkSize { get; init; }
    public uint ResourceForkCompressedSize { get; init; }
    public ushort ResourceForkCrc { get; init; }
    public uint FileType { get; init; }
    public uint FileCreator { get; init; }
    public DateTime CreatedDate { get; init; }
    public DateTime ModifiedDate { get; init; }
  }
}
