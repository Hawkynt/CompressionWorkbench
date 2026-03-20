using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzp;

namespace FileFormat.Uharc;

/// <summary>
/// Reads entries from a UHARC archive.
/// </summary>
public sealed class UharcReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<UharcEntry> _entries = [];
  private bool _disposed;

  /// <summary>Gets the entries present in the archive.</summary>
  public IReadOnlyList<UharcEntry> Entries => this._entries;

  /// <summary>
  /// Initializes a new <see cref="UharcReader"/> and parses the archive directory.
  /// </summary>
  /// <param name="stream">A seekable stream containing a UHARC archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this reader is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="ArgumentException">Thrown when <paramref name="stream"/> is not seekable.</exception>
  /// <exception cref="InvalidDataException">Thrown when the stream does not contain a valid UHARC archive.</exception>
  public UharcReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;

    ReadArchive();
  }

  // ── Parsing ──────────────────────────────────────────────────────────────

  private void ReadArchive() {
    this._stream.Position = 0;
    var reader = new BinaryReader(this._stream, Encoding.UTF8, leaveOpen: true);

    // Verify magic "UHA".
    if (this._stream.Length < UharcConstants.HeaderSize)
      ThrowInvalidArchive("Stream is too short to be a UHARC archive.");

    byte m0 = reader.ReadByte();
    byte m1 = reader.ReadByte();
    byte m2 = reader.ReadByte();
    if (m0 != UharcConstants.Magic[0] || m1 != UharcConstants.Magic[1] || m2 != UharcConstants.Magic[2])
      ThrowInvalidArchive($"Invalid UHARC magic: expected 0x55 0x48 0x41, got 0x{m0:X2} 0x{m1:X2} 0x{m2:X2}.");

    // Version byte.
    byte version = reader.ReadByte();
    if (version > UharcConstants.Version)
      ThrowInvalidArchive($"Unsupported UHARC version: {version} (max supported: {UharcConstants.Version}).");

    // 3 flag bytes (reserved, skip).
    reader.ReadByte();
    reader.ReadByte();
    reader.ReadByte();

    // Read entries sequentially until EOF.
    while (this._stream.Position < this._stream.Length) {
      var entry = TryReadEntry(reader);
      if (entry == null)
        break;
      this._entries.Add(entry);
    }
  }

  private UharcEntry? TryReadEntry(BinaryReader reader) {
    if (this._stream.Position >= this._stream.Length)
      return null;

    // Entry header:
    //   1 byte:  method
    //   4 bytes: original size (LE)
    //   4 bytes: compressed size (LE)
    //   4 bytes: CRC-32 (LE)
    //   4 bytes: modification time — Unix timestamp (LE)
    //   2 bytes: filename length (LE)
    //   N bytes: filename (UTF-8)
    //   1 byte:  attributes (bit 0 = directory)

    long remaining = this._stream.Length - this._stream.Position;
    if (remaining < 20) // minimum entry header without filename
      return null;

    byte method = reader.ReadByte();
    uint originalSize = reader.ReadUInt32();
    uint compressedSize = reader.ReadUInt32();
    uint crc32 = reader.ReadUInt32();
    uint timestamp = reader.ReadUInt32();
    ushort nameLength = reader.ReadUInt16();

    byte[] nameBytes = reader.ReadBytes(nameLength);
    string fileName = Encoding.UTF8.GetString(nameBytes);

    byte attributes = reader.ReadByte();
    bool isDirectory = (attributes & 0x01) != 0;

    long dataOffset = this._stream.Position;

    // Skip compressed data to position at the next entry.
    this._stream.Seek(compressedSize, SeekOrigin.Current);

    return new UharcEntry {
      FileName = fileName,
      OriginalSize = originalSize,
      CompressedSize = compressedSize,
      Method = method,
      Crc32 = crc32,
      LastModified = UharcEntry.DecodeUnixTimestamp(timestamp),
      IsDirectory = isDirectory,
      DataOffset = dataOffset,
    };
  }

  // ── Extraction ───────────────────────────────────────────────────────────

  /// <summary>
  /// Extracts and decompresses the data for the given entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
  /// <exception cref="NotSupportedException">Thrown when the entry uses an unsupported compression method.</exception>
  /// <exception cref="InvalidDataException">Thrown when the CRC-32 does not match.</exception>
  public byte[] Extract(UharcEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.IsDirectory)
      return [];

    this._stream.Position = entry.DataOffset;
    byte[] compressed = new byte[entry.CompressedSize];
    ReadFully(this._stream, compressed);

    byte[] data = entry.Method switch {
      UharcConstants.MethodStore => compressed,
      UharcConstants.MethodLzp => LzpDecompressor.Decompress(compressed),
      _ => throw new NotSupportedException($"Unsupported UHARC compression method: {entry.Method}."),
    };

    // Verify CRC-32.
    if (data.Length > 0) {
      uint computed = Crc32.Compute(data);
      if (computed != entry.Crc32)
        throw new InvalidDataException(
          $"CRC-32 mismatch for '{entry.FileName}': expected 0x{entry.Crc32:X8}, computed 0x{computed:X8}.");
    }

    return data;
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static void ReadFully(Stream stream, byte[] buffer) {
    int offset = 0;
    while (offset < buffer.Length) {
      int read = stream.Read(buffer, offset, buffer.Length - offset);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of UHARC archive data.");
      offset += read;
    }
  }

  private static void ThrowInvalidArchive(string message) =>
    throw new InvalidDataException(message);

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
