using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Cpio;

/// <summary>
/// Creates SVR4 newc/crc, POSIX portable-ASCII (odc), and 7th Edition binary CPIO archives.
/// </summary>
public sealed class CpioWriter : IDisposable {
  private const long PortableAsciiMaxFileSize = 0x1FFFFFFFFL;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly CpioArchiveFormat _format;
  private bool _finished;
  private bool _disposed;
  private uint _nextInode = 1;

  /// <summary>
  /// Initializes a new <see cref="CpioWriter"/> using the SVR4 new ASCII format.
  /// </summary>
  /// <param name="stream">The stream to write the cpio archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public CpioWriter(Stream stream, bool leaveOpen = false)
    : this(stream, CpioArchiveFormat.NewAscii, leaveOpen) { }

  /// <summary>
  /// Initializes a new <see cref="CpioWriter"/> using the requested on-disk variant.
  /// </summary>
  /// <param name="stream">The stream to write the cpio archive to.</param>
  /// <param name="format">The CPIO header variant to write.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public CpioWriter(Stream stream, CpioArchiveFormat format, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._format = format;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Gets the on-disk CPIO variant emitted by this writer.</summary>
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
  /// in bounded 64 KB chunks rather than buffered into RAM.
  /// </summary>
  /// <param name="name">The file name.</param>
  /// <param name="size">The entry's logical byte size.</param>
  /// <param name="data">The source stream supplying exactly <paramref name="size"/> bytes.</param>
  /// <param name="mode">The file mode. Defaults to regular file with 0644 permissions.</param>
  public void AddStreamingFile(string name, long size, Stream data, uint mode = 0x81A4) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(data);
    this.ValidateEntrySize(size);

