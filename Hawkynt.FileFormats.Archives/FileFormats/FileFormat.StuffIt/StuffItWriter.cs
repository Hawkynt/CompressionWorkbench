using System.Buffers.Binary;
using System.Text;

namespace FileFormat.StuffIt;

/// <summary>
/// Creates a StuffIt (SIT) classic archive.
/// </summary>
/// <remarks>
/// <para>
/// Produces archives in the classic "SIT!" format (version 1). Each fork is stored, RLE-packed
/// (method 1) or LZ+Huffman-compressed (method 13), whichever is smallest. Entries are buffered in memory and the archive is written on <see cref="Dispose"/>.
/// </para>
/// <para>
/// '/'-separated names create folders. A folder is a 112-byte entry header whose two method bytes
/// are 0x20 (folder start), followed by the folder's entries and a second header with method 0x21
/// (folder end). The archive header counts only the entries at the root.
/// </para>
/// </remarks>
public sealed class StuffItWriter : IDisposable {
  private const byte FolderStart = 0x20;
  private const byte FolderEnd = 0x21;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly StuffItTree<PendingEntry> _tree = new();
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="StuffItWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the SIT archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this writer is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  public StuffItWriter(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  // ── Public API ───────────────────────────────────────────────────────────

  /// <summary>
  /// Adds a file entry to the archive with an empty resource fork.
  /// </summary>
  /// <param name="fileName">The path; '/' separates folders, each name holding 1-31 Latin-1 bytes.</param>
  /// <param name="data">The uncompressed data fork bytes.</param>
  /// <param name="fileType">The four-character Mac file type code.</param>
  /// <param name="fileCreator">The four-character Mac file creator code.</param>
  /// <param name="lastModified">The last-modification timestamp. Defaults to <see cref="DateTime.UtcNow"/>.</param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="fileName"/> or <paramref name="data"/> is null.
  /// </exception>
  public void AddFile(
      string fileName,
      byte[] data,
      string fileType = "TEXT",
      string fileCreator = "CWIE",
      DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);

