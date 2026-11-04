#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Deflate;
using Compression.Core.Streams;
using FileFormat.Bzip2;
using Crc32 = Compression.Core.Checksums.Crc32;

namespace FileFormat.Phar;

/// <summary>How a PHAR member is stored.</summary>
public enum PharCompression {
  /// <summary>Stored as is.</summary>
  None,
  /// <summary>Raw DEFLATE (PHP's <c>zlib.deflate</c> filter, no zlib header).</summary>
  Gzip,
  /// <summary>A bzip2 stream.</summary>
  Bzip2,
}

/// <summary>The algorithm of a PHAR's trailing signature.</summary>
public enum PharSignatureType : uint {
  /// <summary>MD5 digest.</summary>
  Md5 = 0x01,
  /// <summary>SHA-1 digest.</summary>
  Sha1 = 0x02,
  /// <summary>SHA-256 digest.</summary>
  Sha256 = 0x03,
  /// <summary>SHA-512 digest.</summary>
  Sha512 = 0x04,
  /// <summary>OpenSSL RSA signature (SHA-1).</summary>
  OpenSsl = 0x10,
  /// <summary>OpenSSL RSA signature (SHA-256).</summary>
  OpenSslSha256 = 0x11,
  /// <summary>OpenSSL RSA signature (SHA-512).</summary>
  OpenSslSha512 = 0x12,
}

/// <summary>One manifest entry of a PHAR: a file, or a directory when the name ends in <c>/</c>.</summary>
public sealed record PharEntry(
  string Name, uint UncompressedSize, uint Timestamp, uint CompressedSize, uint Crc32, uint Flags, byte[] Metadata, long DataOffset) {

  /// <summary>Whether this entry is an (explicitly stored) directory.</summary>
  public bool IsDirectory => this.Name.EndsWith('/');

  /// <summary>The Unix permission bits.</summary>
  public uint Permissions => this.Flags & PharReader.PermissionMask;

  /// <summary>How the member's bytes are stored.</summary>
  public PharCompression Compression => (this.Flags & PharReader.EntryGzip) != 0 ? PharCompression.Gzip
    : (this.Flags & PharReader.EntryBzip2) != 0 ? PharCompression.Bzip2
    : PharCompression.None;

  /// <summary>Modification time.</summary>
  public DateTime LastModified => DateTime.UnixEpoch.AddSeconds(this.Timestamp);
}

/// <summary>
/// Reads a PHP archive in the native PHAR layout: a PHP stub ending in <c>__HALT_COMPILER();</c>, the
/// manifest, the member data, and an optional signature trailer. Tar- and ZIP-based phars are a
/// different container and are refused.
/// </summary>
/// <remarks>
/// Layout (integers little-endian except the API version), per the PHP manual's "Phar file format":
/// <code>
/// stub … "__HALT_COMPILER();" [" ?>" ["\r"] "\n"]
/// UInt32 manifestLength  UInt32 count  byte apiHi byte apiLo  UInt32 globalFlags
/// UInt32 aliasLength alias  UInt32 metadataLength metadata
/// { UInt32 nameLength name  UInt32 size  UInt32 mtime  UInt32 compressedSize  UInt32 crc32
///   UInt32 flags  UInt32 metadataLength metadata }*
/// member data, in manifest order
/// [ signature  [UInt32 signatureLength — OpenSSL only]  UInt32 signatureType  "GBMB" ]   (globalFlags &amp; 0x10000)
/// </code>
/// The digest covers every byte before it. Like PHP, a phar whose digest does not match is refused.
/// </remarks>
public sealed class PharReader {

  /// <summary>Global flag: the archive carries a signature trailer.</summary>
  public const uint HeaderSignature = 0x00010000;
  /// <summary>Entry flag: member stored raw-DEFLATE compressed.</summary>
  public const uint EntryGzip = 0x00001000;
  /// <summary>Entry flag: member stored bzip2 compressed.</summary>
  public const uint EntryBzip2 = 0x00002000;
  /// <summary>Entry flags holding the Unix permission bits.</summary>
  public const uint PermissionMask = 0x000001FF;

  /// <summary>The stub terminator, matched case-insensitively as PHP does.</summary>
  public static ReadOnlySpan<byte> HaltToken => "__HALT_COMPILER();"u8;

  /// <summary>The trailer magic.</summary>
  public static ReadOnlySpan<byte> SignatureMagic => "GBMB"u8;

  private readonly byte[] _image;

