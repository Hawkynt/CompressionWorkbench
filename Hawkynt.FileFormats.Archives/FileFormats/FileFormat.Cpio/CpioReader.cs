using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Cpio;

/// <summary>
/// Reads entries from a cpio archive in any of the four historical header
/// variants: 7th Edition binary (either byte order), POSIX portable ASCII
/// ("odc", <c>070707</c>), SVR4 new ASCII (<c>070701</c>) and SVR4 CRC
/// (<c>070702</c>).
/// </summary>
/// <remarks>
/// The variant is decided per entry from the magic the entry itself carries,
/// not once for the archive, because that is what the on-disk format actually
/// specifies and what every reference implementation does.
/// </remarks>
public sealed class CpioReader : IDisposable {

  /// <summary>
  /// Refuses a pathname field larger than this. Nothing legitimate comes near
  /// it, and without the cap a corrupt namesize turns into an allocation the
  /// size of whatever the four hex digits happened to say.
  /// </summary>
  private const int MaxPathNameSize = 1 << 20;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;
  private CpioEntry? _current;

  /// <summary>
  /// Initializes a new <see cref="CpioReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the cpio archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public CpioReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
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
      throw new NotSupportedException(
        $"CPIO entry '{entry.Name}' is {entry.FileSize} bytes; use ReadNextHeader()/CopyCurrentEntryData() for entries beyond 2 GiB.");

    var buffer = new byte[entry.FileSize];
    using (var sink = new MemoryStream(buffer, 0, buffer.Length, writable: true))
      this.CopyCurrentEntryData(sink);

    data = buffer;
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
  /// Identifies the header variant at <paramref name="archive"/>'s current
  /// position without consuming anything, or returns <see langword="null"/>
  /// when the bytes there are not a cpio header. Requires a seekable stream.
  /// </summary>
  public static CpioArchiveFormat? PeekFormat(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanSeek)
      throw new NotSupportedException("Peeking at a cpio header variant requires a seekable stream.");

