using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Zpaq;

/// <summary>
/// Writes a ZPAQ level-1 journaling archive.
/// </summary>
/// <remarks>
/// <para>
/// Each call to <see cref="AddFile"/> or <see cref="AddDirectory"/> creates a
/// transaction consisting of a header block ('c'), a data block ('d'), and an
/// index block ('h'). Data is stored uncompressed (no ZPAQL program is needed).
/// </para>
/// <para>
/// The writer produces archives that can be read by the reference <c>zpaq</c>
/// tool (level 1 journaling format) and by <see cref="ZpaqReader"/>.
/// </para>
/// </remarks>
public sealed class ZpaqWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  // Accumulate SHA-1 hashes for the final index block.
  private readonly List<byte[]> _hashes = [];
  // Track whether we have written anything (for the final index block).
  private bool _hasEntries;

  /// <summary>
  /// Initializes a new ZPAQ writer that writes to the specified stream.
  /// </summary>
  /// <param name="stream">A writable stream to receive the archive data.</param>
  /// <param name="leaveOpen">
  /// <see langword="true"/> to leave <paramref name="stream"/> open when
  /// this writer is disposed; <see langword="false"/> (default) to dispose it.
  /// </param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="stream"/> is <see langword="null"/>.
  /// </exception>
  /// <exception cref="ArgumentException">
  /// Thrown when <paramref name="stream"/> is not writable.
  /// </exception>
  public ZpaqWriter(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanWrite)
      throw new ArgumentException("Stream must be writable.", nameof(stream));

    _stream = stream;
    _leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a file entry to the archive with the given name and data.
  /// </summary>
  /// <param name="fileName">
  /// The filename to store in the archive. Forward slashes are used as path separators.
  /// </param>
  /// <param name="data">The uncompressed file data.</param>
  /// <param name="lastModified">
  /// The last-modified timestamp for the file.
  /// Defaults to <see cref="DateTime.UtcNow"/> if not specified.
  /// </param>
  /// <exception cref="ObjectDisposedException">Thrown if the writer has been disposed.</exception>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="fileName"/> or <paramref name="data"/> is <see langword="null"/>.
  /// </exception>
  public void AddFile(string fileName, byte[] data, DateTime? lastModified = null) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);

    var timestamp = lastModified ?? DateTime.UtcNow;
    var normalizedName = NormalizeName(fileName);

    // Compute SHA-1 hash of the data.
    var hash = Sha1.Compute(data);

    // Write header block ('c').
    WriteHeaderBlock(normalizedName, (long)data.Length, timestamp, isDirectory: false);

    // Write data block ('d').
    WriteDataBlock(data);

    // Record hash for the index block.
    _hashes.Add(hash);
    _hasEntries = true;
  }

  /// <summary>
  /// Adds a directory entry to the archive.
  /// </summary>
  /// <param name="dirName">
  /// The directory name. A trailing '/' is added if not already present.
  /// </param>
  /// <param name="lastModified">
  /// The last-modified timestamp. Defaults to <see cref="DateTime.UtcNow"/> if not specified.
  /// </param>
  /// <exception cref="ObjectDisposedException">Thrown if the writer has been disposed.</exception>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="dirName"/> is <see langword="null"/>.
  /// </exception>
  public void AddDirectory(string dirName, DateTime? lastModified = null) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(dirName);

    var timestamp = lastModified ?? DateTime.UtcNow;
    var normalizedName = NormalizeName(dirName);
    if (!normalizedName.EndsWith('/'))
      normalizedName += '/';

    // Write header block ('c') with size 0.
    WriteHeaderBlock(normalizedName, 0, timestamp, isDirectory: true);

    // Write an empty data block ('d').
    WriteDataBlock([]);

    // Directory entries get a zero hash.
    _hashes.Add(new byte[Sha1.HashSize]);
    _hasEntries = true;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (_disposed)
      return;

    _disposed = true;

    // Write the final index ('h') block with all accumulated SHA-1 hashes.
    if (_hasEntries)
      WriteIndexBlock();

    _stream.Flush();

    if (!_leaveOpen)
      _stream.Dispose();
  }

  // ── Block writers ──────────────────────────────────────────────────────────

  /// <summary>
  /// Writes a header ('c') block for a single file or directory entry.
  /// </summary>
  /// <remarks>
  /// Layout:
  /// <code>
  /// "zPQ" (3 bytes) + level (1) + 'c' (1)
  /// FILETIME (8 bytes, little-endian Windows FILETIME)
  /// attribute (1 byte: 0x20 = file, 0x10 = directory)
  /// filename (UTF-8, null-terminated)
  /// size (8 bytes, little-endian int64)
  /// 0xFF (end-of-block marker)
  /// </code>
  /// </remarks>
  private void WriteHeaderBlock(string name, long size, DateTime timestamp, bool isDirectory) {
    // Block prefix.
    _stream.Write(ZpaqConstants.BlockPrefix);
    _stream.WriteByte(ZpaqConstants.Level1);
    _stream.WriteByte(ZpaqConstants.BlockTypeHeader);

    // Windows FILETIME (100-nanosecond intervals since 1601-01-01 UTC).
    long fileTime = EncodeWindowsFileTime(timestamp);
    Span<byte> ftBytes = stackalloc byte[8];
    BitConverter.TryWriteBytes(ftBytes, fileTime);
    _stream.Write(ftBytes);

    // Attribute byte: 0x20 = normal file, 0x10 = directory (Windows convention).
    _stream.WriteByte(isDirectory ? (byte)0x10 : (byte)0x20);

    // Null-terminated UTF-8 filename.
    var nameBytes = Encoding.UTF8.GetBytes(name);
    _stream.Write(nameBytes);
    _stream.WriteByte(0); // null terminator

    // Uncompressed size (8 bytes, little-endian).
    Span<byte> sizeBytes = stackalloc byte[8];
    BitConverter.TryWriteBytes(sizeBytes, size);
    _stream.Write(sizeBytes);

    // End-of-block marker.
    _stream.WriteByte(0xFF);
  }

  /// <summary>
  /// Writes a data ('d') block containing the raw (uncompressed) file data.
  /// </summary>
  /// <remarks>
  /// Layout:
  /// <code>
  /// "zPQ" (3 bytes) + level (1) + 'd' (1)
  /// raw data bytes
  /// </code>
  /// </remarks>
  private void WriteDataBlock(byte[] data) {
    // Block prefix.
    _stream.Write(ZpaqConstants.BlockPrefix);
    _stream.WriteByte(ZpaqConstants.Level1);
    _stream.WriteByte(ZpaqConstants.BlockTypeData);

    // Raw data payload.
    if (data.Length > 0)
      _stream.Write(data);
  }

  /// <summary>
  /// Writes the index ('h') block containing SHA-1 hashes for all files
  /// added to the archive.
  /// </summary>
  /// <remarks>
  /// Layout:
  /// <code>
  /// "zPQ" (3 bytes) + level (1) + 'h' (1)
  /// For each file: SHA-1 hash (20 bytes)
  /// </code>
  /// </remarks>
  private void WriteIndexBlock() {
    // Block prefix.
    _stream.Write(ZpaqConstants.BlockPrefix);
    _stream.WriteByte(ZpaqConstants.Level1);
    _stream.WriteByte(ZpaqConstants.BlockTypeIndex);

    // SHA-1 hashes, one per entry.
    foreach (var hash in _hashes)
      _stream.Write(hash);
  }

  // ── Utilities ──────────────────────────────────────────────────────────────

  private static string NormalizeName(string name) =>
    name.Replace('\\', '/');

  /// <summary>
  /// Encodes a <see cref="DateTime"/> as a Windows FILETIME (little-endian int64).
  /// </summary>
  private static long EncodeWindowsFileTime(DateTime dateTime) {
    try {
      return dateTime.Kind == DateTimeKind.Utc
        ? dateTime.ToFileTimeUtc()
        : dateTime.ToUniversalTime().ToFileTimeUtc();
    } catch {
      // If the date is out of range for FILETIME, return 0.
      return 0;
    }
  }
}