  public PharReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;
    using (var ms = new MemoryStream()) {
      stream.CopyTo(ms);
      this._image = ms.ToArray();
    }
    var image = this._image;
    if (image.AsSpan().StartsWith("PK\x03\x04"u8) || (image.Length > 262 && image.AsSpan(257, 5).SequenceEqual("ustar"u8)))
      throw new InvalidDataException("PHAR: a ZIP- or tar-based phar, not the native phar layout.");

    var halt = IndexOfIgnoreCase(image, HaltToken);
    if (halt < 0)
      throw new InvalidDataException("PHAR: no __HALT_COMPILER(); in the stub.");
    var pos = halt + HaltToken.Length;
    if (pos + 3 <= image.Length && image[pos] is (byte)' ' or (byte)'\n' && image[pos + 1] == '?' && image[pos + 2] == '>') {
      pos += 3;
      if (pos < image.Length && image[pos] == '\r') {
        if (pos + 1 >= image.Length || image[pos + 1] != '\n')
          throw new InvalidDataException("PHAR: '?>\\r' after __HALT_COMPILER(); without '\\n'.");
        pos += 2;
      } else if (pos < image.Length && image[pos] == '\n')
        ++pos;
    }
    this.Stub = image[..pos];

    var manifestLength = ReadUInt32(image, pos, "manifest length");
    var manifestStart = pos + 4;
    if (manifestLength < 14 || manifestLength > image.Length - manifestStart)
      throw new InvalidDataException($"PHAR: manifest length {manifestLength} overruns the file.");
    var manifestEnd = manifestStart + (int)manifestLength;

    var p = manifestStart;
    var count = ReadUInt32(image, p, "entry count");
    this.ApiVersion = (ushort)((image[p + 4] << 8) | image[p + 5]);
    this.GlobalFlags = ReadUInt32(image, p + 6, "global flags");
    p += 10;
    this.Alias = Encoding.UTF8.GetString(ReadBlock(image, ref p, manifestEnd, "alias"));
    this.Metadata = ReadBlock(image, ref p, manifestEnd, "archive metadata");