    var origin = archive.Position;
    try {
      Span<byte> magic = stackalloc byte[6];
      var total = 0;
      while (total < magic.Length) {
        var read = archive.Read(magic[total..]);
        if (read <= 0)
          break;
        total += read;
      }

      if (total != magic.Length)
        return null;

      try {
        return IdentifyVariant(magic);
      } catch (InvalidDataException) {
        return null;
      }
    } finally {
      archive.Position = origin;
    }
  }

  /// <summary>
  /// Advances past the current entry's payload and alignment padding without
  /// reading the payload, for callers that only walk headers. Falls back to a
  /// buffered discard when the stream cannot seek.
  /// </summary>
  /// <remarks>
  /// Unlike <see cref="CopyCurrentEntryData" /> this deliberately does not
  /// verify a CRC entry's checksum — nothing has looked at the bytes.
  /// </remarks>
  public void SkipCurrentEntryData() {
    if (this._current is not { } entry)
      return;

    if (!this._stream.CanSeek) {
      this.CopyCurrentEntryData(null);
      return;
    }

    var span = entry.FileSize + CpioLayout.DataPadding(entry.Format, entry.FileSize);
    if (this._stream.Position + span > this._stream.Length)
      throw new EndOfStreamException(
        $"CPIO entry '{entry.Name}' claims {entry.FileSize} bytes but the archive ends first.");

    this._stream.Position += span;
    this._current = null;
  }

  /// <summary>
  /// Copies the current entry's data to <paramref name="destination" /> (or discards
  /// it when null), verifies an SVR4 CRC entry's payload sum, and consumes the
  /// alignment padding the entry's own variant calls for.
  /// </summary>
  public void CopyCurrentEntryData(Stream? destination) {
    if (this._current is not { } entry)
      return;

    uint checksum = 0;
    var remaining = entry.FileSize;
    if (remaining > 0) {
      var buffer = new byte[64 * 1024];
      while (remaining > 0) {
        var want = (int)Math.Min(buffer.Length, remaining);
        var read = this._stream.Read(buffer, 0, want);
        if (read <= 0)
          throw new EndOfStreamException(
            $"CPIO entry '{entry.Name}' ends {remaining} bytes before the {entry.FileSize} bytes its header declares.");

        if (entry.Format == CpioArchiveFormat.NewCrc)
          for (var i = 0; i < read; ++i)
            checksum = unchecked(checksum + buffer[i]);

        destination?.Write(buffer, 0, read);
        remaining -= read;
      }
    }

    // The "CRC" variant's check field is an unsigned byte sum, not a CRC-32.
    // Verifying it is the whole reason the variant exists.
    if (entry.Format == CpioArchiveFormat.NewCrc && checksum != entry.Checksum)
      throw new InvalidDataException(
        $"CPIO entry '{entry.Name}' fails its checksum: header says 0x{entry.Checksum:X8}, payload sums to 0x{checksum:X8}.");

    this.Skip(CpioLayout.DataPadding(entry.Format, entry.FileSize));
    this._current = null;
  }

  private CpioEntry? ReadEntryHeaderOnly() {
    Span<byte> magic = stackalloc byte[6];
    var magicRead = this.Read(magic);
    if (magicRead == 0)
      return null; // clean end of stream
    if (magicRead != magic.Length)
      throw new EndOfStreamException($"Truncated CPIO header: only {magicRead} of 6 magic bytes present.");

    var format = IdentifyVariant(magic);
    var (entry, nameSize) = format switch {
      CpioArchiveFormat.NewAscii or CpioArchiveFormat.NewCrc => this.ReadNewAsciiHeader(magic, format),
      CpioArchiveFormat.PortableAscii => this.ReadPortableAsciiHeader(magic),
      _ => this.ReadBinaryHeader(magic, format == CpioArchiveFormat.BinaryLittleEndian),
    };

    if (nameSize is <= 0 or > MaxPathNameSize)
      throw new InvalidDataException($"CPIO entry declares an implausible pathname length of {nameSize} bytes.");

    var nameBytes = new byte[nameSize];
    this.ReadExactly(nameBytes, $"CPIO pathname ({nameSize} bytes)");
    if (nameBytes[^1] != 0)
      throw new InvalidDataException("CPIO pathname is not NUL-terminated.");

    entry.Name = Encoding.ASCII.GetString(nameBytes, 0, nameSize - 1);
    this.Skip(CpioLayout.NamePadding(format, nameSize));

    return entry.Name == CpioConstants.Trailer ? null : entry;
  }

  /// <summary>
  /// Decides the variant from the leading bytes. The three ASCII magics come
  /// first because they are the specific ones; the binary magic is only a
  /// 16-bit word, and the byte order it appears in is what names the variant.
  /// The two sets cannot collide — an ASCII header starts <c>0x30 0x37</c>,
  /// which is neither byte order of <c>0x71C7</c>.
  /// </summary>
  private static CpioArchiveFormat IdentifyVariant(ReadOnlySpan<byte> magic) {
    if (magic.SequenceEqual("070701"u8)) return CpioArchiveFormat.NewAscii;
    if (magic.SequenceEqual("070702"u8)) return CpioArchiveFormat.NewCrc;
    if (magic.SequenceEqual("070707"u8)) return CpioArchiveFormat.PortableAscii;
    if (BinaryPrimitives.ReadUInt16LittleEndian(magic) == CpioConstants.BinaryMagic) return CpioArchiveFormat.BinaryLittleEndian;
    if (BinaryPrimitives.ReadUInt16BigEndian(magic) == CpioConstants.BinaryMagic) return CpioArchiveFormat.BinaryBigEndian;

    throw new InvalidDataException(
      $"Invalid cpio magic: {Convert.ToHexString(magic)} ('{Sanitize(magic)}').");
  }

  private static string Sanitize(ReadOnlySpan<byte> bytes) {
    Span<char> text = stackalloc char[bytes.Length];
    for (var i = 0; i < bytes.Length; ++i)
      text[i] = bytes[i] is >= 0x20 and < 0x7F ? (char)bytes[i] : '.';
    return new(text);
  }

  private (CpioEntry Entry, int NameSize) ReadNewAsciiHeader(ReadOnlySpan<byte> magic, CpioArchiveFormat format) {
    var header = this.ReadHeaderBody(magic, CpioConstants.NewAsciiHeaderSize, "SVR4 CPIO header");

    var entry = new CpioEntry {
      Format = format,
      Inode = ParseHex(header, 6),
      Mode = ParseHex(header, 14),
      Uid = ParseHex(header, 22),
      Gid = ParseHex(header, 30),
      NumLinks = ParseHex(header, 38),
      ModificationTime = ParseHex(header, 46),
      FileSize = ParseHex(header, 54),
      DevMajor = ParseHex(header, 62),
      DevMinor = ParseHex(header, 70),
      RDevMajor = ParseHex(header, 78),
      RDevMinor = ParseHex(header, 86),
      Checksum = ParseHex(header, 102),
    };

    return (entry, checked((int)ParseHex(header, 94)));
  }

  private (CpioEntry Entry, int NameSize) ReadPortableAsciiHeader(ReadOnlySpan<byte> magic) {
    var header = this.ReadHeaderBody(magic, CpioConstants.PortableAsciiHeaderSize, "portable-ASCII CPIO header");

    // odc packs dev and rdev as single numbers; the classic split is the one
    // mknod(2) used on the systems that wrote these archives.
    var device = ParseOctal(header, 6, 6);
    var rDevice = ParseOctal(header, 42, 6);

    var entry = new CpioEntry {
      Format = CpioArchiveFormat.PortableAscii,
      DevMajor = (uint)(device >> 8),
      DevMinor = (uint)(device & 0xFF),
      Inode = (uint)ParseOctal(header, 12, 6),
      Mode = (uint)ParseOctal(header, 18, 6),
      Uid = (uint)ParseOctal(header, 24, 6),
      Gid = (uint)ParseOctal(header, 30, 6),
      NumLinks = (uint)ParseOctal(header, 36, 6),
      RDevMajor = (uint)(rDevice >> 8),
      RDevMinor = (uint)(rDevice & 0xFF),
      ModificationTime = (uint)ParseOctal(header, 48, 11),
      FileSize = ParseOctal(header, 65, 11),
    };

    return (entry, checked((int)ParseOctal(header, 59, 6)));
  }

  private (CpioEntry Entry, int NameSize) ReadBinaryHeader(ReadOnlySpan<byte> magic, bool littleEndian) {
    var header = this.ReadHeaderBody(magic, CpioConstants.BinaryHeaderSize, "binary CPIO header");

    ushort Word(int offset) => littleEndian
      ? BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(offset, 2))
      : BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(offset, 2));

    // The two 32-bit fields are a pair of 16-bit words, most significant first,
    // each word in the host order the archive was written on (cpio(5)).
    uint Long(int offset) => ((uint)Word(offset) << 16) | Word(offset + 2);

    var device = Word(2);
    var rDevice = Word(14);

    var entry = new CpioEntry {
      Format = littleEndian ? CpioArchiveFormat.BinaryLittleEndian : CpioArchiveFormat.BinaryBigEndian,
      DevMajor = (uint)device >> 8,
      DevMinor = (uint)device & 0xFF,
      Inode = Word(4),
      Mode = Word(6),
      Uid = Word(8),
      Gid = Word(10),
      NumLinks = Word(12),
      RDevMajor = (uint)rDevice >> 8,
      RDevMinor = (uint)rDevice & 0xFF,
      ModificationTime = Long(16),
      FileSize = Long(22),
    };

    return (entry, Word(20));
  }

  private byte[] ReadHeaderBody(ReadOnlySpan<byte> magic, int headerSize, string what) {
    var header = new byte[headerSize];
    magic.CopyTo(header);
    this.ReadExactly(header.AsSpan(magic.Length), what);
    return header;
  }

  private static uint ParseHex(ReadOnlySpan<byte> header, int offset) {
    var field = header.Slice(offset, 8);
    uint result = 0;
    foreach (var b in field) {
      var digit = b switch {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
        // Some producers space-pad short fields; treat that as a leading zero
        // rather than rejecting an archive every other tool reads.
        (byte)' ' => 0,
        _ => throw new InvalidDataException(
          $"Invalid hexadecimal digit 0x{b:X2} in CPIO header at offset {offset}."),
      };
      result = (result << 4) | (uint)digit;
    }
    return result;
  }

  private static long ParseOctal(ReadOnlySpan<byte> header, int offset, int length) {
    var field = header.Slice(offset, length);
    long result = 0;
    foreach (var b in field) {
      var digit = b switch {
        >= (byte)'0' and <= (byte)'7' => b - (byte)'0',
        (byte)' ' or 0 => 0,
        _ => throw new InvalidDataException(
          $"Invalid octal digit 0x{b:X2} in CPIO header at offset {offset}."),
      };
      result = (result << 3) | (uint)digit;
    }
    return result;
  }

  private int Read(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = this._stream.Read(buffer[total..]);
      if (read <= 0)
        break;
      total += read;
    }
    return total;
  }

  private void ReadExactly(Span<byte> buffer, string what) {
    var read = this.Read(buffer);
    if (read != buffer.Length)
      throw new EndOfStreamException($"Truncated {what}: expected {buffer.Length} bytes, got {read}.");
  }

  private void Skip(int count) {
    if (count <= 0)
      return;

    // Seeking past the end would silently succeed and only surface as a bogus
    // magic on the next header, so the padding is bounds-checked either way.
    if (this._stream.CanSeek) {
      if (this._stream.Position + count > this._stream.Length)
        throw new EndOfStreamException($"Truncated CPIO alignment padding: expected {count} bytes.");
      this._stream.Position += count;
      return;
    }

    Span<byte> scratch = stackalloc byte[4];
    this.ReadExactly(scratch[..count], "CPIO alignment padding");
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
