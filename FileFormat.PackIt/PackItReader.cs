using System.Buffers.Binary;
using System.Text;

namespace FileFormat.PackIt;

/// <summary>
/// Reads entries from a PackIt (.pit) classic Macintosh archive.
/// </summary>
/// <remarks>
/// PackIt (Harry Chesley, 1984) was one of the earliest Mac archive formats.
/// Each entry in the archive begins with a 4-byte magic word ("PMag" for stored,
/// "PMa4" for Huffman-compressed), followed by the entry header and inline file data.
///
/// Per-entry layout:
/// <code>
///   [0..4]   magic: "PMag" (stored) or "PMa4" (Huffman compressed)
///   [4..67]  filename field: 63 bytes, Pascal string (byte 4 = length, bytes 5..67 = name)
///   [67..71] Mac file type (4 ASCII bytes)
///   [71..75] Mac creator code (4 ASCII bytes)
///   [75..77] Finder flags (uint16 BE)
///   [77]     locked flag (1 byte)
///   [78]     zero padding (1 byte)
///   [79..83] data fork size (uint32 BE)
///   [83..87] resource fork size (uint32 BE)
///   [87..]   data fork bytes (data fork size bytes)
///            resource fork bytes (resource fork size bytes)
/// </code>
/// This reader supports stored ("PMag") entries with full extraction and lists
/// Huffman-compressed ("PMa4") entries for informational purposes only.
/// </remarks>
public sealed class PackItReader : IDisposable {
  /// <summary>Size of the fixed per-entry header in bytes (magic + filename field + metadata).</summary>
  public const int EntryHeaderSize = 87;

  /// <summary>Offset of the Pascal filename field within the entry header.</summary>
  private const int FileNameOffset = 4;

  /// <summary>Maximum length of the filename field in bytes (63 bytes: 1 length + 62 name).</summary>
  private const int FileNameFieldSize = 63;

  /// <summary>Offset of the Mac file type within the entry header.</summary>
  private const int FileTypeOffset = 67;

  /// <summary>Offset of the Mac creator code within the entry header.</summary>
  private const int CreatorOffset = 71;

  /// <summary>Offset of the data fork size within the entry header.</summary>
  private const int DataForkSizeOffset = 79;

  /// <summary>Offset of the resource fork size within the entry header.</summary>
  private const int ResourceForkSizeOffset = 83;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<PackItEntry> _entries = [];
  private bool _disposed;

  /// <summary>
  /// Opens a PackIt archive from the given stream and reads all entries.
  /// </summary>
  /// <param name="stream">A readable and seekable stream containing a .pit archive.</param>
  /// <param name="leaveOpen">
  /// <see langword="true"/> to leave <paramref name="stream"/> open after this reader is disposed.
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  public PackItReader(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this.ParseArchive();
  }

  /// <summary>Gets the list of entries found in the archive.</summary>
  public IReadOnlyList<PackItEntry> Entries => this._entries;

  /// <summary>
  /// Extracts the data fork of the specified entry.
  /// </summary>
  /// <param name="entry">An entry obtained from <see cref="Entries"/>.</param>
  /// <returns>
  /// The raw data fork bytes. For stored entries the bytes are the original file data.
  /// For Huffman-compressed entries the raw compressed bytes are returned.
  /// </returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
  /// <exception cref="ObjectDisposedException">Thrown when this reader has been disposed.</exception>
  public byte[] Extract(PackItEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (entry.DataForkSize == 0)
      return [];

    this._stream.Position = entry.DataOffset;
    return this.ReadExact((int)entry.DataForkSize);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  // ── Parsing ───────────────────────────────────────────────────────────────────

  private void ParseArchive() {
    Span<byte> magicBuf = stackalloc byte[4];

    while (this._stream.Position < this._stream.Length) {
      // Try to read 4-byte magic. If fewer than 4 bytes remain, stop.
      var remaining = (int)(this._stream.Length - this._stream.Position);
      if (remaining < EntryHeaderSize)
        break;

      this.ReadExact(magicBuf);

      var isStored     = magicBuf.SequenceEqual(PackItConstants.MagicStored);
      var isCompressed = !isStored && magicBuf.SequenceEqual(PackItConstants.MagicCompressed);

      if (!isStored && !isCompressed)
        break; // no more recognized entries

      // Read the rest of the entry header (EntryHeaderSize - 4 bytes already consumed).
      var hdrRemainder = this.ReadExact(EntryHeaderSize - 4);

      // Filename: Pascal string at hdrRemainder[0..63] (relative to hdrRemainder).
      // hdrRemainder[0] = name length, [1..62] = name bytes
      var nameLength = hdrRemainder[0];
      if (nameLength > FileNameFieldSize - 1)
        nameLength = (byte)(FileNameFieldSize - 1);
      var name = nameLength > 0
        ? Encoding.Latin1.GetString(hdrRemainder, 1, nameLength)
        : string.Empty;

      // File type and creator are 4 ASCII bytes each.
      // Offsets within hdrRemainder = (absolute offset) - 4 (magic already consumed).
      var fileTypeOff = FileTypeOffset  - 4; // 63
      var creatorOff  = CreatorOffset   - 4; // 67
      var dataOff     = DataForkSizeOffset     - 4; // 75
      var rsrcOff     = ResourceForkSizeOffset - 4; // 79

      var fileType = Encoding.ASCII.GetString(hdrRemainder, fileTypeOff, 4);
      var creator  = Encoding.ASCII.GetString(hdrRemainder, creatorOff,  4);

      var dataForkSize = BinaryPrimitives.ReadUInt32BigEndian(hdrRemainder.AsSpan(dataOff, 4));
      var rsrcForkSize = BinaryPrimitives.ReadUInt32BigEndian(hdrRemainder.AsSpan(rsrcOff, 4));

      // Data fork bytes start immediately after the header.
      var dataForkStart = this._stream.Position;

      this._entries.Add(new PackItEntry {
        Name             = name,
        FileType         = fileType,
        Creator          = creator,
        DataForkSize     = dataForkSize,
        ResourceForkSize = rsrcForkSize,
        IsCompressed     = isCompressed,
        DataOffset       = dataForkStart,
      });

      // Skip past data fork + resource fork to reach the next entry.
      this._stream.Position = dataForkStart + dataForkSize + rsrcForkSize;
    }
  }

  // ── Stream helpers ────────────────────────────────────────────────────────────

  private byte[] ReadExact(int count) {
    var buf = new byte[count];
    this.ReadExact(buf.AsSpan());
    return buf;
  }

  private void ReadExact(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = this._stream.Read(buffer[total..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of stream reading PackIt data.");
      total += read;
    }
  }
}
