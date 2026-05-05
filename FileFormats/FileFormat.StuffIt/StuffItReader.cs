using System.Buffers.Binary;
using System.Text;

namespace FileFormat.StuffIt;

/// <summary>
/// Reads entries from a StuffIt (SIT) classic archive.
/// </summary>
/// <remarks>
/// Supports the classic SIT format (magic "SIT!", version 1) with Store (method 0)
/// and RLE (method 1) compression for the data fork. Resource forks are available
/// via <see cref="ExtractResourceFork"/>.
/// </remarks>
public sealed class StuffItReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<StuffItEntry> _entries = [];
  private bool _disposed;

  /// <summary>
  /// Opens a StuffIt archive from the given stream and parses the entry directory.
  /// </summary>
  /// <param name="stream">A seekable stream containing a SIT archive.</param>
  /// <param name="leaveOpen">
  /// <see langword="true"/> to leave <paramref name="stream"/> open after this reader is disposed.
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="InvalidDataException">Thrown when the stream does not contain a valid SIT archive.</exception>
  public StuffItReader(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    ParseArchive();
  }

  /// <summary>Gets the list of entries found in the archive.</summary>
  public IReadOnlyList<StuffItEntry> Entries => this._entries;

  /// <summary>
  /// Extracts and decompresses the data fork of the specified entry.
  /// </summary>
  /// <param name="entry">An entry obtained from <see cref="Entries"/>.</param>
  /// <returns>The decompressed data fork bytes.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
  /// <exception cref="NotSupportedException">
  /// Thrown when the data fork compression method is not supported.
  /// </exception>
  /// <exception cref="InvalidDataException">Thrown when the CRC-16 of the decompressed data does not match.</exception>
  public byte[] Extract(StuffItEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (entry.CompressedDataSize == 0 && entry.DataForkSize == 0)
      return [];

    this._stream.Position = entry.DataForkOffset;
    var compressed = ReadExact((int)entry.CompressedDataSize);
    var decompressed = Decompress(entry.DataMethod, compressed, (int)entry.DataForkSize);

    VerifyCrc16(decompressed, entry.DataForkCrc16, entry.FileName, "data fork");
    return decompressed;
  }

  /// <summary>
  /// Extracts and decompresses the resource fork of the specified entry.
  /// </summary>
  /// <param name="entry">An entry obtained from <see cref="Entries"/>.</param>
  /// <returns>The decompressed resource fork bytes.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
  /// <exception cref="NotSupportedException">
  /// Thrown when the resource fork compression method is not supported.
  /// </exception>
  /// <exception cref="InvalidDataException">Thrown when the CRC-16 does not match.</exception>
  public byte[] ExtractResourceFork(StuffItEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (entry.CompressedResourceSize == 0 && entry.ResourceForkSize == 0)
      return [];

    this._stream.Position = entry.ResourceDataOffset;
    var compressed = ReadExact((int)entry.CompressedResourceSize);
    var decompressed = Decompress(entry.ResourceMethod, compressed, (int)entry.ResourceForkSize);

    VerifyCrc16(decompressed, entry.ResourceForkCrc16, entry.FileName, "resource fork");
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
    Span<byte> archiveHeader = stackalloc byte[StuffItConstants.ArchiveHeaderSize];
    ReadExact(archiveHeader);

    var magic = BinaryPrimitives.ReadUInt32BigEndian(archiveHeader);
    if (magic != StuffItConstants.MagicSit)
      throw new InvalidDataException(
        $"Not a StuffIt archive. Expected magic 0x{StuffItConstants.MagicSit:X8}, found 0x{magic:X8}.");

    var fileCount = BinaryPrimitives.ReadUInt16BigEndian(archiveHeader[4..]);
    // archiveHeader[6..10] = total archive length (uint32 BE) — informational only.
    var signature = BinaryPrimitives.ReadUInt32BigEndian(archiveHeader[10..]);
    if (signature != StuffItConstants.ArchiveSignatureRLau)
      throw new InvalidDataException(
        $"StuffIt archive missing 'rLau' signature (found 0x{signature:X8}).");

    // Read each entry header sequentially.
    for (var i = 0; i < fileCount; ++i) {
      var entry = ReadEntryHeader();
      this._entries.Add(entry);

      // Skip past the compressed resource fork + data fork so we are positioned
      // at the next entry header.
      this._stream.Position = entry.DataForkOffset + entry.CompressedDataSize;
    }
  }

  private StuffItEntry ReadEntryHeader() {
    var hdr = ReadExact(StuffItConstants.EntryHeaderSize);

    var resourceMethod = hdr[0];
    var dataMethod     = hdr[1];
    var nameLength     = hdr[2];

    // Clamp to max 63 characters.
    if (nameLength > StuffItConstants.FileNameMaxLength)
      nameLength = StuffItConstants.FileNameMaxLength;

    // Mac Roman is not registered by default on .NET Core; Latin-1 (ISO-8859-1) is the
    // closest portable encoding and correctly round-trips all ASCII filenames.
    var fileName = Encoding.Latin1.GetString(hdr, StuffItConstants.FileNameOffset, nameLength);

    // hdr[66..70] = file type (4-char Mac code)
    var fileType    = Encoding.ASCII.GetString(hdr, 66, 4);
    // hdr[70..74] = file creator (4-char Mac code)
    var fileCreator = Encoding.ASCII.GetString(hdr, 70, 4);

    // hdr[74..76] = Finder flags (big-endian uint16) — not exposed
    // hdr[76..80] = creation date (Mac timestamp, uint32 BE) — not exposed
    var modDate = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(80, 4));

    var resourceForkUncompressed = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(84, 4));
    var dataForkUncompressed     = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(88, 4));
    var resourceForkCompressed   = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(92, 4));
    var dataForkCompressed       = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(96, 4));

    var resourceCrc16 = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(100, 2));
    var dataCrc16     = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(102, 2));

    // hdr[104..110] = reserved (6 bytes)
    // hdr[110..112] = header CRC-16 — not verified here

    var lastModified = modDate == 0
      ? DateTime.MinValue
      : StuffItConstants.MacEpoch.AddSeconds(modDate);

    var resourceDataOffset = this._stream.Position;
    var dataForkOffset     = resourceDataOffset + resourceForkCompressed;

    return new StuffItEntry {
      FileName               = fileName,
      DataMethod             = dataMethod,
      ResourceMethod         = resourceMethod,
      DataForkSize           = dataForkUncompressed,
      ResourceForkSize       = resourceForkUncompressed,
      CompressedDataSize     = dataForkCompressed,
      CompressedResourceSize = resourceForkCompressed,
      FileType               = fileType,
      FileCreator            = fileCreator,
      LastModified           = lastModified,
      ResourceDataOffset     = resourceDataOffset,
      DataForkOffset         = dataForkOffset,
      DataForkCrc16          = dataCrc16,
      ResourceForkCrc16      = resourceCrc16,
    };
  }

  // ── Decompression ─────────────────────────────────────────────────────────────

  private static byte[] Decompress(int method, byte[] compressed, int uncompressedSize) =>
    method switch {
      StuffItConstants.MethodStore => compressed,
      StuffItConstants.MethodRle   => StuffItRle.Decode(compressed),
      _                            => throw new NotSupportedException(
                                       $"StuffIt compression method {method} is not supported."),
    };

  // ── CRC-16/CCITT (forward, non-reflected, init=0) ─────────────────────────────

  private static readonly ushort[] Crc16Table = BuildCrc16Table();

  private static ushort[] BuildCrc16Table() {
    const ushort poly = StuffItConstants.Crc16Polynomial;
    var table = new ushort[256];
    for (var i = 0; i < 256; ++i) {
      var crc = (ushort)(i << 8);
      for (var j = 0; j < 8; ++j)
        crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ poly) : (ushort)(crc << 1);
      table[i] = crc;
    }
    return table;
  }

  private static ushort ComputeCrc16(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (var b in data)
      crc = (ushort)((crc << 8) ^ Crc16Table[(byte)(crc >> 8) ^ b]);
    return crc;
  }

  private static void VerifyCrc16(byte[] data, ushort expected, string fileName, string forkName) {
    if (expected == 0)
      return; // zero means no checksum stored

    var actual = ComputeCrc16(data);
    if (actual != expected)
      throw new InvalidDataException(
        $"CRC-16 mismatch for '{fileName}' ({forkName}): expected 0x{expected:X4}, computed 0x{actual:X4}.");
  }

  // ── Stream helpers ────────────────────────────────────────────────────────────

  private byte[] ReadExact(int count) {
    var buf = new byte[count];
    ReadExact(buf.AsSpan());
    return buf;
  }

  private void ReadExact(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = this._stream.Read(buffer[total..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of stream reading StuffIt data.");
      total += read;
    }
  }
}
