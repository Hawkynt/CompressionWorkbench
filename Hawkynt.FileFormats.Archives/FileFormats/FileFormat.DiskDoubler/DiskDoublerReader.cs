using System.Buffers.Binary;
using System.Text;

namespace FileFormat.DiskDoubler;

/// <summary>
/// Reads the header of a DiskDoubler compressed file and provides access to
/// its data and resource fork entries.
/// </summary>
/// <remarks>
/// DiskDoubler (Salient Software, 1989-1993) compressed individual files using a
/// proprietary 82-byte header followed by the compressed data fork and an optional
/// compressed resource fork. This reader reconstructs the entry list from the header
/// and supports extraction of stored (method 0) entries; for all other compression
/// methods the raw compressed bytes are returned unchanged.
///
/// Header layout (82 bytes):
/// <code>
///   [0..4]   format version identifier (uint32 BE)
///   [4..8]   Mac file type code (4 ASCII bytes)
///   [8..12]  Mac creator code (4 ASCII bytes)
///   [12..14] Finder flags (uint16 BE)
///   [14..16] padding / reserved
///   [16..20] data fork original size (uint32 BE)
///   [20..24] data fork compressed size (uint32 BE)
///   [24..28] resource fork original size (uint32 BE)
///   [28..32] resource fork compressed size (uint32 BE)
///   [32]     data fork compression method
///   [33]     resource fork compression method
///   [34..82] filename (Pascal string: byte 34 = length, bytes 35..82 = name)
/// </code>
/// Compressed data follows immediately after the 82-byte header.
/// </remarks>
public sealed class DiskDoublerReader : IDisposable {
  /// <summary>Size of the fixed-length DiskDoubler file header in bytes.</summary>
  public const int HeaderSize = 82;

  /// <summary>Byte offset of the filename Pascal string within the header.</summary>
  private const int FileNameOffset = 34;

  /// <summary>Maximum bytes available for the filename (Pascal length byte + 47 name bytes).</summary>
  private const int FileNameFieldLength = 48;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<DiskDoublerEntry> _entries = [];
  private bool _disposed;

  /// <summary>
  /// Opens a DiskDoubler compressed file from the given stream and parses its header.
  /// </summary>
  /// <param name="stream">A readable and seekable stream containing a DiskDoubler file.</param>
  /// <param name="leaveOpen">
  /// <see langword="true"/> to leave <paramref name="stream"/> open after this reader is disposed.
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="InvalidDataException">Thrown when the stream is too short to contain a valid header.</exception>
  public DiskDoublerReader(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this.ParseHeader();
  }

  /// <summary>Gets the list of entries parsed from the DiskDoubler file header.</summary>
  /// <remarks>
  /// A DiskDoubler file contains at most two entries: one for the data fork and one for the
  /// resource fork. The resource fork entry is omitted when the resource fork is empty.
  /// </remarks>
  public IReadOnlyList<DiskDoublerEntry> Entries => this._entries;

  /// <summary>
  /// Extracts the data for the specified entry.
  /// </summary>
  /// <param name="entry">An entry obtained from <see cref="Entries"/>.</param>
  /// <returns>
  /// The uncompressed bytes for method 0 (stored), or the raw compressed bytes for all
  /// other methods.
  /// </returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
  /// <exception cref="ObjectDisposedException">Thrown when this reader has been disposed.</exception>
  public byte[] Extract(DiskDoublerEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (entry.CompressedSize == 0)
      return [];

    this._stream.Position = entry.DataOffset;
    var compressed = this.ReadExact((int)entry.CompressedSize);

    return entry.Method == DiskDoublerConstants.MethodStored
      ? compressed
      : compressed; // raw compressed bytes for unsupported methods
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

  private void ParseHeader() {
    if (this._stream.Length - this._stream.Position < HeaderSize)
      throw new InvalidDataException(
        $"Stream is too short to contain a DiskDoubler header (need {HeaderSize} bytes).");

    var hdr = this.ReadExact(HeaderSize);

    // [4..8]  Mac file type  — informational only
    // [8..12] Mac creator    — informational only
    // [16..20] data fork original size
    var dataOrigSize        = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(16, 4));
    // [20..24] data fork compressed size
    var dataCompSize        = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(20, 4));
    // [24..28] resource fork original size
    var rsrcOrigSize        = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(24, 4));
    // [28..32] resource fork compressed size
    var rsrcCompSize        = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(28, 4));
    // [32] data fork method, [33] resource fork method
    var dataMethod          = hdr[32];
    var rsrcMethod          = hdr[33];

    // [34] Pascal string length, [35..81] name bytes
    var nameLength = hdr[FileNameOffset];
    if (nameLength > FileNameFieldLength - 1)
      nameLength = (byte)(FileNameFieldLength - 1);
    var name = nameLength > 0
      ? Encoding.Latin1.GetString(hdr, FileNameOffset + 1, nameLength)
      : string.Empty;

    // Compressed data follows the header immediately.
    var dataStart = this._stream.Position; // = HeaderSize from stream start

    if (dataOrigSize > 0 || dataCompSize > 0) {
      this._entries.Add(new DiskDoublerEntry {
        Name           = name,
        Method         = dataMethod,
        OriginalSize   = dataOrigSize,
        CompressedSize = dataCompSize,
        DataOffset     = dataStart,
        IsDataFork     = true,
      });
    }

    if (rsrcOrigSize > 0 || rsrcCompSize > 0) {
      this._entries.Add(new DiskDoublerEntry {
        Name           = name + ".rsrc",
        Method         = rsrcMethod,
        OriginalSize   = rsrcOrigSize,
        CompressedSize = rsrcCompSize,
        DataOffset     = dataStart + dataCompSize,
        IsDataFork     = false,
      });
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
        throw new EndOfStreamException("Unexpected end of stream reading DiskDoubler data.");
      total += read;
    }
  }
}
