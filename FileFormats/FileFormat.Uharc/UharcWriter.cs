using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzp;

namespace FileFormat.Uharc;

/// <summary>
/// Creates a UHARC archive.
/// </summary>
public sealed class UharcWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="UharcWriter"/> and writes the archive header.
  /// </summary>
  /// <param name="stream">The stream to write the UHARC archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this writer is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  public UharcWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    WriteHeader();
  }

  // ── Public API ───────────────────────────────────────────────────────────

  /// <summary>
  /// Adds a file entry to the archive. Compresses with LZP, falling back to Store
  /// if the compressed output is not smaller.
  /// </summary>
  /// <param name="fileName">The filename to store (use '/' as path separator).</param>
  /// <param name="data">The uncompressed file data.</param>
  /// <param name="lastModified">The last-modification timestamp. Defaults to the current UTC time.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="fileName"/> or <paramref name="data"/> is null.</exception>
  public void AddFile(string fileName, byte[] data, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);

    var crc = Crc32.Compute(data);
    var timestamp = UharcEntry.EncodeUnixTimestamp(lastModified ?? DateTime.UtcNow);

    // Attempt LZP compression.
    var compressed = LzpCompressor.Compress(data);
    byte method;
    byte[] payload;

    if (compressed.Length < data.Length) {
      method = UharcConstants.MethodLzp;
      payload = compressed;
    } else {
      method = UharcConstants.MethodStore;
      payload = data;
    }

    WriteEntryHeader(
      method: method,
      originalSize: (uint)data.Length,
      compressedSize: (uint)payload.Length,
      crc32: crc,
      timestamp: timestamp,
      fileName: fileName,
      isDirectory: false);

    this._stream.Write(payload);
  }

  /// <summary>
  /// Adds a directory entry to the archive (zero data).
  /// </summary>
  /// <param name="name">The directory name (use '/' as path separator).</param>
  /// <param name="lastModified">The last-modification timestamp. Defaults to the current UTC time.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
  public void AddDirectory(string name, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(name);

    var timestamp = UharcEntry.EncodeUnixTimestamp(lastModified ?? DateTime.UtcNow);

    WriteEntryHeader(
      method: UharcConstants.MethodStore,
      originalSize: 0u,
      compressedSize: 0u,
      crc32: 0u,
      timestamp: timestamp,
      fileName: name,
      isDirectory: true);

    // No data bytes follow a directory entry.
  }

  // ── Low-level serialisation ──────────────────────────────────────────────

  private void WriteHeader() {
    // 3 bytes magic + 1 byte version + 3 bytes flags (reserved zeros).
    this._stream.Write(UharcConstants.Magic);
    this._stream.WriteByte(UharcConstants.Version);
    this._stream.WriteByte(0); // flags[0]
    this._stream.WriteByte(0); // flags[1]
    this._stream.WriteByte(0); // flags[2]
  }

  private void WriteEntryHeader(
      byte method,
      uint originalSize,
      uint compressedSize,
      uint crc32,
      uint timestamp,
      string fileName,
      bool isDirectory) {

    var writer = new BinaryWriter(this._stream, Encoding.UTF8, leaveOpen: true);

    writer.Write(method);
    writer.Write(originalSize);
    writer.Write(compressedSize);
    writer.Write(crc32);
    writer.Write(timestamp);

    var nameBytes = Encoding.UTF8.GetBytes(fileName);
    writer.Write((ushort)nameBytes.Length);
    writer.Write(nameBytes);

    var attributes = isDirectory ? (byte)0x01 : (byte)0x00;
    writer.Write(attributes);
  }

  // ── IDisposable ──────────────────────────────────────────────────────────

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      this._stream.Flush();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