    var checksum = this._format == CpioArchiveFormat.NewCrc
      ? ComputeStreamingChecksum(data, size, name)
      : 0u;
    this.WriteEntryHeader(name, size, mode, checksum);
    this.CopyPayload(data, size, name);
    this.WritePadding(GetDataPadding(this._format, size));
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
    var checksum = this._format == CpioArchiveFormat.NewCrc ? ComputeChecksum(data) : 0u;
    this.WriteEntryHeader(name, data.Length, mode, checksum);
    this._stream.Write(data);
    this.WritePadding(GetDataPadding(this._format, data.Length));
  }

  private void WriteEntryHeader(string name, long fileSize, uint mode, uint checksum) {
    ArgumentNullException.ThrowIfNull(name);
    this.ValidateEntrySize(fileSize);

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
      case CpioArchiveFormat.BinaryLittleEndian:
      case CpioArchiveFormat.BinaryBigEndian:
        this.WriteBinaryHeader(nameBytes, inode, mode, fileSize);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(this._format), this._format, "Unsupported CPIO format.");
    }
  }

  private void WriteNewAsciiHeader(ReadOnlySpan<byte> nameBytes, uint inode, uint mode, long fileSize, uint checksum) {
    if (nameBytes.Length > uint.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(nameBytes), "CPIO pathname is too large for newc.");

    var magic = this._format == CpioArchiveFormat.NewCrc
      ? CpioConstants.NewCrcMagic
      : CpioConstants.NewAsciiMagic;
    var header = string.Format(
      CultureInfo.InvariantCulture,
      "{0}{1:X8}{2:X8}{3:X8}{4:X8}{5:X8}{6:X8}{7:X8}{8:X8}{9:X8}{10:X8}{11:X8}{12:X8}{13:X8}",
      magic,
      inode,
      mode,
      0u,
      0u,
      1u,
      0u,
      checked((uint)fileSize),
      0u,
      0u,
      0u,
      0u,
      (uint)nameBytes.Length,
      checksum);

    this._stream.Write(Encoding.ASCII.GetBytes(header));
    this._stream.Write(nameBytes);
    this.WritePadding(Padding(CpioConstants.NewAsciiHeaderSize + nameBytes.Length, 4));
  }

  private void WritePortableAsciiHeader(ReadOnlySpan<byte> nameBytes, uint inode, uint mode, long fileSize) {
    const uint maxSixOctalDigits = 0x3FFFF;
    if (inode > maxSixOctalDigits || mode > maxSixOctalDigits || nameBytes.Length > maxSixOctalDigits)
      throw new ArgumentOutOfRangeException(nameof(inode), "CPIO odc 6-digit octal field overflow.");

    var header = string.Concat(
      CpioConstants.PortableAsciiMagic,
      ToOctal(0, 6),
      ToOctal(inode, 6),
      ToOctal(mode, 6),
      ToOctal(0, 6),
      ToOctal(0, 6),
      ToOctal(1, 6),
      ToOctal(0, 6),
      ToOctal(0, 11),
      ToOctal((ulong)nameBytes.Length, 6),
      ToOctal((ulong)fileSize, 11));

    this._stream.Write(Encoding.ASCII.GetBytes(header));
    this._stream.Write(nameBytes);
  }

  private void WriteBinaryHeader(ReadOnlySpan<byte> nameBytes, uint inode, uint mode, long fileSize) {
    if (inode > ushort.MaxValue || mode > ushort.MaxValue || nameBytes.Length > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(inode), "Binary CPIO 16-bit field overflow.");

    Span<byte> header = stackalloc byte[CpioConstants.BinaryHeaderSize];
    var littleEndian = this._format == CpioArchiveFormat.BinaryLittleEndian;

    void WriteWord(int offset, ushort value) {
      if (littleEndian)
        BinaryPrimitives.WriteUInt16LittleEndian(header[offset..], value);
      else
        BinaryPrimitives.WriteUInt16BigEndian(header[offset..], value);
    }

    void WriteLong(int offset, uint value) {
      WriteWord(offset, (ushort)(value >> 16));
      WriteWord(offset + 2, (ushort)value);
    }

    WriteWord(0, CpioConstants.BinaryMagic);
    WriteWord(2, 0);
    WriteWord(4, checked((ushort)inode));
    WriteWord(6, checked((ushort)mode));
    WriteWord(8, 0);
    WriteWord(10, 0);
    WriteWord(12, 1);
    WriteWord(14, 0);
    WriteLong(16, 0);
    WriteWord(20, checked((ushort)nameBytes.Length));
    WriteLong(22, checked((uint)fileSize));

    this._stream.Write(header);
    this._stream.Write(nameBytes);
    this.WritePadding(Padding(CpioConstants.BinaryHeaderSize + nameBytes.Length, 2));
  }

  private void ValidateEntrySize(long fileSize) {
    if (fileSize < 0)
      throw new ArgumentOutOfRangeException(nameof(fileSize));

    var maximum = this._format switch {
      CpioArchiveFormat.PortableAscii => PortableAsciiMaxFileSize,
      CpioArchiveFormat.BinaryLittleEndian or CpioArchiveFormat.BinaryBigEndian => int.MaxValue,
      _ => uint.MaxValue,
    };
    if (fileSize > maximum)
      throw new ArgumentOutOfRangeException(nameof(fileSize),
        $"{this._format} cannot represent a {fileSize}-byte CPIO entry (maximum {maximum}).");
  }

  private void CopyPayload(Stream data, long size, string name) {
    var buffer = new byte[64 * 1024];
    var remaining = size;
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

  private static uint ComputeStreamingChecksum(Stream data, long size, string name) {
    if (!data.CanSeek)
      throw new NotSupportedException("Streaming SVR4 CRC output requires a seekable source so the checksum can precede the payload.");

    var originalPosition = data.Position;
    try {
      uint checksum = 0;
      var buffer = new byte[64 * 1024];
      var remaining = size;
      while (remaining > 0) {
        var toRead = (int)Math.Min(buffer.Length, remaining);
        var read = data.Read(buffer, 0, toRead);
        if (read <= 0)
          throw new EndOfStreamException(
            $"CPIO streaming entry '{name}': source ended {remaining} bytes short of the declared size {size}.");
        for (var i = 0; i < read; ++i)
          checksum = unchecked(checksum + buffer[i]);
        remaining -= read;
      }
      return checksum;
    } finally {
      data.Position = originalPosition;
    }
  }

  private static uint ComputeChecksum(ReadOnlySpan<byte> data) {
    uint checksum = 0;
    foreach (var value in data)
      checksum = unchecked(checksum + value);
    return checksum;
  }

  private void WritePadding(int count) {
    for (var i = 0; i < count; ++i)
      this._stream.WriteByte(0);
  }

  private static int GetDataPadding(CpioArchiveFormat format, long fileSize) => format switch {
    CpioArchiveFormat.NewAscii or CpioArchiveFormat.NewCrc => Padding(fileSize, 4),
    CpioArchiveFormat.BinaryLittleEndian or CpioArchiveFormat.BinaryBigEndian => Padding(fileSize, 2),
    _ => 0,
  };

  private static int Padding(long length, int alignment) => (int)((alignment - length % alignment) % alignment);

  private static string ToOctal(ulong value, int width) {
    var text = Convert.ToString(checked((long)value), 8);
    if (text.Length > width)
      throw new ArgumentOutOfRangeException(nameof(value), $"Value {value} does not fit a {width}-digit octal CPIO field.");
    return text.PadLeft(width, '0');
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
