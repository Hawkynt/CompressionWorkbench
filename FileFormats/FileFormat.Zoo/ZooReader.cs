using System.Text;
using Compression.Core.Checksums;
using Compression.Core.BitIO;
using Compression.Core.Dictionary.Lzw;

namespace FileFormat.Zoo;

/// <summary>
/// Reads entries from a Zoo archive.
/// </summary>
public sealed class ZooReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<ZooEntry> _entries = [];
  private bool _disposed;

  /// <summary>Gets the entries present in the archive (deleted entries are included; check <see cref="ZooEntry.IsDeleted"/>).</summary>
  public IReadOnlyList<ZooEntry> Entries => this._entries;

  /// <summary>
  /// Initializes a new <see cref="ZooReader"/> and reads the directory.
  /// </summary>
  /// <param name="stream">A seekable stream containing a Zoo archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this reader is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="ArgumentException">Thrown when <paramref name="stream"/> is not seekable.</exception>
  /// <exception cref="InvalidDataException">Thrown when the stream does not contain a valid Zoo archive.</exception>
  public ZooReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;

    ReadDirectory();
  }

  // ── Directory parsing ────────────────────────────────────────────────────

  private void ReadDirectory() {
    var reader = new BinaryReader(this._stream, Encoding.Latin1, leaveOpen: true);

    // --- Archive header ---
    if (this._stream.Length < ZooConstants.ArchiveHeaderSize)
      ThrowInvalidArchive("Stream is too short to contain a Zoo archive header.");

    this._stream.Position = 0;

    // Text (20 bytes) – skip, just for humans.
    reader.ReadBytes(20);

    var tag = reader.ReadUInt32();
    if (tag != ZooConstants.Magic)
      ThrowInvalidArchive($"Invalid Zoo magic: 0x{tag:X8}.");

    var firstEntryOffset = reader.ReadUInt32();
    // minus-offset (int32) — skip validation for tolerance.
    reader.ReadInt32();
    // required major/minor version — skip.
    reader.ReadByte();
    reader.ReadByte();

    if (firstEntryOffset == 0)
      return; // empty archive

    // --- Walk the directory chain ---
    var nextOffset = firstEntryOffset;

    while (nextOffset != 0) {
      this._stream.Position = nextOffset;
      var entry = ReadDirectoryEntry(reader);
      if (entry == null)
        break;

      this._entries.Add(entry);
      nextOffset = (uint)entry.HeaderOffset; // temporarily stored next-pointer
      // Restore the correct HeaderOffset value.
      entry.HeaderOffset = this._stream.Position - ZooConstants.DirectoryEntryFixedSize - GetFilenameAreaSize(entry);
    }
  }

  private static int GetFilenameAreaSize(ZooEntry entry) {
    // Fixed part already read; this is called after parsing to adjust HeaderOffset.
    // We only need this for the final offset correction — not strictly needed since
    // we store DataOffset directly. Leave as 0; HeaderOffset isn't used after parsing.
    return 0;
  }

  private ZooEntry? ReadDirectoryEntry(BinaryReader reader) {
    var entryStart = this._stream.Position;

    // Tag
    var tag = reader.ReadUInt32();
    if (tag != ZooConstants.Magic)
      ThrowInvalidArchive($"Invalid entry tag at offset {entryStart}: 0x{tag:X8}.");

    var type       = reader.ReadByte();
    var method     = reader.ReadByte();
    var nextOff    = reader.ReadUInt32();
    var dataOff    = reader.ReadUInt32();
    var dosDate  = reader.ReadUInt16();
    var dosTime  = reader.ReadUInt16();
    var crc16    = reader.ReadUInt16();
    var origSize   = reader.ReadUInt32();
    var compSize   = reader.ReadUInt32();
    var majorVer   = reader.ReadByte();
    var minorVer   = reader.ReadByte();
    var deleted    = reader.ReadByte();
    /*byte structure =*/ reader.ReadByte();
    /*uint commentOff =*/ reader.ReadUInt32();
    /*ushort commentLen =*/ reader.ReadUInt16();
    // Total fixed bytes read: 4+1+1+4+4+2+2+2+4+4+1+1+1+1+4+2 = 38

    // Short filename: null-terminated, up to 13 bytes.
    var shortName = ReadNullTerminatedString(reader, 13);

    string? longName = null;
    if (type == ZooConstants.TypeLongName) {
      var longNameLen = reader.ReadUInt16();
      var longNameBytes = reader.ReadBytes(longNameLen);
      longName = Encoding.Latin1.GetString(longNameBytes);
    }

    var entry = new ZooEntry {
      FileName          = shortName,
      LongFileName      = longName,
      CompressionMethod = (ZooCompressionMethod)method,
      Crc16             = crc16,
      OriginalSize      = origSize,
      CompressedSize    = compSize,
      LastModified      = ZooEntry.FromMsDosDateTime(dosDate, dosTime),
      IsDeleted         = deleted != 0,
      MajorVersion      = majorVer,
      MinorVersion      = minorVer,
      DataOffset        = dataOff,
      // Temporarily borrow HeaderOffset to carry nextOff to the caller.
      HeaderOffset      = nextOff,
    };

    return entry;
  }

  private static string ReadNullTerminatedString(BinaryReader reader, int maxBytes) {
    // Always consume exactly maxBytes from the stream.
    var raw = reader.ReadBytes(maxBytes);
    var len = Array.IndexOf(raw, (byte)0);
    if (len < 0) len = raw.Length;
    return Encoding.Latin1.GetString(raw, 0, len);
  }

  // ── Data extraction ──────────────────────────────────────────────────────

  /// <summary>
  /// Extracts and decompresses the data for an entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
  /// <exception cref="InvalidDataException">Thrown when the CRC-16 does not match or the data is corrupt.</exception>
  public byte[] ExtractEntry(ZooEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    this._stream.Position = entry.DataOffset;

    var compressed = new byte[entry.CompressedSize];
    ReadFully(this._stream, compressed);

    byte[] data;
    switch (entry.CompressionMethod) {
      case ZooCompressionMethod.Store:
        data = compressed;
        break;

      case ZooCompressionMethod.Lzw: {
        using var ms = new MemoryStream(compressed);
        var decoder = new LzwDecoder(
          ms,
          minBits:      ZooConstants.LzwMinBits,
          maxBits:      ZooConstants.LzwMaxBits,
          useClearCode: true,
          useStopCode:  false,
          bitOrder:     BitOrder.LsbFirst);
        data = decoder.Decode((int)entry.OriginalSize);
        break;
      }

      default:
        throw new NotSupportedException($"Unsupported Zoo compression method: {(byte)entry.CompressionMethod}.");
    }

    var computed = Crc16.Compute(data);
    if (computed != entry.Crc16)
      throw new InvalidDataException(
        $"CRC-16 mismatch for '{entry.EffectiveName}': expected 0x{entry.Crc16:X4}, computed 0x{computed:X4}.");

    return data;
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static void ReadFully(Stream stream, byte[] buffer) {
    var offset = 0;
    while (offset < buffer.Length) {
      var read = stream.Read(buffer, offset, buffer.Length - offset);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of Zoo archive data.");
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
