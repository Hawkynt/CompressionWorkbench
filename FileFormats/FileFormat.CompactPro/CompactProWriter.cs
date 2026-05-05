using System.Buffers.Binary;
using System.Text;

namespace FileFormat.CompactPro;

/// <summary>
/// Creates a Compact Pro (.cpt) archive.
/// </summary>
/// <remarks>
/// Produces archives compatible with the classic Compact Pro format (Bill Goodman, 1990-1998).
/// All entries are stored using method 0 (Store). Entries are buffered in memory and the
/// archive is written on <see cref="Dispose"/>.
/// </remarks>
public sealed class CompactProWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<PendingItem> _items = [];
  private readonly Stack<int> _folderStarts = [];
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="CompactProWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the .cpt archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this writer is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  public CompactProWriter(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  // ── Public API ─────────────────────────────────────────────────────────────

  /// <summary>
  /// Adds a file entry to the archive.
  /// </summary>
  /// <param name="name">The filename (up to 63 characters).</param>
  /// <param name="data">The uncompressed data fork bytes.</param>
  /// <param name="resourceFork">The optional uncompressed resource fork bytes.</param>
  /// <param name="modified">The modification timestamp. Defaults to <see cref="DateTime.UtcNow"/>.</param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="name"/> or <paramref name="data"/> is null.
  /// </exception>
  public void AddFile(string name, byte[] data, byte[]? resourceFork = null, DateTime? modified = null) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var resFork = resourceFork ?? [];
    var mod = modified ?? DateTime.UtcNow;

    this._items.Add(new PendingItem {
      Type          = PendingItemType.File,
      FileName      = name,
      DataFork      = data,
      ResourceFork  = resFork,
      ModifiedDate  = mod,
      CreatedDate   = mod,
    });
  }

  /// <summary>
  /// Begins a new directory in the archive. Must be paired with <see cref="EndDirectory"/>.
  /// </summary>
  /// <param name="name">The directory name (up to 63 characters).</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
  public void AddDirectory(string name) {
    ArgumentNullException.ThrowIfNull(name);
    this._folderStarts.Push(this._items.Count);
    this._items.Add(new PendingItem {
      Type     = PendingItemType.FolderStart,
      FileName = name,
    });
  }

  /// <summary>
  /// Ends the current directory. Must be paired with a prior <see cref="AddDirectory"/> call.
  /// </summary>
  /// <exception cref="InvalidOperationException">Thrown when there is no open directory to end.</exception>
  public void EndDirectory() {
    if (this._folderStarts.Count == 0)
      throw new InvalidOperationException("No open directory to end.");

    var startIndex = this._folderStarts.Pop();
    // Count items between folder start and this end marker (exclusive of both).
    var itemCount = this._items.Count - startIndex - 1;
    this._items[startIndex].FolderItemCount = itemCount;

    this._items.Add(new PendingItem {
      Type = PendingItemType.FolderEnd,
    });
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      this.Flush();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  // ── Serialisation ──────────────────────────────────────────────────────────

  private void Flush() {
    // Close any unclosed folders.
    while (this._folderStarts.Count > 0)
      this.EndDirectory();

    // Count top-level entries for the volume header.
    var topLevelCount = CountTopLevelEntries();

    // Build the header table and the data blocks separately.
    using var headerStream = new MemoryStream();
    using var dataStream = new MemoryStream();

    foreach (var item in this._items) {
      switch (item.Type) {
        case PendingItemType.File:
          WriteFileHeader(headerStream, item, dataStream);
          break;
        case PendingItemType.FolderStart:
          WriteFolderHeader(headerStream, item);
          break;
        case PendingItemType.FolderEnd:
          WriteFolderEnd(headerStream);
          break;
      }
    }

    // Write volume header: magic (1 byte) + entry count (2 bytes BE).
    this._stream.WriteByte(CompactProConstants.Magic);
    Span<byte> countBuf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(countBuf, (ushort)topLevelCount);
    this._stream.Write(countBuf);

    // Write all entry headers.
    headerStream.Position = 0;
    headerStream.CopyTo(this._stream);

    // Write all compressed data blocks.
    dataStream.Position = 0;
    dataStream.CopyTo(this._stream);

    this._stream.Flush();
  }

  private int CountTopLevelEntries() {
    var count = 0;
    var depth = 0;
    foreach (var item in this._items) {
      switch (item.Type) {
        case PendingItemType.File:
          if (depth == 0) ++count;
          break;
        case PendingItemType.FolderStart:
          if (depth == 0) ++count;
          ++depth;
          break;
        case PendingItemType.FolderEnd:
          --depth;
          break;
      }
    }
    return count;
  }

  private static void WriteFileHeader(Stream headerStream, PendingItem item, Stream dataStream) {
    // Entry type.
    headerStream.WriteByte(CompactProConstants.EntryTypeFile);

    // Filename (Pascal-style: length byte + name bytes).
    var nameBytes = Encoding.Latin1.GetBytes(item.FileName);
    var nameLen = Math.Min(nameBytes.Length, CompactProConstants.FileNameMaxLength);
    headerStream.WriteByte((byte)nameLen);
    headerStream.Write(nameBytes, 0, nameLen);

    // Compression methods (both Store).
    headerStream.WriteByte(CompactProConstants.MethodStored);
    headerStream.WriteByte(CompactProConstants.MethodStored);

    Span<byte> buf4 = stackalloc byte[4];
    Span<byte> buf2 = stackalloc byte[2];

    // Data fork: uncompressed size.
    BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)item.DataFork.Length);
    headerStream.Write(buf4);

    // Data fork: compressed size (same as uncompressed for Store).
    BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)item.DataFork.Length);
    headerStream.Write(buf4);

    // Data fork CRC-16.
    var dataCrc = ComputeCrc16(item.DataFork);
    BinaryPrimitives.WriteUInt16BigEndian(buf2, dataCrc);
    headerStream.Write(buf2);

    // Resource fork: uncompressed size.
    BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)item.ResourceFork.Length);
    headerStream.Write(buf4);

    // Resource fork: compressed size (same as uncompressed for Store).
    BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)item.ResourceFork.Length);
    headerStream.Write(buf4);

    // Resource fork CRC-16.
    var resCrc = ComputeCrc16(item.ResourceFork);
    BinaryPrimitives.WriteUInt16BigEndian(buf2, resCrc);
    headerStream.Write(buf2);

    // File type (4 bytes BE).
    BinaryPrimitives.WriteUInt32BigEndian(buf4, 0x54455854); // 'TEXT'
    headerStream.Write(buf4);

    // File creator (4 bytes BE).
    BinaryPrimitives.WriteUInt32BigEndian(buf4, 0x43574945); // 'CWIE'
    headerStream.Write(buf4);

    // Creation date (Mac epoch).
    var createdMac = ToMacTimestamp(item.CreatedDate);
    BinaryPrimitives.WriteUInt32BigEndian(buf4, createdMac);
    headerStream.Write(buf4);

    // Modification date (Mac epoch).
    var modifiedMac = ToMacTimestamp(item.ModifiedDate);
    BinaryPrimitives.WriteUInt32BigEndian(buf4, modifiedMac);
    headerStream.Write(buf4);

    // Write data fork then resource fork to the data stream.
    if (item.DataFork.Length > 0)
      dataStream.Write(item.DataFork);
    if (item.ResourceFork.Length > 0)
      dataStream.Write(item.ResourceFork);
  }

  private static void WriteFolderHeader(Stream headerStream, PendingItem item) {
    // Entry type.
    headerStream.WriteByte(CompactProConstants.EntryTypeFolder);

    // Filename (Pascal-style).
    var nameBytes = Encoding.Latin1.GetBytes(item.FileName);
    var nameLen = Math.Min(nameBytes.Length, CompactProConstants.FileNameMaxLength);
    headerStream.WriteByte((byte)nameLen);
    headerStream.Write(nameBytes, 0, nameLen);

    // Number of items in folder (uint16 BE).
    Span<byte> buf2 = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(buf2, (ushort)item.FolderItemCount);
    headerStream.Write(buf2);
  }

  private static void WriteFolderEnd(Stream headerStream) {
    headerStream.WriteByte(CompactProConstants.EntryTypeEnd);
  }

  // ── CRC-16/CCITT (forward, non-reflected, init=0) ─────────────────────────

  private static readonly ushort[] Crc16Table = BuildCrc16Table();

  private static ushort[] BuildCrc16Table() {
    const ushort poly = CompactProConstants.Crc16Polynomial;
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

  // ── Helpers ────────────────────────────────────────────────────────────────

  private static uint ToMacTimestamp(DateTime dt) {
    if (dt <= CompactProConstants.MacEpoch)
      return 0;
    var diff = dt.ToUniversalTime() - CompactProConstants.MacEpoch;
    var totalSeconds = diff.TotalSeconds;
    return totalSeconds > uint.MaxValue ? uint.MaxValue : (uint)totalSeconds;
  }

  // ── Pending item types ─────────────────────────────────────────────────────

  private enum PendingItemType { File, FolderStart, FolderEnd }

  private sealed class PendingItem {
    public PendingItemType Type { get; init; }
    public string FileName { get; init; } = string.Empty;
    public byte[] DataFork { get; init; } = [];
    public byte[] ResourceFork { get; init; } = [];
    public DateTime CreatedDate { get; init; }
    public DateTime ModifiedDate { get; init; }
    public int FolderItemCount { get; set; }
  }
}
