using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Cpio;

/// <summary>
/// Creates a cpio archive in any of the four historical header variants:
/// 7th Edition binary (either byte order), POSIX portable ASCII ("odc"),
/// SVR4 new ASCII and SVR4 CRC. Defaults to SVR4 new ASCII, the variant every
/// modern producer writes.
/// </summary>
public sealed class CpioWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly CpioArchiveFormat _format;
  private bool _finished;
  private bool _disposed;
  private uint _nextInode = 1;

  /// <summary>
  /// Initializes a new <see cref="CpioWriter"/> writing the SVR4 new ASCII variant.
  /// </summary>
  /// <param name="stream">The stream to write the cpio archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public CpioWriter(Stream stream, bool leaveOpen = false)
    : this(stream, CpioArchiveFormat.NewAscii, leaveOpen) { }

  /// <summary>
  /// Initializes a new <see cref="CpioWriter"/> writing the requested variant.
  /// </summary>
  /// <param name="stream">The stream to write the cpio archive to.</param>
  /// <param name="format">The on-disk header variant to emit.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public CpioWriter(Stream stream, CpioArchiveFormat format, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._format = format;
    this._leaveOpen = leaveOpen;
    _ = CpioLayout.HeaderSize(format); // rejects an out-of-range enum value up front
  }

  /// <summary>Gets the on-disk variant this writer emits.</summary>
  public CpioArchiveFormat Format => this._format;

  /// <summary>
  /// Adds a file entry.
  /// </summary>
  /// <param name="name">The file name.</param>
  /// <param name="data">The file data.</param>
  /// <param name="mode">The file mode. Defaults to regular file with 0644 permissions.</param>
  public void AddFile(string name, ReadOnlySpan<byte> data, uint mode = 0x81A4) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    this.WriteEntry(name, data, mode);
  }

  /// <summary>
  /// Adds a file entry whose payload is streamed from <paramref name="data"/>
  /// in bounded 64 KB chunks rather than buffered into RAM. Every cpio variant
  /// encodes the file size before the payload, so the pre-known
  /// <paramref name="size"/> is written into the header, then exactly
  /// <paramref name="size"/> bytes are copied, then the variant's alignment pad.
  /// </summary>
  /// <remarks>
  /// Produces byte-identical output to <see cref="AddFile(string, ReadOnlySpan{byte}, uint)"/>
  /// for the same name/size/payload. Peak memory is the 64 KB copy buffer
  /// regardless of <paramref name="size"/> — except for
  /// <see cref="CpioArchiveFormat.NewCrc"/>, whose checksum has to precede the
  /// payload and therefore needs a seekable source to pre-scan.
  /// </remarks>
  /// <param name="name">The file name.</param>
  /// <param name="size">The entry's logical byte size.</param>
  /// <param name="data">The source stream supplying exactly <paramref name="size"/> bytes.</param>
  /// <param name="mode">The file mode. Defaults to regular file with 0644 permissions.</param>
  public void AddStreamingFile(string name, long size, Stream data, uint mode = 0x81A4) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(data);
    ArgumentOutOfRangeException.ThrowIfNegative(size);

    var checksum = this._format == CpioArchiveFormat.NewCrc ? PreScanChecksum(data, size, name) : 0u;
    this.WriteEntryHeader(name, size, mode, checksum);

    var remaining = size;
    if (remaining > 0) {
      var buffer = new byte[64 * 1024];
      while (remaining > 0) {
        var toRead = (int)Math.Min(buffer.Length, remaining);
        var read = data.Read(buffer, 0, toRead);
        if (read <= 0)
          throw new EndOfStreamException(
            $"CPIO streaming entry '{name}': source ended {remaining} bytes short of the declared size {size}.");
        this._stream.Write(buffer, 0, read);
        remaining -= read;
      }
    }

    this.WritePadding(CpioLayout.DataPadding(this._format, size));
  }

  /// <summary>
  /// Adds a directory entry.
  /// </summary>
  /// <param name="name">The directory name.</param>
  /// <param name="mode">The directory mode. Defaults to directory with 0755 permissions.</param>
  public void AddDirectory(string name, uint mode = 0x41ED) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    this.WriteEntry(name, [], mode);
  }

  /// <summary>
  /// Writes the trailer and finishes the archive.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    this.WriteEntry(CpioConstants.Trailer, [], 0);
  }

  private void WriteEntry(string name, ReadOnlySpan<byte> data, uint mode) {
    var checksum = this._format == CpioArchiveFormat.NewCrc ? Checksum(data) : 0u;
    this.WriteEntryHeader(name, data.Length, mode, checksum);

    if (data.Length > 0)
      this._stream.Write(data);

    this.WritePadding(CpioLayout.DataPadding(this._format, data.Length));
  }

  /// <summary>
  /// Writes the fixed header, the NUL-terminated name, and the post-name
  /// alignment padding — the shared header path for both the buffered
  /// <see cref="WriteEntry"/> and the streaming <see cref="AddStreamingFile"/>.
  /// </summary>
  private void WriteEntryHeader(string name, long fileSize, uint mode, uint checksum) {
    ArgumentNullException.ThrowIfNull(name);

    var nameBytes = Encoding.ASCII.GetBytes(name + '\0');
    var inode = name == CpioConstants.Trailer ? 0u : ++this._nextInode;

    switch (this._format) {
      case CpioArchiveFormat.NewAscii:
      case CpioArchiveFormat.NewCrc:
        this.WriteNewAsciiHeader(nameBytes, inode, mode, fileSize, checksum);
        break;
      case CpioArchiveFormat.PortableAscii:
        this.WritePortableAsciiHeader(nameBytes, inode, mode, fileSize);
        break;
      default:
        this.WriteBinaryHeader(nameBytes, inode, mode, fileSize);
        break;
    }

    this._stream.Write(nameBytes);
    this.WritePadding(CpioLayout.NamePadding(this._format, nameBytes.Length));
  }

  private void WriteNewAsciiHeader(byte[] nameBytes, uint inode, uint mode, long fileSize, uint checksum) {
    if (fileSize > uint.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(fileSize),
        $"The SVR4 cpio header cannot carry a {fileSize}-byte entry; its size field is 8 hexadecimal digits.");

    var header = string.Format(
      CultureInfo.InvariantCulture,
      "{0}{1:X8}{2:X8}{3:X8}{4:X8}{5:X8}{6:X8}{7:X8}{8:X8}{9:X8}{10:X8}{11:X8}{12:X8}{13:X8}",
      this._format == CpioArchiveFormat.NewCrc ? CpioConstants.NewCrcMagic : CpioConstants.NewAsciiMagic,
      inode,                   // c_ino
      mode,                    // c_mode
      0u,                      // c_uid
      0u,                      // c_gid
      1u,                      // c_nlink
      0u,                      // c_mtime
      (uint)fileSize,          // c_filesize
      0u,                      // c_devmajor
      0u,                      // c_devminor
      0u,                      // c_rdevmajor
      0u,                      // c_rdevminor
      (uint)nameBytes.Length,  // c_namesize
      checksum                 // c_check
    );

    this._stream.Write(Encoding.ASCII.GetBytes(header));
  }

  private void WritePortableAsciiHeader(byte[] nameBytes, uint inode, uint mode, long fileSize) {
    if (fileSize > CpioConstants.PortableAsciiMaxLongField)
      throw new ArgumentOutOfRangeException(nameof(fileSize),
        $"The odc cpio header cannot carry a {fileSize}-byte entry; its size field is 11 octal digits.");
    if (nameBytes.Length > CpioConstants.PortableAsciiMaxShortField)
      throw new ArgumentOutOfRangeException(nameof(nameBytes),
        $"The odc cpio header cannot carry a {nameBytes.Length}-byte pathname; its length field is 6 octal digits.");

    var header = string.Concat(
      CpioConstants.PortableAsciiMagic,
      Octal(0, 6),                          // c_dev
      Octal(inode & CpioConstants.PortableAsciiMaxShortField, 6), // c_ino
      Octal(mode & CpioConstants.PortableAsciiMaxShortField, 6),  // c_mode
      Octal(0, 6),                          // c_uid
      Octal(0, 6),                          // c_gid
      Octal(1, 6),                          // c_nlink
      Octal(0, 6),                          // c_rdev
      Octal(0, 11),                         // c_mtime
      Octal((ulong)nameBytes.Length, 6),    // c_namesize
      Octal((ulong)fileSize, 11)            // c_filesize
    );

    this._stream.Write(Encoding.ASCII.GetBytes(header));
  }

  private void WriteBinaryHeader(byte[] nameBytes, uint inode, uint mode, long fileSize) {
    if (fileSize > uint.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(fileSize),
        $"The binary cpio header cannot carry a {fileSize}-byte entry; its size field is 32 bits.");
    if (nameBytes.Length > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(nameBytes),
        $"The binary cpio header cannot carry a {nameBytes.Length}-byte pathname; its length field is 16 bits.");

    var le = this._format == CpioArchiveFormat.BinaryLittleEndian;
    Span<byte> header = stackalloc byte[CpioConstants.BinaryHeaderSize];

    // Written through static helpers rather than local functions: a local
    // function may not capture the stack-allocated span.
    PutWord(header, 0, CpioConstants.BinaryMagic, le);
    PutWord(header, 2, 0, le);                              // c_dev
    PutWord(header, 4, (ushort)inode, le);                  // c_ino — 16 bits; synthetic, so wrapping is harmless
    PutWord(header, 6, (ushort)mode, le);                   // c_mode
    PutWord(header, 8, 0, le);                              // c_uid
    PutWord(header, 10, 0, le);                             // c_gid
    PutWord(header, 12, 1, le);                             // c_nlink
    PutWord(header, 14, 0, le);                             // c_rdev
    PutLong(header, 16, 0, le);                             // c_mtime
    PutWord(header, 20, (ushort)nameBytes.Length, le);      // c_namesize
    PutLong(header, 22, (uint)fileSize, le);                // c_filesize

    this._stream.Write(header);
  }

  private static void PutWord(Span<byte> header, int offset, ushort value, bool littleEndian) {
    if (littleEndian)
      BinaryPrimitives.WriteUInt16LittleEndian(header[offset..], value);
    else
      BinaryPrimitives.WriteUInt16BigEndian(header[offset..], value);
  }

  /// <summary>Most significant 16-bit word first, each word in the chosen byte order.</summary>
  private static void PutLong(Span<byte> header, int offset, uint value, bool littleEndian) {
    PutWord(header, offset, (ushort)(value >> 16), littleEndian);
    PutWord(header, offset + 2, (ushort)value, littleEndian);
  }

  private static uint Checksum(ReadOnlySpan<byte> data) {
    uint sum = 0;
    foreach (var value in data)
      sum = unchecked(sum + value);
    return sum;
  }

  /// <summary>
  /// Sums the payload without consuming it, so the CRC variant's check field can
  /// be written ahead of bytes the caller has not handed over yet.
  /// </summary>
  private static uint PreScanChecksum(Stream data, long size, string name) {
    if (size == 0)
      return 0;
    if (!data.CanSeek)
      throw new NotSupportedException(
        $"CPIO streaming entry '{name}': the CRC variant stores its checksum ahead of the payload, so the source stream must be seekable.");

    var origin = data.Position;
    try {
      uint sum = 0;
      var buffer = new byte[64 * 1024];
      var remaining = size;
      while (remaining > 0) {
        var toRead = (int)Math.Min(buffer.Length, remaining);
        var read = data.Read(buffer, 0, toRead);
        if (read <= 0)
          throw new EndOfStreamException(
            $"CPIO streaming entry '{name}': source ended {remaining} bytes short of the declared size {size}.");
        for (var i = 0; i < read; ++i)
          sum = unchecked(sum + buffer[i]);
        remaining -= read;
      }
      return sum;
    } finally {
      data.Position = origin;
    }
  }

  private void WritePadding(int count) {
    for (var i = 0; i < count; ++i)
      this._stream.WriteByte(0);
  }

  private static string Octal(ulong value, int width) {
    var text = Convert.ToString((long)value, 8);
    return text.Length > width
      ? throw new ArgumentOutOfRangeException(nameof(value), $"Value {value} does not fit a {width}-digit octal cpio field.")
      : text.PadLeft(width, '0');
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;

    this._disposed = true;
    if (!this._finished)
      this.Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
