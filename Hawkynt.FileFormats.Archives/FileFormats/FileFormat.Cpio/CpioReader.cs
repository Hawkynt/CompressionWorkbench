using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Cpio;

/// <summary>
/// Reads entries from SVR4 newc/crc, POSIX portable-ASCII (odc), 7th Edition
/// binary CPIO, and PWB/UNIX binary CPIO archives.
/// </summary>
public sealed class CpioReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _assumePwbBinary;
  private bool _disposed;
  private CpioEntry? _current;

  /// <summary>
  /// Initializes a new <see cref="CpioReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the cpio archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="assumePwbBinary">
  /// Interpret little-endian binary headers using PWB/UNIX mode semantics.
  /// PWB and 7th Edition little-endian CPIO have the same physical header layout,
  /// so this explicit override is the only unambiguous choice for archives whose
  /// inode modes do not contain a distinctive old PWB flag combination.
  /// </param>
  public CpioReader(Stream stream, bool leaveOpen = false, bool assumePwbBinary = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._assumePwbBinary = assumePwbBinary;
  }

  /// <summary>
  /// Reads all entries from the archive.
  /// </summary>
  /// <returns>A list of entries with their associated data.</returns>
  public List<(CpioEntry Entry, byte[] Data)> ReadAll() {
    var result = new List<(CpioEntry, byte[])>();

    while (true) {
      var entry = this.ReadEntry(out var data);
      if (entry == null)
        break;
      result.Add((entry, data));
    }

    return result;
  }

  /// <summary>
  /// Reads the next entry from the archive.
  /// </summary>
  /// <param name="data">The entry's file data.</param>
  /// <returns>The entry, or null if the trailer was reached.</returns>
  public CpioEntry? ReadEntry(out byte[] data) {
    data = [];
    var entry = this.ReadNextHeader();
    if (entry == null)
      return null;
    if (entry.FileSize > int.MaxValue)
      throw new NotSupportedException("Use ReadNextHeader()/CopyCurrentEntryData() for CPIO entries larger than 2 GiB.");

    using var output = new MemoryStream((int)entry.FileSize);
    this.CopyCurrentEntryData(output);
    data = output.ToArray();
    return entry;
  }

  /// <summary>
  /// Reads the next entry's header, leaving the stream positioned at its data.
  /// Returns null at the trailer. Pair with <see cref="CopyCurrentEntryData" />,
  /// which must be called before the next header even for skipped entries so the
  /// reader stays aligned.
  /// </summary>
  /// <remarks>
  /// The <c>out byte[]</c> overload cannot carry an entry larger than an array;
  /// this pair can.
  /// </remarks>
  public CpioEntry? ReadNextHeader() => this._current = this.ReadEntryHeaderOnly();

  /// <summary>
  /// Copies the current entry's data to <paramref name="destination" /> (or discards
  /// it when null), validates an SVR4 CRC entry's additive checksum, and consumes
  /// the variant-specific alignment padding.
  /// </summary>
  public void CopyCurrentEntryData(Stream? destination) {
    if (this._current is not { } entry)
      return;

    var remaining = entry.FileSize;
    uint checksum = 0;
    if (remaining > 0) {
      var buffer = new byte[64 * 1024];
      while (remaining > 0) {
        var want = (int)Math.Min(buffer.Length, remaining);
        var read = this._stream.Read(buffer, 0, want);
        if (read <= 0)
          throw new EndOfStreamException($"CPIO entry '{entry.Name}' ended {remaining} bytes before its declared size.");

        if (entry.Format == CpioArchiveFormat.NewCrc)
          for (var i = 0; i < read; ++i)
            checksum = unchecked(checksum + buffer[i]);

        destination?.Write(buffer, 0, read);
        remaining -= read;
      }
    }

    if (entry.Format == CpioArchiveFormat.NewCrc && checksum != entry.Checksum)
      throw new InvalidDataException(
        $"CPIO entry '{entry.Name}' checksum mismatch: stored 0x{entry.Checksum:X8}, computed 0x{checksum:X8}.");

    this.Skip(GetDataPadding(entry.Format, entry.FileSize));
    this._current = null;
  }

  private CpioEntry? ReadEntryHeaderOnly() {
    Span<byte> prefix = stackalloc byte[6];
    var prefixRead = this.ReadExact(prefix);
    if (prefixRead == 0)
      return null;
    if (prefixRead != prefix.Length)
      throw new EndOfStreamException("Truncated CPIO header.");

    CpioEntry entry;
    int nameSize;
    int headerSize;

    if (prefix.SequenceEqual("070701"u8)) {
      (entry, nameSize) = this.ReadNewAsciiHeader(prefix, CpioArchiveFormat.NewAscii);
      headerSize = CpioConstants.NewAsciiHeaderSize;
    } else if (prefix.SequenceEqual("070702"u8)) {
      (entry, nameSize) = this.ReadNewAsciiHeader(prefix, CpioArchiveFormat.NewCrc);
      headerSize = CpioConstants.NewAsciiHeaderSize;
    } else if (prefix.SequenceEqual("070707"u8)) {
      (entry, nameSize) = this.ReadPortableAsciiHeader(prefix);
      headerSize = CpioConstants.PortableAsciiHeaderSize;
    } else if (IsBinaryMagic(prefix[..2], out var littleEndian)) {
      (entry, nameSize) = this.ReadBinaryHeader(prefix, littleEndian);
      headerSize = CpioConstants.BinaryHeaderSize;
    } else {
      var printable = Encoding.ASCII.GetString(prefix);
      throw new InvalidDataException(
        $"Invalid cpio magic: {Convert.ToHexString(prefix[..2])} / '{printable}'.");
    }

    if (nameSize <= 0)
      throw new InvalidDataException("CPIO entry has an invalid zero-length pathname field.");

    var nameBytes = new byte[nameSize];
    this.ReadExactly(nameBytes, $"CPIO entry pathname ({nameSize} bytes)");
    if (nameBytes[^1] != 0)
      throw new InvalidDataException("CPIO pathname is not NUL-terminated.");
    entry.Name = Encoding.ASCII.GetString(nameBytes, 0, nameBytes.Length - 1);

    this.Skip(GetNamePadding(entry.Format, headerSize, nameSize));
    return entry.Name == CpioConstants.Trailer ? null : entry;
  }

  private (CpioEntry Entry, int NameSize) ReadNewAsciiHeader(
    ReadOnlySpan<byte> prefix,
    CpioArchiveFormat format
  ) {
    var header = new byte[CpioConstants.NewAsciiHeaderSize];
    prefix.CopyTo(header);
    this.ReadExactly(header.AsSpan(prefix.Length), "SVR4 CPIO header");

    var entry = new CpioEntry {
      Format = format,
      Inode = ParseHexUInt32(header.AsSpan(6, 8)),
      Mode = ParseHexUInt32(header.AsSpan(14, 8)),
      Uid = ParseHexUInt32(header.AsSpan(22, 8)),
      Gid = ParseHexUInt32(header.AsSpan(30, 8)),
      NumLinks = ParseHexUInt32(header.AsSpan(38, 8)),
      ModificationTime = ParseHexUInt32(header.AsSpan(46, 8)),
      FileSize = ParseHexUInt32(header.AsSpan(54, 8)),
      DevMajor = ParseHexUInt32(header.AsSpan(62, 8)),
      DevMinor = ParseHexUInt32(header.AsSpan(70, 8)),
      RDevMajor = ParseHexUInt32(header.AsSpan(78, 8)),
      RDevMinor = ParseHexUInt32(header.AsSpan(86, 8)),
      Checksum = ParseHexUInt32(header.AsSpan(102, 8)),
    };
    var nameSize = checked((int)ParseHexUInt32(header.AsSpan(94, 8)));
    return (entry, nameSize);
  }

  private (CpioEntry Entry, int NameSize) ReadPortableAsciiHeader(ReadOnlySpan<byte> prefix) {
    var header = new byte[CpioConstants.PortableAsciiHeaderSize];
    prefix.CopyTo(header);
    this.ReadExactly(header.AsSpan(prefix.Length), "portable-ASCII CPIO header");

    var device = ParseOctalUInt32(header.AsSpan(6, 6));
    var rDevice = ParseOctalUInt32(header.AsSpan(42, 6));
    var entry = new CpioEntry {
      Format = CpioArchiveFormat.PortableAscii,
      Inode = ParseOctalUInt32(header.AsSpan(12, 6)),
      Mode = ParseOctalUInt32(header.AsSpan(18, 6)),
      Uid = ParseOctalUInt32(header.AsSpan(24, 6)),
      Gid = ParseOctalUInt32(header.AsSpan(30, 6)),
      NumLinks = ParseOctalUInt32(header.AsSpan(36, 6)),
      ModificationTime = checked((uint)ParseOctalUInt64(header.AsSpan(48, 11))),
      FileSize = checked((long)ParseOctalUInt64(header.AsSpan(65, 11))),
      DevMajor = device >> 8,
      DevMinor = device & 0xFF,
      RDevMajor = rDevice >> 8,
      RDevMinor = rDevice & 0xFF,
    };
    var nameSize = checked((int)ParseOctalUInt32(header.AsSpan(59, 6)));
    return (entry, nameSize);
  }

  private (CpioEntry Entry, int NameSize) ReadBinaryHeader(ReadOnlySpan<byte> prefix, bool littleEndian) {
    var header = new byte[CpioConstants.BinaryHeaderSize];
    prefix.CopyTo(header);
    this.ReadExactly(header.AsSpan(prefix.Length), "binary CPIO header");

    ushort ReadWord(int offset) => littleEndian
      ? BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(offset, 2))
      : BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(offset, 2));
    uint ReadLong(int offset) => ((uint)ReadWord(offset) << 16) | ReadWord(offset + 2);

    var device = ReadWord(2);
    var rawMode = ReadWord(6);
    var numLinks = ReadWord(12);
    var rDevice = ReadWord(14);
    var fileSize = ReadLong(22);
    var format = littleEndian ? CpioArchiveFormat.BinaryLittleEndian : CpioArchiveFormat.BinaryBigEndian;
    var mode = (uint)rawMode;

    if (littleEndian && (this._assumePwbBinary || LooksLikePwbMode(rawMode, numLinks, rDevice, fileSize))) {
      this._assumePwbBinary = true;
      format = CpioArchiveFormat.PwbBinary;
      mode = NormalizePwbMode(rawMode);
    }

    var entry = new CpioEntry {
      Format = format,
      Inode = ReadWord(4),
      Mode = mode,
      Uid = ReadWord(8),
      Gid = ReadWord(10),
      NumLinks = numLinks,
      ModificationTime = ReadLong(16),
      FileSize = fileSize,
      DevMajor = (uint)device >> 8,
      DevMinor = (uint)device & 0xFF,
      RDevMajor = (uint)rDevice >> 8,
      RDevMinor = (uint)rDevice & 0xFF,
    };
    return (entry, ReadWord(20));
  }

  /// <summary>
  /// PWB and 7th Edition little-endian CPIO are byte-layout identical. Auto
  /// detection is therefore deliberately conservative: only old inode modes
  /// that are invalid or strongly implausible under 7th Edition semantics cause
  /// the reader to switch the remainder of the archive to PWB interpretation.
  /// </summary>
  private static bool LooksLikePwbMode(ushort mode, ushort numLinks, ushort rDevice, uint fileSize) {
    var type = mode & 0xF000;
    var isKnownV7Type = type is 0x1000 or 0x2000 or 0x4000 or 0x6000 or 0x8000 or 0xA000 or 0xC000;
    if (!isKnownV7Type)
      return true;

    // PWB directories frequently carried the old IALLOC bit, yielding 014xxxx,
    // which V7 would call a socket. Directory link counts are normally >= 2.
    if (type == 0xC000 && numLinks >= 2)
      return true;

    // IALLOC + PWB character-device type yields 012xxxx, a V7 symlink. A real
    // symlink has no rdev, while a character device normally does.
    return type == 0xA000 && rDevice != 0 && fileSize == 0;
  }

  /// <summary>
  /// Drops PWB/V6 inode-only IALLOC and ILARG bits, then maps the PWB regular-file
  /// representation (no remaining type bits) to the modern regular-file type.
  /// This is the same public behavior expected from a PWB-aware cpio reader.
  /// </summary>
  private static uint NormalizePwbMode(ushort mode) {
    var normalized = (uint)(mode & 0x6FFF); // octal 067777: clear IALLOC (0100000) + ILARG (0010000)
    if ((normalized & 0xF000) == 0)
      normalized |= 0x8000;
    return normalized;
  }

  private static bool IsBinaryMagic(ReadOnlySpan<byte> bytes, out bool littleEndian) {
    if (BinaryPrimitives.ReadUInt16LittleEndian(bytes) == CpioConstants.BinaryMagic) {
      littleEndian = true;
      return true;
    }
    if (BinaryPrimitives.ReadUInt16BigEndian(bytes) == CpioConstants.BinaryMagic) {
      littleEndian = false;
      return true;
    }
    littleEndian = false;
    return false;
  }

  private static int GetNamePadding(CpioArchiveFormat format, int headerSize, int nameSize) => format switch {
    CpioArchiveFormat.NewAscii or CpioArchiveFormat.NewCrc => Padding(headerSize + nameSize, 4),
    CpioArchiveFormat.BinaryLittleEndian or CpioArchiveFormat.BinaryBigEndian or CpioArchiveFormat.PwbBinary
      => Padding(headerSize + nameSize, 2),
    _ => 0,
  };

  private static int GetDataPadding(CpioArchiveFormat format, long fileSize) => format switch {
    CpioArchiveFormat.NewAscii or CpioArchiveFormat.NewCrc => Padding(fileSize, 4),
    CpioArchiveFormat.BinaryLittleEndian or CpioArchiveFormat.BinaryBigEndian or CpioArchiveFormat.PwbBinary
      => Padding(fileSize, 2),
    _ => 0,
  };

  private static int Padding(long length, int alignment) => (int)((alignment - length % alignment) % alignment);

  private static uint ParseHexUInt32(ReadOnlySpan<byte> value)
    => uint.Parse(Encoding.ASCII.GetString(value), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

  private static uint ParseOctalUInt32(ReadOnlySpan<byte> value)
    => checked((uint)ParseOctalUInt64(value));

  private static ulong ParseOctalUInt64(ReadOnlySpan<byte> value) {
    ulong result = 0;
    foreach (var b in value) {
      if (b is < (byte)'0' or > (byte)'7')
        throw new InvalidDataException($"Invalid octal digit 0x{b:X2} in CPIO header.");
      result = checked((result << 3) | (uint)(b - (byte)'0'));
    }
    return result;
  }

  private int ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        break;
      totalRead += read;
    }
    return totalRead;
  }

  private void ReadExactly(Span<byte> buffer, string what) {
    var read = this.ReadExact(buffer);
    if (read != buffer.Length)
      throw new EndOfStreamException($"Truncated {what}: expected {buffer.Length} bytes, got {read}.");
  }

  private void Skip(int count) {
    if (count <= 0)
      return;
    if (this._stream.CanSeek) {
      if (this._stream.Position > this._stream.Length - count)
        throw new EndOfStreamException($"Truncated CPIO padding: expected {count} bytes.");
      this._stream.Position += count;
      return;
    }

    Span<byte> buffer = stackalloc byte[4];
    this.ReadExactly(buffer[..count], "CPIO alignment padding");
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
