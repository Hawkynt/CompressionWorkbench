using System.Text;

namespace FileFormat.Cpio;

/// <summary>
/// Creates a cpio archive in the "new" (SVR4) ASCII format.
/// </summary>
public sealed class CpioWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _finished;
  private bool _disposed;
  private uint _nextInode = 1;

  /// <summary>
  /// Initializes a new <see cref="CpioWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the cpio archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public CpioWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a file entry.
  /// </summary>
  /// <param name="name">The file name.</param>
  /// <param name="data">The file data.</param>
  /// <param name="mode">The file mode. Defaults to regular file with 0644 permissions.</param>
  public void AddFile(string name, ReadOnlySpan<byte> data, uint mode = 0x81A4) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    WriteEntry(name, data, mode);
  }

  /// <summary>
  /// Adds a directory entry.
  /// </summary>
  /// <param name="name">The directory name.</param>
  /// <param name="mode">The directory mode. Defaults to directory with 0755 permissions.</param>
  public void AddDirectory(string name, uint mode = 0x41ED) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    WriteEntry(name, [], mode);
  }

  /// <summary>
  /// Writes the trailer and finishes the archive.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    // Write trailer entry
    WriteEntry(CpioConstants.Trailer, [], 0);
  }

  private void WriteEntry(string name, ReadOnlySpan<byte> data, uint mode) {
    // Name must include null terminator
    var nameBytes = Encoding.ASCII.GetBytes(name + '\0');
    var nameSize = nameBytes.Length;

    var inode = name == CpioConstants.Trailer ? 0u : ++this._nextInode;

    // Build header string (110 bytes, all hex)
    var header = string.Format(
      "{0}{1:X8}{2:X8}{3:X8}{4:X8}{5:X8}{6:X8}{7:X8}{8:X8}{9:X8}{10:X8}{11:X8}{12:X8}{13:X8}",
      CpioConstants.NewAsciiMagic,
      inode,          // c_ino
      mode,           // c_mode
      0u,             // c_uid
      0u,             // c_gid
      1u,             // c_nlink
      0u,             // c_mtime
      (uint)data.Length, // c_filesize
      0u,             // c_devmajor
      0u,             // c_devminor
      0u,             // c_rdevmajor
      0u,             // c_rdevminor
      (uint)nameSize, // c_namesize
      0u              // c_check
    );

    var headerBytes = Encoding.ASCII.GetBytes(header);
    this._stream.Write(headerBytes);
    this._stream.Write(nameBytes);

    // Pad to 4-byte boundary after header + name
    var headerPlusName = CpioConstants.NewAsciiHeaderSize + nameSize;
    var namePadding = (4 - (headerPlusName % 4)) % 4;
    for (var i = 0; i < namePadding; ++i)
      this._stream.WriteByte(0);

    // Write data
    if (data.Length > 0) {
      this._stream.Write(data);

      // Pad to 4-byte boundary after data
      var dataPadding = (4 - (data.Length % 4)) % 4;
      for (var i = 0; i < dataPadding; ++i)
        this._stream.WriteByte(0);
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._finished)
        Finish();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
