using System.Buffers.Binary;
using System.Text;

namespace FileFormat.StuffIt;

/// <summary>
/// Creates a StuffIt (SIT) classic archive.
/// </summary>
/// <remarks>
/// Produces archives compatible with the classic "SIT!" format (version 1).
/// Supports Store (method 0) and RLE (method 1) compression.
/// Entries are buffered in memory and the archive is written on <see cref="Dispose"/>.
/// </remarks>
public sealed class StuffItWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<PendingEntry> _entries = [];
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
  /// <param name="fileName">The filename (up to 63 characters, Latin-1 encoded).</param>
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
  /// <param name="fileName">The filename (up to 63 characters, Latin-1 encoded).</param>
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

    byte dataMethod;
    byte[] compressedData = CompressFork(dataFork, out dataMethod);

    byte resMethod;
    byte[] compressedResource = CompressFork(resourceFork, out resMethod);

    this._entries.Add(new PendingEntry {
      FileName           = fileName,
      FileType           = fileType,
      FileCreator        = fileCreator,
      LastModified       = lastModified ?? DateTime.UtcNow,
      DataFork           = dataFork,
      ResourceFork       = resourceFork,
      CompressedData     = compressedData,
      CompressedResource = compressedResource,
      DataMethod         = dataMethod,
      ResourceMethod     = resMethod,
    });
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
    // Buffer all entry headers + data into a MemoryStream, then write the
    // archive header (which needs file count and total length) followed by
    // the buffered content.
    using var buffer = new MemoryStream();

    foreach (PendingEntry entry in this._entries)
      WriteEntry(buffer, entry);

    // Write archive header (22 bytes), matching the reader's layout:
    //   [0..4]   magic "SIT!"
    //   [4..6]   file count (uint16 BE)
    //   [6..10]  total archive length (uint32 BE)
    //   [10..14] "rLau" signature (uint32 BE)
    //   [14]     version (1)
    //   [15..22] reserved (zeros)
    Span<byte> archiveHeader = stackalloc byte[StuffItConstants.ArchiveHeaderSize];
    archiveHeader.Clear();

    uint totalLength = (uint)(StuffItConstants.ArchiveHeaderSize + buffer.Length);

    BinaryPrimitives.WriteUInt32BigEndian(archiveHeader, StuffItConstants.MagicSit);
    BinaryPrimitives.WriteUInt16BigEndian(archiveHeader[4..], (ushort)this._entries.Count);
    BinaryPrimitives.WriteUInt32BigEndian(archiveHeader[6..], totalLength);
    BinaryPrimitives.WriteUInt32BigEndian(archiveHeader[10..], StuffItConstants.ArchiveSignatureRLau);
    archiveHeader[14] = 1;

    this._stream.Write(archiveHeader);

    // Write buffered entries.
    buffer.Position = 0;
    buffer.CopyTo(this._stream);
    this._stream.Flush();
  }

  private static void WriteEntry(Stream output, PendingEntry entry) {
    Span<byte> hdr = stackalloc byte[StuffItConstants.EntryHeaderSize];
    hdr.Clear();

    // [0] resource fork method
    hdr[0] = entry.ResourceMethod;
    // [1] data fork method
    hdr[1] = entry.DataMethod;

    // [2] name length, [3..66] filename (Latin-1, null-padded)
    byte[] nameBytes = Encoding.Latin1.GetBytes(entry.FileName);
    int nameLen = Math.Min(nameBytes.Length, StuffItConstants.FileNameMaxLength);
    hdr[2] = (byte)nameLen;
    nameBytes.AsSpan(0, nameLen).CopyTo(hdr.Slice(StuffItConstants.FileNameOffset, nameLen));

    // [66..70] file type (4 chars ASCII)
    WriteAscii4(hdr[66..], entry.FileType);
    // [70..74] file creator (4 chars ASCII)
    WriteAscii4(hdr[70..], entry.FileCreator);

    // [74..76] Finder flags — zero
    // [76..80] creation date — use same as modification date
    uint macTimestamp = ToMacTimestamp(entry.LastModified);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[76..], macTimestamp);
    // [80..84] modification date
    BinaryPrimitives.WriteUInt32BigEndian(hdr[80..], macTimestamp);

    // [84..88] resource fork uncompressed size
    BinaryPrimitives.WriteUInt32BigEndian(hdr[84..], (uint)entry.ResourceFork.Length);
    // [88..92] data fork uncompressed size
    BinaryPrimitives.WriteUInt32BigEndian(hdr[88..], (uint)entry.DataFork.Length);
    // [92..96] resource fork compressed size
    BinaryPrimitives.WriteUInt32BigEndian(hdr[92..], (uint)entry.CompressedResource.Length);
    // [96..100] data fork compressed size
    BinaryPrimitives.WriteUInt32BigEndian(hdr[96..], (uint)entry.CompressedData.Length);

    // [100..102] resource fork CRC-16
    ushort resCrc = ComputeCrc16(entry.ResourceFork);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[100..], resCrc);
    // [102..104] data fork CRC-16
    ushort dataCrc = ComputeCrc16(entry.DataFork);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[102..], dataCrc);

    // [104..110] reserved — zeros

    // [110..112] header CRC-16 over bytes 0..110
    ushort headerCrc = ComputeCrc16(hdr[..110]);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[110..], headerCrc);

    output.Write(hdr);

    // Write resource fork compressed data, then data fork compressed data.
    if (entry.CompressedResource.Length > 0)
      output.Write(entry.CompressedResource);
    if (entry.CompressedData.Length > 0)
      output.Write(entry.CompressedData);
  }

  // ── Compression ──────────────────────────────────────────────────────────

  private static byte[] CompressFork(byte[] data, out byte method) {
    if (data.Length == 0) {
      method = StuffItConstants.MethodStore;
      return [];
    }

    byte[] rleEncoded = StuffItRle.Encode(data);
    if (rleEncoded.Length < data.Length) {
      method = StuffItConstants.MethodRle;
      return rleEncoded;
    }

    method = StuffItConstants.MethodStore;
    return data;
  }

  // ── CRC-16/CCITT (forward, non-reflected, init=0) ───────────────────────

  private static readonly ushort[] Crc16Table = BuildCrc16Table();

  private static ushort[] BuildCrc16Table() {
    const ushort poly = StuffItConstants.Crc16Polynomial;
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

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static void WriteAscii4(Span<byte> dest, string value) {
    byte[] bytes = Encoding.ASCII.GetBytes(value);
    int len = Math.Min(bytes.Length, 4);
    bytes.AsSpan(0, len).CopyTo(dest);
    // Pad with spaces if shorter than 4.
    for (int i = len; i < 4; ++i)
      dest[i] = (byte)' ';
  }

  private static uint ToMacTimestamp(DateTime dt) {
    if (dt <= StuffItConstants.MacEpoch)
      return 0;
    TimeSpan diff = dt.ToUniversalTime() - StuffItConstants.MacEpoch;
    double totalSeconds = diff.TotalSeconds;
    return totalSeconds > uint.MaxValue ? uint.MaxValue : (uint)totalSeconds;
  }

  // ── Pending entry ────────────────────────────────────────────────────────

  private sealed class PendingEntry {
    public string FileName { get; init; } = string.Empty;
    public string FileType { get; init; } = "TEXT";
    public string FileCreator { get; init; } = "CWIE";
    public DateTime LastModified { get; init; }
    public byte[] DataFork { get; init; } = [];
    public byte[] ResourceFork { get; init; } = [];
    public byte[] CompressedData { get; init; } = [];
    public byte[] CompressedResource { get; init; } = [];
    public byte DataMethod { get; init; }
    public byte ResourceMethod { get; init; }
  }
}