    AddFileWithResourceFork(fileName, data, [], fileType, fileCreator, lastModified);
  }

  /// <summary>
  /// Adds a file entry to the archive with both data and resource forks.
  /// </summary>
  /// <param name="fileName">The path; '/' separates folders, each name holding 1-31 Latin-1 bytes.</param>
  /// <param name="dataFork">The uncompressed data fork bytes.</param>
  /// <param name="resourceFork">The uncompressed resource fork bytes.</param>
  /// <param name="fileType">The four-character Mac file type code.</param>
  /// <param name="fileCreator">The four-character Mac file creator code.</param>
  /// <param name="lastModified">The last-modification timestamp. Defaults to <see cref="DateTime.UtcNow"/>.</param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="fileName"/>, <paramref name="dataFork"/>, or
  /// <paramref name="resourceFork"/> is null.
  /// </exception>
  public void AddFileWithResourceFork(
      string fileName,
      byte[] dataFork,
      byte[] resourceFork,
      string fileType = "TEXT",
      string fileCreator = "CWIE",
      DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(dataFork);
    ArgumentNullException.ThrowIfNull(resourceFork);
    CheckPath(fileName);

    var compressedData = CompressFork(dataFork, out var dataMethod);
    var compressedResource = CompressFork(resourceFork, out var resMethod);
    var modified = lastModified ?? DateTime.UtcNow;
    this._tree.AddFile(fileName, new PendingEntry {
      FileType           = fileType,
      FileCreator        = fileCreator,
      DataFork           = dataFork,
      ResourceFork       = resourceFork,
      CompressedData     = compressedData,
      CompressedResource = compressedResource,
      DataMethod         = dataMethod,
      ResourceMethod     = resMethod,
    }, modified);
  }

  /// <summary>Adds a folder, which may stay empty; folders named in file paths are created on their own.</summary>
  /// <param name="path">The folder path; '/' separates folders, each name holding 1-31 Latin-1 bytes.</param>
  /// <param name="lastModified">The last-modification timestamp. Defaults to <see cref="DateTime.UtcNow"/>.</param>
  public void AddDirectory(string path, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(path);
    CheckPath(path);
    this._tree.AddFolder(path, lastModified ?? DateTime.UtcNow);
  }

  private static void CheckPath(string path) {
    foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
      if (Encoding.Latin1.GetByteCount(part) is 0 or > StuffItConstants.FileNameMaxLength)
        throw new NotSupportedException(
          $"A classic StuffIt name holds 1-{StuffItConstants.FileNameMaxLength} bytes; '{part}' would be truncated.");
  }

  // ── IDisposable ──────────────────────────────────────────────────────────

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      Flush();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  // ── Serialisation ────────────────────────────────────────────────────────

  private void Flush() {
    // Buffer all entry headers + data, then write the archive header (which needs the root
    // entry count and total length) followed by the buffered content.
    using var buffer = new MemoryStream();
    foreach (var node in this._tree.Root.Children)
      WriteNode(buffer, node);

    // Archive header (22 bytes):
    //   [0..4]   magic "SIT!"
    //   [4..6]   number of entries at the root (uint16 BE)
    //   [6..10]  total archive length (uint32 BE)
    //   [10..14] "rLau" signature (uint32 BE)
    //   [14]     version (1)
    //   [15..22] reserved (zeros)
    Span<byte> archiveHeader = stackalloc byte[StuffItConstants.ArchiveHeaderSize];
    archiveHeader.Clear();

    var totalLength = (uint)(StuffItConstants.ArchiveHeaderSize + buffer.Length);

    BinaryPrimitives.WriteUInt32BigEndian(archiveHeader, StuffItConstants.MagicSit);
    BinaryPrimitives.WriteUInt16BigEndian(archiveHeader[4..], (ushort)this._tree.Root.Children.Count);
    BinaryPrimitives.WriteUInt32BigEndian(archiveHeader[6..], totalLength);
    BinaryPrimitives.WriteUInt32BigEndian(archiveHeader[10..], StuffItConstants.ArchiveSignatureRLau);
    archiveHeader[14] = 1;

    this._stream.Write(archiveHeader);

    buffer.Position = 0;
    buffer.CopyTo(this._stream);
    this._stream.Flush();
  }

  private static void WriteNode(Stream output, StuffItTree<PendingEntry>.Node node) {
    if (node.File is { } file) {
      WriteEntry(output, node.Name, node.Modified, file);
      return;
    }
    WriteFolderMarker(output, node.Name, node.Modified, FolderStart);
    foreach (var child in node.Children) WriteNode(output, child);
    WriteFolderMarker(output, node.Name, node.Modified, FolderEnd);
  }

  private static void WriteFolderMarker(Stream output, string name, DateTime modified, byte method) {
    Span<byte> hdr = stackalloc byte[StuffItConstants.EntryHeaderSize];
    hdr.Clear();
    hdr[0] = method;
    hdr[1] = method;
    WriteName(hdr, name);
    var macTimestamp = ToMacTimestamp(modified);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[76..], macTimestamp);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[80..], macTimestamp);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[110..], ComputeCrc16(hdr[..110]));
    output.Write(hdr);
  }

  private static void WriteName(Span<byte> hdr, string name) {
    var nameBytes = Encoding.Latin1.GetBytes(name);
    hdr[2] = (byte)nameBytes.Length;
    nameBytes.CopyTo(hdr.Slice(StuffItConstants.FileNameOffset, nameBytes.Length));
  }

  private static void WriteEntry(Stream output, string name, DateTime modified, PendingEntry entry) {
    Span<byte> hdr = stackalloc byte[StuffItConstants.EntryHeaderSize];
    hdr.Clear();

    // [0] resource fork method, [1] data fork method
    hdr[0] = entry.ResourceMethod;
    hdr[1] = entry.DataMethod;

    // [2] name length, [3..34] name (Latin-1, zero-padded)
    WriteName(hdr, name);

    // [66..70] file type, [70..74] file creator (4 chars ASCII)
    WriteAscii4(hdr[66..], entry.FileType);
    WriteAscii4(hdr[70..], entry.FileCreator);

    // [74..76] Finder flags — zero; [76..80] creation date, [80..84] modification date
    var macTimestamp = ToMacTimestamp(modified);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[76..], macTimestamp);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[80..], macTimestamp);

    // [84..100] fork sizes: resource, data, compressed resource, compressed data
    BinaryPrimitives.WriteUInt32BigEndian(hdr[84..], (uint)entry.ResourceFork.Length);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[88..], (uint)entry.DataFork.Length);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[92..], (uint)entry.CompressedResource.Length);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[96..], (uint)entry.CompressedData.Length);

    // [100..104] fork CRC-16s; [104..110] reserved; [110..112] header CRC-16 over bytes 0..110
    BinaryPrimitives.WriteUInt16BigEndian(hdr[100..], ComputeCrc16(entry.ResourceFork));
    BinaryPrimitives.WriteUInt16BigEndian(hdr[102..], ComputeCrc16(entry.DataFork));
    BinaryPrimitives.WriteUInt16BigEndian(hdr[110..], ComputeCrc16(hdr[..110]));

    output.Write(hdr);

    // Resource fork data, then data fork data.
    if (entry.CompressedResource.Length > 0)
      output.Write(entry.CompressedResource);
    if (entry.CompressedData.Length > 0)
      output.Write(entry.CompressedData);
  }

  // ── Compression ──────────────────────────────────────────────────────────

  /// <summary>Picks the smallest of method 13 (LZ+Huffman), RLE and storing.</summary>
  internal static byte[] CompressFork(byte[] data, out byte method) {
    method = StuffItConstants.MethodStore;
    if (data.Length == 0) return [];

    var best = data;
    var lzHuffman = StuffIt13Encoder.Encode(data);
    if (lzHuffman.Length < best.Length) {
      best = lzHuffman;
      method = StuffItConstants.MethodLzHuffman;
    }
    var rleEncoded = StuffItRle90.Encode(data);
    if (rleEncoded.Length < best.Length) {
      best = rleEncoded;
      method = StuffItConstants.MethodRle;
    }
    return best;
  }

  // StuffIt checks headers and forks with CRC-16/ARC (reflected 0xA001, init 0), as every reader does.
  internal static ushort ComputeCrc16(ReadOnlySpan<byte> data) => Compression.Core.Checksums.Crc16.Compute(data);

  // ── Helpers ──────────────────────────────────────────────────────────────

  internal static void WriteAscii4(Span<byte> dest, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    var len = Math.Min(bytes.Length, 4);
    bytes.AsSpan(0, len).CopyTo(dest);
    // Pad with spaces if shorter than 4.
    for (var i = len; i < 4; ++i)
      dest[i] = (byte)' ';
  }

  internal static uint ToMacTimestamp(DateTime dt) {
    if (dt <= StuffItConstants.MacEpoch)
      return 0;
    var diff = dt.ToUniversalTime() - StuffItConstants.MacEpoch;
    var totalSeconds = diff.TotalSeconds;
    return totalSeconds > uint.MaxValue ? uint.MaxValue : (uint)totalSeconds;
  }

  // ── Pending entry ────────────────────────────────────────────────────────

  private sealed class PendingEntry {
    public string FileType { get; init; } = "TEXT";
    public string FileCreator { get; init; } = "CWIE";
    public byte[] DataFork { get; init; } = [];
    public byte[] ResourceFork { get; init; } = [];
    public byte[] CompressedData { get; init; } = [];
    public byte[] CompressedResource { get; init; } = [];
    public byte DataMethod { get; init; }
    public byte ResourceMethod { get; init; }
  }
}
