using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Ha;

/// <summary>
/// Creates an Ha archive.
/// </summary>
public sealed class HaWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _firstEntry = true;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="HaWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the Ha archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this writer is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  public HaWriter(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  // ── Public API ───────────────────────────────────────────────────────────

  /// <summary>
  /// Adds a file entry to the archive using Store compression (method 0).
  /// </summary>
  /// <param name="fileName">The filename to store (use '/' as path separator).</param>
  /// <param name="data">The uncompressed file data.</param>
  /// <param name="lastModified">The last-modification timestamp. Defaults to the current local time.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="fileName"/> or <paramref name="data"/> is null.</exception>
  public void AddFile(string fileName, byte[] data, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);

    var crc         = Crc32.Compute(data);
    var dosDateTime = HaEntry.EncodeMsDosDateTime(lastModified ?? DateTime.Now);

    WriteEntryHeader(
      method:         HaConstants.MethodStore,
      compressedSize: (uint)data.Length,
      originalSize:   (uint)data.Length,
      crc32:          crc,
      dosDateTime:    dosDateTime,
      fileName:       fileName);

    this._stream.Write(data);
  }

  /// <summary>
  /// Adds a directory entry to the archive (method 14, zero size).
  /// </summary>
  /// <param name="name">The directory name (use '/' as path separator).</param>
  /// <param name="lastModified">The last-modification timestamp. Defaults to the current local time.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
  public void AddDirectory(string name, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(name);

    var dosDateTime = HaEntry.EncodeMsDosDateTime(lastModified ?? DateTime.Now);

    WriteEntryHeader(
      method:         HaConstants.MethodDirectory,
      compressedSize: 0u,
      originalSize:   0u,
      crc32:          0u,
      dosDateTime:    dosDateTime,
      fileName:       name);

    // No data bytes follow a directory entry.
  }

  // ── Low-level serialisation ──────────────────────────────────────────────

  private void WriteEntryHeader(
      int method,
      uint compressedSize,
      uint originalSize,
      uint crc32,
      uint dosDateTime,
      string fileName) {

    // Write "HA" magic before the very first entry.
    if (this._firstEntry) {
      this._stream.Write(HaConstants.Magic);
      this._firstEntry = false;
    }

    var writer = new BinaryWriter(this._stream, Encoding.Latin1, leaveOpen: true);

    // Version (high nibble = 0) + method (low nibble).
    writer.Write((byte)(method & 0x0F));

    // Sizes and checksum.
    writer.Write(compressedSize);
    writer.Write(originalSize);
    writer.Write(crc32);

    // MS-DOS date/time.
    writer.Write(dosDateTime);

    // Null-terminated filename.
    var nameBytes = Encoding.Latin1.GetBytes(fileName);
    writer.Write(nameBytes);
    writer.Write((byte)0); // null terminator
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
