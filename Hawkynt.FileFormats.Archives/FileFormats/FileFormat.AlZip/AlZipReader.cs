// Layout per XADMaster's XADALZipParser.m (LGPL-2.1-or-later) and alkegi/unalz (MIT); see
// THIRD-PARTY-NOTICE.unalz.txt.

using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;
using FileFormat.Bzip2;

namespace FileFormat.AlZip;

/// <summary>
/// Reads single-volume ALZip archives: "ALZ\x01" + 4 bytes, local file headers ("BLZ\x01"), then the
/// central-directory and end records ("CLZ\x01", "CLZ\x02"). Members are stored, deflated or
/// ALZ-bzip2'd, optionally ZipCrypto-encrypted. Names are CP949.
/// </summary>
public sealed class AlZipReader : IDisposable {
  internal static ReadOnlySpan<byte> Magic => "ALZ\x01"u8;

  internal const uint LocalSig = 0x015A4C42;   // "BLZ\x01"
  internal const uint CentralSig = 0x015A4C43; // "CLZ\x01"
  internal const uint EndSig = 0x025A4C43;     // "CLZ\x02"
  internal const uint CommentSig = 0x015A4C45; // "ELZ\x01"
  internal const uint SplitSig = 0x035A4C43;   // "CLZ\x03"

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<AlZipEntry> _entries = [];

  public IReadOnlyList<AlZipEntry> Entries => this._entries;

  /// <summary>Whether the archive is one volume of a split set (only whole entries of it are readable).</summary>
  public bool IsSplit { get; private set; }

  internal static Encoding Cp949 {
    get {
      Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
      return Encoding.GetEncoding(949);
    }
  }

