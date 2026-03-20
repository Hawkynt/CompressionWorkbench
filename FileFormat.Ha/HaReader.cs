using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Ha;

/// <summary>
/// Reads entries from an Ha archive.
/// </summary>
public sealed class HaReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<HaEntry> _entries = [];
  private bool _disposed;

  /// <summary>Gets the entries present in the archive.</summary>
  public IReadOnlyList<HaEntry> Entries => this._entries;

  /// <summary>
  /// Initializes a new <see cref="HaReader"/> and parses the archive directory.
  /// </summary>
  /// <param name="stream">A seekable stream containing an Ha archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this reader is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="ArgumentException">Thrown when <paramref name="stream"/> is not seekable.</exception>
  /// <exception cref="InvalidDataException">Thrown when the stream does not contain a valid Ha archive.</exception>
  public HaReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;

    ReadEntries();
  }

  // ── Parsing ──────────────────────────────────────────────────────────────

  private void ReadEntries() {
    this._stream.Position = 0;
    var reader = new BinaryReader(this._stream, Encoding.Latin1, leaveOpen: true);

    // Verify magic "HA".
    if (this._stream.Length < 2)
      ThrowInvalidArchive("Stream is too short to be an Ha archive.");

    byte m0 = reader.ReadByte();
    byte m1 = reader.ReadByte();
    if (m0 != HaConstants.Magic[0] || m1 != HaConstants.Magic[1])
      ThrowInvalidArchive($"Invalid Ha magic: expected 0x48 0x41, got 0x{m0:X2} 0x{m1:X2}.");

    // Read entries sequentially until EOF.
    while (this._stream.Position < this._stream.Length) {
      var entry = TryReadEntry(reader);
      if (entry == null)
        break;
      this._entries.Add(entry);
    }
  }

  private HaEntry? TryReadEntry(BinaryReader reader) {
    // Need at least 1 byte for the method byte.
    if (this._stream.Position >= this._stream.Length)
      return null;

    // Version + method byte: high nibble = version (should be 0), low nibble = method.
    byte versionMethod = reader.ReadByte();
    int method = versionMethod & 0x0F;

    uint compressedSize = reader.ReadUInt32();
    uint originalSize   = reader.ReadUInt32();
    uint crc32          = reader.ReadUInt32();
    uint dosDateTime    = reader.ReadUInt32();

    string fileName = ReadNullTerminatedString(reader);

    long dataOffset = this._stream.Position;

    // Skip compressed data to position at the next entry.
    this._stream.Seek(compressedSize, SeekOrigin.Current);

    return new HaEntry {
      FileName       = fileName,
      Method         = method,
      OriginalSize   = originalSize,
      CompressedSize = compressedSize,
      Crc32          = crc32,
      LastModified   = HaEntry.DecodeMsDosDateTime(dosDateTime),
      DataOffset     = dataOffset,
    };
  }

  private static string ReadNullTerminatedString(BinaryReader reader) {
    var sb = new StringBuilder();
    while (true) {
      byte b = reader.ReadByte();
      if (b == 0)
        break;
      sb.Append((char)b);
    }
    return sb.ToString();
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
  public byte[] Extract(HaEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    this._stream.Position = entry.DataOffset;
    byte[] compressed = new byte[entry.CompressedSize];
    ReadFully(this._stream, compressed);

    byte[] data;
    switch (entry.Method) {
      case HaConstants.MethodStore:
      case HaConstants.MethodDirectory:
        data = compressed;
        break;

      case HaConstants.MethodHsc:
        throw new NotSupportedException("HSC (method 1) decompression is not supported.");

      case HaConstants.MethodAsc:
        throw new NotSupportedException("ASC (method 2) decompression is not supported.");

      default:
        throw new NotSupportedException($"Unsupported Ha compression method: {entry.Method}.");
    }

    // Directory entries have no data to check.
    if (entry.Method != HaConstants.MethodDirectory && data.Length > 0) {
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
        throw new EndOfStreamException("Unexpected end of Ha archive data.");
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