    // The smallest entry record is 24 bytes plus its name.
    if (count > (manifestEnd - p) / 24u)
      throw new InvalidDataException($"PHAR: {count} entries cannot fit the manifest.");
    var entries = new List<PharEntry>((int)count);
    long data = manifestEnd;
    for (var i = 0u; i < count; ++i) {
      var name = Encoding.UTF8.GetString(ReadBlock(image, ref p, manifestEnd, "entry name"));
      if (p + 20 > manifestEnd)
        throw new InvalidDataException($"PHAR: truncated manifest entry '{name}'.");
      var size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(p));
      var time = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(p + 4));
      var csize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(p + 8));
      var crc = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(p + 12));
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(p + 16));
      p += 20;
      var meta = ReadBlock(image, ref p, manifestEnd, "entry metadata");
      entries.Add(new PharEntry(name, size, time, csize, crc, flags, meta, data));
      data += csize;
    }
    if (p != manifestEnd)
      throw new InvalidDataException("PHAR: manifest length does not match its entries.");
    this.Entries = entries;

    var dataEnd = (long)image.Length;
    if ((this.GlobalFlags & HeaderSignature) != 0) {
      if (image.Length < manifestEnd + 8 || !image.AsSpan(image.Length - 4).SequenceEqual(SignatureMagic))
        throw new InvalidDataException("PHAR: signature flag set but no GBMB trailer.");
      var type = (PharSignatureType)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(image.Length - 8));
      var isOpenSsl = type is PharSignatureType.OpenSsl or PharSignatureType.OpenSslSha256 or PharSignatureType.OpenSslSha512;
      // An OpenSSL signature carries its own length before the type; a digest's length follows from the type.
      var sigEnd = image.Length - (isOpenSsl ? 12 : 8);
      long sigLength = type switch {
        PharSignatureType.Md5 => 16,
        PharSignatureType.Sha1 => 20,
        PharSignatureType.Sha256 => 32,
        PharSignatureType.Sha512 => 64,
        _ when isOpenSsl && sigEnd >= manifestEnd => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sigEnd)),
        _ when isOpenSsl => throw new InvalidDataException("PHAR: truncated OpenSSL signature."),
        _ => throw new InvalidDataException($"PHAR: unknown signature type 0x{(uint)type:X}."),
      };
      var sigStart = sigEnd - sigLength;
      if (sigStart < manifestEnd)
        throw new InvalidDataException("PHAR: signature overruns the file.");
      this.SignatureType = type;
      this.Signature = image[(int)sigStart..sigEnd];
      dataEnd = sigStart;
      // OpenSSL signatures need the publisher's key, which a phar does not carry; digests are checked.
      if (!isOpenSsl && !ComputeDigest(type, image.AsSpan(0, (int)sigStart)).AsSpan().SequenceEqual(this.Signature))
        throw new InvalidDataException($"PHAR: {type} signature does not match the archive.");
    }
    if (data > dataEnd)
      throw new InvalidDataException("PHAR: member data overruns the file.");
  }

  /// <summary>The stub, up to and including the line end after <c>__HALT_COMPILER();</c>.</summary>
  public byte[] Stub { get; }

  /// <summary>The manifest API version (e.g. 0x1110 for 1.1.1).</summary>
  public ushort ApiVersion { get; }

  /// <summary>The global manifest flags.</summary>
  public uint GlobalFlags { get; }

  /// <summary>The archive alias; empty when none is stored.</summary>
  public string Alias { get; }

  /// <summary>The archive's serialized PHP metadata; empty when none.</summary>
  public byte[] Metadata { get; }

  /// <summary>Manifest entries in file order.</summary>
  public IReadOnlyList<PharEntry> Entries { get; }

  /// <summary>The signature algorithm, or null when the archive is unsigned.</summary>
  public PharSignatureType? SignatureType { get; }

  /// <summary>The stored digest or OpenSSL signature; empty when unsigned.</summary>
  public byte[] Signature { get; } = [];

  /// <summary>Returns a member's stored bytes, still compressed.</summary>
  public byte[] ReadStored(PharEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return this._image.AsSpan((int)entry.DataOffset, (int)entry.CompressedSize).ToArray();
  }

  /// <summary>Returns a member's content, decompressed and checked against its size and CRC-32.</summary>
  public byte[] Extract(PharEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var stored = this.ReadStored(entry);
    var content = entry.Compression switch {
      PharCompression.Gzip => DeflateDecompressor.Decompress(stored),
      PharCompression.Bzip2 => DecompressBzip2(stored),
      _ => stored,
    };
    if (content.Length != entry.UncompressedSize)
      throw new InvalidDataException($"PHAR: '{entry.Name}' decoded to {content.Length} bytes, manifest says {entry.UncompressedSize}.");
    if (Crc32.Compute(content) != entry.Crc32)
      throw new InvalidDataException($"PHAR: CRC-32 mismatch for '{entry.Name}'.");
    return content;
  }

  /// <summary>Computes a PHAR digest signature of <paramref name="data"/>.</summary>
  public static byte[] ComputeDigest(PharSignatureType type, ReadOnlySpan<byte> data) => type switch {
    PharSignatureType.Md5 => MD5.HashData(data),
    PharSignatureType.Sha1 => SHA1.HashData(data),
    PharSignatureType.Sha256 => SHA256.HashData(data),
    PharSignatureType.Sha512 => SHA512.HashData(data),
    _ => throw new NotSupportedException($"PHAR: {type} is not a digest signature."),
  };

  private static byte[] DecompressBzip2(byte[] stored) {
    using var input = new MemoryStream(stored);
    using var bz = new Bzip2Stream(input, CompressionStreamMode.Decompress, leaveOpen: true);
    using var output = new MemoryStream();
    bz.CopyTo(output);
    return output.ToArray();
  }

  private static uint ReadUInt32(byte[] image, int offset, string what) {
    if (offset < 0 || offset + 4 > image.Length)
      throw new InvalidDataException($"PHAR: truncated before the {what}.");
    return BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset));
  }

  private static byte[] ReadBlock(byte[] image, ref int p, int end, string what) {
    if (p + 4 > end)
      throw new InvalidDataException($"PHAR: truncated manifest before the {what}.");
    var length = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(p));
    p += 4;
    if (length > end - p)
      throw new InvalidDataException($"PHAR: {what} overruns the manifest.");
    var block = image[p..(p + (int)length)];
    p += (int)length;
    return block;
  }

  private static int IndexOfIgnoreCase(byte[] haystack, ReadOnlySpan<byte> needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; ++i) {
      var j = 0;
      while (j < needle.Length && AsciiUpper(haystack[i + j]) == needle[j]) ++j;
      if (j == needle.Length) return i;
    }
    return -1;
  }

  private static byte AsciiUpper(byte b) => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;
}
