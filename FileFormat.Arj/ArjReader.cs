using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Arj;

namespace FileFormat.Arj;

/// <summary>
/// Reads entries from an ARJ archive stream.
/// Supports extraction of stored entries (methods 0 and 4).
/// Compressed methods (1–3) are detected but extraction is not supported.
/// </summary>
public sealed class ArjReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly string? _password;
  private readonly List<ArjEntry> _entries = [];
  private bool _disposed;

  /// <summary>
  /// Gets the list of entries found in the archive (both files and directories, excluding
  /// the main archive comment header).
  /// </summary>
  public IReadOnlyList<ArjEntry> Entries => this._entries;

  /// <summary>
  /// Initializes a new <see cref="ArjReader"/> and reads the archive index.
  /// </summary>
  /// <param name="stream">A readable, seekable stream containing the ARJ archive.</param>
  /// <param name="leaveOpen">
  /// <see langword="true"/> to leave the stream open when this instance is disposed.
  /// </param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="stream"/> is <see langword="null"/>.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the stream does not contain a valid ARJ archive.
  /// </exception>
  public ArjReader(Stream stream, bool leaveOpen = false)
    : this(stream, password: null, leaveOpen) {
  }

  /// <summary>
  /// Opens an ARJ archive with an optional password for garbled (encrypted) entries.
  /// </summary>
  /// <param name="stream">A readable, seekable stream containing the ARJ archive.</param>
  /// <param name="password">The password for decrypting garbled entries, or null if not encrypted.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when disposed.</param>
  public ArjReader(Stream stream, string? password, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._password = string.IsNullOrEmpty(password) ? null : password;
    this.ReadArchive();
  }

  /// <summary>
  /// Extracts the data for the specified entry and verifies its CRC-32.
  /// </summary>
  /// <param name="entry">The entry to extract. Must belong to this archive.</param>
  /// <returns>The uncompressed file data.</returns>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="entry"/> is <see langword="null"/>.
  /// </exception>
  /// <exception cref="NotSupportedException">
  /// Thrown when the entry uses a compressed method (1–3) that is not supported.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the CRC-32 of the extracted data does not match the stored value.
  /// </exception>
  public byte[] ExtractEntry(ArjEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    this._stream.Position = entry.DataOffset;

    byte[] rawData = new byte[entry.CompressedSize];
    if (entry.CompressedSize > 0)
      ReadExact(this._stream, rawData);

    // Degarble if the entry is encrypted
    bool isGarbled = (entry.Flags & ArjConstants.FlagGarbled) != 0;
    if (isGarbled) {
      if (this._password == null)
        throw new InvalidOperationException(
          $"Entry '{entry.FileName}' is garbled (encrypted) but no password was provided.");
      rawData = ArjWriter.ApplyGarble(rawData, this._password);
    }

    byte[] data;
    switch (entry.Method) {
      case ArjConstants.MethodStore:
      case ArjConstants.MethodStoreFast:
        data = rawData;
        break;
      case ArjConstants.MethodCompressed1:
      case ArjConstants.MethodCompressed2:
      case ArjConstants.MethodCompressed3: {
        using var ms = new MemoryStream(rawData);
        var decoder = new ArjDecoder(ms, entry.Method);
        data = decoder.Decode((int)entry.OriginalSize);
        break;
      }
      default:
        throw new NotSupportedException(
          $"ARJ compression method {entry.Method} is not supported.");
    }

    uint actual = Crc32.Compute(data);
    if (actual != entry.Crc32)
      throw new InvalidDataException(
        $"CRC-32 mismatch for '{entry.FileName}': " +
        $"expected 0x{entry.Crc32:X8}, computed 0x{actual:X8}.");

    return data;
  }

  // -------------------------------------------------------------------------
  // Archive parsing
  // -------------------------------------------------------------------------

  private void ReadArchive() {
    // The first header is the main archive header (file type = comment/archive header).
    var mainHeader = this.ReadOneHeader();
    if (mainHeader == null)
      throw new InvalidDataException("Stream does not contain a valid ARJ archive header.");

    // Position past the main archive header's data block (always zero length).
    this._stream.Position = mainHeader.DataOffset + mainHeader.CompressedSize;

    // Read file headers until the end-of-archive marker.
    while (true) {
      var entry = this.ReadOneHeader();
      if (entry == null)
        break; // end-of-archive

      // Collect all entries (files and directories), but skip comment-only headers.
      if (entry.FileType != ArjConstants.FileTypeComment)
        this._entries.Add(entry);

      // Advance past this entry's data block.
      this._stream.Position = entry.DataOffset + entry.CompressedSize;
    }
  }

  /// <summary>
  /// Reads one ARJ header from the current stream position.
  /// Returns <see langword="null"/> on an end-of-archive marker or at end of stream.
  /// </summary>
  private ArjEntry? ReadOneHeader() {
    if (this._stream.Position >= this._stream.Length - 1)
      return null;

    // --- Header ID (2 bytes, LE) ---
    int lo = this._stream.ReadByte();
    int hi = this._stream.ReadByte();
    if (lo < 0 || hi < 0)
      return null;

    ushort headerId = (ushort)(lo | (hi << 8));
    if (headerId != ArjConstants.HeaderId)
      throw new InvalidDataException(
        $"Invalid ARJ header ID 0x{headerId:X4} at stream offset {this._stream.Position - 2}.");

    // --- Basic header size (2 bytes, LE) ---
    int szLo = this._stream.ReadByte();
    int szHi = this._stream.ReadByte();
    if (szLo < 0 || szHi < 0)
      throw new EndOfStreamException("Unexpected end of stream reading ARJ basic header size.");

    ushort basicHeaderSize = (ushort)(szLo | (szHi << 8));

    // A size of 0 is the end-of-archive marker.
    if (basicHeaderSize == 0)
      return null;

    if (basicHeaderSize > 2600)
      throw new InvalidDataException(
        $"ARJ basic header size {basicHeaderSize} is unreasonably large.");

    // Read the header body (basicHeaderSize bytes).
    var headerBytes = new byte[basicHeaderSize];
    ReadExact(this._stream, headerBytes);

    // --- CRC-32 of the header body (4 bytes, LE) ---
    using var binReader = new BinaryReader(this._stream, Encoding.UTF8, leaveOpen: true);
    uint storedHeaderCrc = binReader.ReadUInt32();

    uint computedHeaderCrc = Crc32.Compute(headerBytes);
    if (computedHeaderCrc != storedHeaderCrc)
      throw new InvalidDataException(
        $"ARJ header CRC-32 mismatch: expected 0x{storedHeaderCrc:X8}, " +
        $"computed 0x{computedHeaderCrc:X8}.");

    // --- Extended headers (skip) ---
    while (true) {
      ushort extSize = binReader.ReadUInt16();
      if (extSize == 0)
        break;
      // extSize is the count of bytes in the extended header body (not counting the size field).
      // Each extended header is followed by a 4-byte CRC.
      this._stream.Seek(extSize + 4, SeekOrigin.Current);
    }

    // The data for this entry starts at the current stream position.
    long dataOffset = this._stream.Position;

    // Parse the header body into an ArjEntry.
    var entry = ParseHeaderBody(headerBytes);
    entry.DataOffset = dataOffset;
    return entry;
  }

  // -------------------------------------------------------------------------
  // Header body parsing
  // -------------------------------------------------------------------------

  private static ArjEntry ParseHeaderBody(ReadOnlySpan<byte> h) {
    if (h.Length < ArjConstants.FirstHeaderMinSize)
      throw new InvalidDataException(
        $"ARJ header body is too short ({h.Length} bytes; " +
        $"minimum {ArjConstants.FirstHeaderMinSize}).");

    // Byte layout of the header body (starts at what the spec calls "byte 4"):
    //   [0]      firstHeaderSize  — size of the fixed portion (up to but not including filename)
    //   [1]      archiverVersion
    //   [2]      minVersionToExtract
    //   [3]      hostOs
    //   [4]      arjFlags
    //   [5]      method
    //   [6]      fileType
    //   [7]      reserved
    //   [8..11]  msdosTimestamp (uint32 LE)
    //   [12..15] compressedSize (uint32 LE)
    //   [16..19] originalSize   (uint32 LE)
    //   [20..23] crc32           (uint32 LE)
    //   [24..25] filespacePos   (uint16 LE)
    //   [26..27] fileMode       (uint16 LE)
    //   [28..29] hostData       (2 bytes)
    //   [firstHeaderSize..] null-terminated filename
    //   [after filename]    null-terminated comment

    byte firstHeaderSize = h[0];
    byte hostOs = h[3];
    byte arjFlags = h[4];
    byte method = h[5];
    byte fileType = h[6];
    uint msdosTimestamp = ReadUInt32Le(h, 8);
    uint compressedSize = ReadUInt32Le(h, 12);
    uint originalSize = ReadUInt32Le(h, 16);
    uint crc32 = ReadUInt32Le(h, 20);
    ushort fileMode = ReadUInt16Le(h, 26);

    // Strings follow immediately after the fixed portion.
    int pos = firstHeaderSize;
    string fileName = ReadNullTerminatedString(h, pos, out pos);
    string comment = ReadNullTerminatedString(h, pos, out _);

    return new ArjEntry {
      FileName = fileName,
      Comment = comment,
      HostOs = hostOs,
      Flags = arjFlags,
      Method = method,
      FileType = fileType,
      MsdosTimestamp = msdosTimestamp,
      CompressedSize = compressedSize,
      OriginalSize = originalSize,
      Crc32 = crc32,
      FileMode = fileMode,
    };
  }

  // -------------------------------------------------------------------------
  // Low-level helpers
  // -------------------------------------------------------------------------

  private static string ReadNullTerminatedString(ReadOnlySpan<byte> data, int offset, out int endOffset) {
    if (offset >= data.Length) {
      endOffset = offset;
      return string.Empty;
    }

    int start = offset;
    while (offset < data.Length && data[offset] != 0)
      ++offset;

    string result = Encoding.ASCII.GetString(data[start..offset]);
    endOffset = Math.Min(offset + 1, data.Length); // step past null terminator
    return result;
  }

  private static uint ReadUInt32Le(ReadOnlySpan<byte> data, int offset) =>
    (uint)(data[offset]
      | (data[offset + 1] << 8)
      | (data[offset + 2] << 16)
      | (data[offset + 3] << 24));

  private static ushort ReadUInt16Le(ReadOnlySpan<byte> data, int offset) =>
    (ushort)(data[offset] | (data[offset + 1] << 8));

  private static void ReadExact(Stream stream, byte[] buffer) {
    int total = 0;
    while (total < buffer.Length) {
      int read = stream.Read(buffer, total, buffer.Length - total);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of ARJ archive stream.");
      total += read;
    }
  }

  // -------------------------------------------------------------------------
  // IDisposable
  // -------------------------------------------------------------------------

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