  public AlZipReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this.ReadArchive();
  }

  public void Dispose() {
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private uint ReadUInt32() {
    Span<byte> b = stackalloc byte[4];
    this._stream.ReadExactly(b);
    return BinaryPrimitives.ReadUInt32LittleEndian(b);
  }

  private void ReadArchive() {
    this._stream.Position = 0;
    Span<byte> head = stackalloc byte[8];
    if (this._stream.ReadAtLeast(head, 8, throwOnEndOfStream: false) < 8 || !head[..4].SequenceEqual(Magic))
      throw new InvalidDataException("Not an ALZip archive (missing ALZ\\x01 header).");

    Span<byte> sigBuf = stackalloc byte[4];
    while (true) {
      if (this._stream.ReadAtLeast(sigBuf, 4, throwOnEndOfStream: false) < 4) {
        this.IsSplit |= this._entries.Count > 0; // ended without its end record
        break;
      }
      var sig = BinaryPrimitives.ReadUInt32LittleEndian(sigBuf);
      if (sig == LocalSig) {
        this.ReadLocalHeader();
        continue;
      }
      if (sig is CentralSig or EndSig or CommentSig)
        break; // central records carry nothing the local headers do not; comments sit at the end
      if (sig == SplitSig) {
        this.IsSplit = true;
        break;
      }
      throw new InvalidDataException($"Unexpected ALZip signature 0x{sig:X8}.");
    }
  }

  private void ReadLocalHeader() {
    Span<byte> h = stackalloc byte[9];
    this._stream.ReadExactly(h);
    var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(h);
    var attributes = h[2];
    var dosTime = BinaryPrimitives.ReadUInt32LittleEndian(h[3..]);
    var descriptor = h[7];
    var width = (descriptor & 0xF0) switch {
      0x00 => 0,
      0x10 => 1,
      0x20 => 2,
      0x40 => 4,
      0x80 => 8,
      var w => throw new InvalidDataException($"Invalid ALZip size-field width 0x{w:X2}."),
    };
    int method = 0;
    uint crc = 0;
    long packed = 0, size = 0;
    if (width > 0) {
      Span<byte> m = stackalloc byte[6];
      this._stream.ReadExactly(m);
      method = m[0];
      crc = BinaryPrimitives.ReadUInt32LittleEndian(m[2..]);
      packed = this.ReadSized(width);
      size = this.ReadSized(width);
    }
    if (nameLength == 0 || nameLength > 4096)
      throw new InvalidDataException("Invalid ALZip file-name length.");
    var name = new byte[nameLength];
    this._stream.ReadExactly(name);
    byte[]? encryptionHeader = null;
    if ((descriptor & 0x01) != 0) {
      encryptionHeader = new byte[AlZipCrypto.HeaderLength];
      this._stream.ReadExactly(encryptionHeader);
    }
    var dataOffset = this._stream.Position;
    if (packed < 0 || dataOffset + packed > this._stream.Length) {
      // a member continued in the next volume of a split set
      this.IsSplit = true;
      this._stream.Position = this._stream.Length;
    } else
      this._stream.Position = dataOffset + packed;

    this._entries.Add(new AlZipEntry {
      FileName = Cp949.GetString(name).Replace('\\', '/'),
      Attributes = attributes,
      DosTime = dosTime,
      LastModified = DosTimeToDateTime(dosTime),
      Descriptor = descriptor,
      Method = method,
      Crc32 = crc,
      CompressedSize = packed,
      OriginalSize = size,
      EncryptionHeader = encryptionHeader,
      DataOffset = dataOffset,
    });
  }

  private long ReadSized(int width) {
    Span<byte> b = stackalloc byte[8];
    b.Clear();
    this._stream.ReadExactly(b[..width]);
    var v = BinaryPrimitives.ReadUInt64LittleEndian(b);
    return v > long.MaxValue ? -1 : (long)v;
  }

  /// <summary>Decompresses (and if needed decrypts) a member, verifying its CRC-32.</summary>
  public byte[] Extract(AlZipEntry entry, string? password = null) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.OriginalSize == 0 && entry.CompressedSize == 0)
      return [];
    if (entry.DataOffset + entry.CompressedSize > this._stream.Length)
      throw new NotSupportedException($"ALZip member '{entry.FileName}' continues in another volume; split archives are not supported.");
    var packed = new byte[entry.CompressedSize];
    this._stream.Position = entry.DataOffset;
    this._stream.ReadExactly(packed);

    if (entry.IsEncrypted) {
      if (string.IsNullOrEmpty(password))
        throw new InvalidOperationException($"ALZip member '{entry.FileName}' is encrypted; a password is required.");
      var crypto = new AlZipCrypto(Cp949.GetBytes(password));
      var check = (entry.Descriptor & 0x08) != 0 ? (byte)(entry.DosTime >> 8) : (byte)(entry.Crc32 >> 24);
      if (!crypto.CheckHeader(entry.EncryptionHeader, check))
        throw new InvalidDataException($"ALZip member '{entry.FileName}': wrong password.");
      crypto.Decrypt(packed);
    }

    byte[] data;
    try {
      data = entry.Method switch {
        0 => packed,
        1 => AlzBzip2.Decompress(packed, entry.OriginalSize),
        2 => DeflateDecompressor.Decompress(packed),
        3 => throw new NotSupportedException($"ALZip member '{entry.FileName}' uses obfuscated deflate (method 3), which is not supported."),
        _ => throw new NotSupportedException($"ALZip member '{entry.FileName}' uses unknown method {entry.Method}."),
      };
    } catch (Exception e) when (e is not (NotSupportedException or InvalidDataException)) {
      throw new InvalidDataException($"ALZip member '{entry.FileName}': corrupt data.", e);
    }
    if (data.LongLength != entry.OriginalSize)
      throw new InvalidDataException($"ALZip member '{entry.FileName}': size mismatch.");
    if (Compression.Core.Checksums.Crc32.Compute(data) != entry.Crc32)
      throw new InvalidDataException($"ALZip member '{entry.FileName}': CRC mismatch.");
    return data;
  }

  internal static DateTime? DosTimeToDateTime(uint dosTime) {
    if (dosTime == 0) return null;
    try {
      return new DateTime((int)((dosTime >> 25) & 0x7F) + 1980, (int)((dosTime >> 21) & 0x0F), (int)((dosTime >> 16) & 0x1F),
        (int)((dosTime >> 11) & 0x1F), (int)((dosTime >> 5) & 0x3F), (int)(dosTime & 0x1F) * 2, DateTimeKind.Local);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }

  internal static uint DateTimeToDosTime(DateTime dt) {
    if (dt.Year < 1980) dt = new DateTime(1980, 1, 1);
    return (uint)(((dt.Year - 1980) << 25) | (dt.Month << 21) | (dt.Day << 16) | (dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
  }
}
