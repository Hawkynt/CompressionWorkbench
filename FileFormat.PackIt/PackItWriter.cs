using System.Buffers.Binary;
using System.Text;

namespace FileFormat.PackIt;

/// <summary>
/// Creates a PackIt (.pit) classic Macintosh archive.
/// </summary>
/// <remarks>
/// Produces archives using the stored ("PMag") entry format only. All entries are
/// written with an empty resource fork. The archive is written incrementally on each
/// <see cref="AddFile"/> call, so the stream must remain writable until
/// <see cref="Dispose"/> is called.
/// </remarks>
public sealed class PackItWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>
  /// Initialises a new <see cref="PackItWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the .pit archive to.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open when this writer is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  public PackItWriter(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Appends a stored file entry to the archive.
  /// </summary>
  /// <param name="name">The filename (up to 62 characters, Latin-1 encoded).</param>
  /// <param name="data">The uncompressed data fork bytes.</param>
  /// <param name="fileType">The four-character Mac file type code (default "TEXT").</param>
  /// <param name="creator">The four-character Mac creator code (default "CWIE").</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> or <paramref name="data"/> is null.</exception>
  /// <exception cref="ObjectDisposedException">Thrown when this writer has been disposed.</exception>
  public void AddFile(
      string name,
      byte[] data,
      string fileType = "TEXT",
      string creator  = "CWIE") {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    this.WriteEntry(name, data, fileType, creator);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      this._stream.Flush();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  // ── Serialisation ─────────────────────────────────────────────────────────────

  private void WriteEntry(string name, byte[] data, string fileType, string creator) {
    // Magic "PMag" = stored.
    this._stream.Write(PackItConstants.MagicStored);

    // Filename field: 63 bytes (Pascal string: 1 length byte + up to 62 name bytes).
    var nameBytes = Encoding.Latin1.GetBytes(name);
    var nameLen   = Math.Min(nameBytes.Length, PackItConstants.FileNameMaxLength);
    Span<byte> nameField = stackalloc byte[PackItConstants.FileNameMaxLength + 1]; // 63 bytes
    nameField.Clear();
    nameField[0] = (byte)nameLen;
    nameBytes.AsSpan(0, nameLen).CopyTo(nameField[1..]);
    this._stream.Write(nameField);

    // File type (4 bytes) + creator (4 bytes).
    Span<byte> typeField    = stackalloc byte[4];
    Span<byte> creatorField = stackalloc byte[4];
    WriteAscii4(typeField,    fileType);
    WriteAscii4(creatorField, creator);
    this._stream.Write(typeField);
    this._stream.Write(creatorField);

    // Finder flags (2 bytes) + locked (1 byte) + zero padding (1 byte) = 4 bytes.
    Span<byte> meta = stackalloc byte[4];
    meta.Clear();
    this._stream.Write(meta);

    // Data fork size (uint32 BE) + resource fork size (uint32 BE) = 8 bytes.
    Span<byte> sizes = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(sizes,      (uint)data.Length);
    BinaryPrimitives.WriteUInt32BigEndian(sizes[4..], 0u); // resource fork = empty
    this._stream.Write(sizes);

    // Data fork bytes.
    if (data.Length > 0)
      this._stream.Write(data);
    // Resource fork bytes: zero bytes (already accounted for by size = 0).
  }

  // ── Helpers ───────────────────────────────────────────────────────────────────

  private static void WriteAscii4(Span<byte> dest, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    var len   = Math.Min(bytes.Length, 4);
    bytes.AsSpan(0, len).CopyTo(dest);
    for (var i = len; i < 4; ++i)
      dest[i] = (byte)' ';
  }
}
