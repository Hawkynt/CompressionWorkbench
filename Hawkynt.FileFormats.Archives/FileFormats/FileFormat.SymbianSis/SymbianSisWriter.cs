using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileFormat.SymbianSis;

/// <summary>A file a written package installs.</summary>
/// <param name="Target">The install target, "!:\sys\bin\app.exe" style ("!" lets the user pick the drive).</param>
/// <param name="Data">Its contents.</param>
public sealed record SymbianSisFile(string Target, byte[] Data);

/// <summary>What <see cref="SymbianSisWriter"/> packs: the header of a makesis package file plus its file lines.</summary>
public sealed class SymbianSisPackage {
  /// <summary>The package name (the .pkg "#{...}" line), in English.</summary>
  public required string Name { get; init; }

  /// <summary>UID 3, the package UID.</summary>
  public required uint Uid { get; init; }

  /// <summary>The non-localised vendor name (the .pkg ":" line).</summary>
  public string Vendor { get; init; } = "Vendor";

  /// <summary>The English vendor name (the .pkg "%{...}" line); the unique vendor name when null.</summary>
  public string? LocalizedVendor { get; init; }

  /// <summary>The package version.</summary>
  public (int Major, int Minor, int Build) Version { get; init; } = (1, 0, 0);

  /// <summary>The creation time recorded in SISInfo (UTC).</summary>
  public DateTime CreationTime { get; init; } = DateTime.UnixEpoch;

  /// <summary>The files to install, in package order.</summary>
  public IReadOnlyList<SymbianSisFile> Files { get; init; } = [];
}

/// <summary>
/// Writes unsigned Symbian OS 9.x SIS packages the way makesis writes a .pkg with one English
/// language, an S60 3rd Edition target device and plain install lines: the same fields in the
/// same order, the controller and each smaller-when-compressed file deflated with the zlib level
/// makesis uses, so that such a package comes out byte-identical to makesis's own.
/// </summary>
/// <remarks>
/// Field layout from "Symbian OS v9.X SIS File Format Specification" 1.1; the choices the
/// specification leaves open (which optional fields makesis writes, when it compresses, the
/// zero-based month) were taken from packages the SDK's makesis made.
/// </remarks>
public static class SymbianSisWriter {
  /// <summary>The S60 3rd Edition platform UID the .pkg "[0x102752AE], 0, 0, 0, {"S60ProductID"}" line names.</summary>
  public const uint S60ProductUid = 0x102752AE;

  private const uint LanguageEnglish = 1;
  private const uint EOpInstall = 1;
  private const uint ESISHashAlgSHA1 = 1;

  /// <summary>Writes a package.</summary>
  public static byte[] Write(SymbianSisPackage package) {
    ArgumentNullException.ThrowIfNull(package);

    var stored = package.Files.Select(f => Compress(f.Data)).ToList();
    var data = Field(30, Array(31, [Array(32, stored.Select(s => Field(3, s)).ToList())]));

    var controller = Field(13, Concat(
      Info(package),
      Field(16, Array(33, [])),
      Field(15, Array(11, [U32(LanguageEnglish)])),
      Field(17, Concat(
        Array(18, [Concat(
          Field(9, U32(S60ProductUid)),
          Field(5, Field(4, new byte[12])),
          Array(1, [Encoding.Unicode.GetBytes("S60ProductID")]))]),
        Array(18, []))),
      Field(19, Array(20, [])),
      Field(28, Concat(
        Array(24, package.Files.Select((f, i) => FileDescription(f, stored[i].Length - 12, i)).ToList()),
        Array(13, []),
        Array(26, []))),
      Field(40, U32(0))));
    var compressedController = Field(3, Concat(Header(1, controller.Length), ZlibAsMakesis(controller)));

    using var ms = new MemoryStream();
    ms.Write(UidHeader(package.Uid));
    ms.Write(Field(12, Concat(
      Field(34, U16(Crc16Ccitt.Compute(compressedController))),
      Field(35, U16(Crc16Ccitt.Compute(data))),
      compressedController,
      data)));
    return ms.ToArray();
  }

  // 4.2.5: UID, vendor unique name, names, vendor names, version, creation time, type SA, no flags.
  private static byte[] Info(SymbianSisPackage p) {
    var t = p.CreationTime.ToUniversalTime();
    var date = new byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(date, (ushort)t.Year);
    date[2] = (byte)(t.Month - 1); // 4.1.6.2: January is 0
    date[3] = (byte)t.Day;
    var version = new byte[12];
    BinaryPrimitives.WriteInt32LittleEndian(version, p.Version.Major);
    BinaryPrimitives.WriteInt32LittleEndian(version.AsSpan(4), p.Version.Minor);
    BinaryPrimitives.WriteInt32LittleEndian(version.AsSpan(8), p.Version.Build);
    return Field(14, Concat(
      Field(9, U32(p.Uid)),
      Field(1, Encoding.Unicode.GetBytes(p.Vendor)),
      Array(1, [Encoding.Unicode.GetBytes(p.Name)]),
      Array(1, [Encoding.Unicode.GetBytes(p.LocalizedVendor ?? p.Vendor)]),
      Field(4, version),
      Field(8, Concat(Field(6, date), Field(7, [(byte)t.Hour, (byte)t.Minute, (byte)t.Second]))),
      [0, 0]));
  }

  // 4.2.14; makesis adds SISCapabilities only for an E32 image that requests some.
  private static byte[] FileDescription(SymbianSisFile f, long storedLength, int index) {
    var fixedPart = new byte[28];
    BinaryPrimitives.WriteUInt32LittleEndian(fixedPart, EOpInstall);
    BinaryPrimitives.WriteInt64LittleEndian(fixedPart.AsSpan(8), storedLength);
    BinaryPrimitives.WriteInt64LittleEndian(fixedPart.AsSpan(16), f.Data.Length);
    BinaryPrimitives.WriteInt32LittleEndian(fixedPart.AsSpan(24), index);
    var capabilities = E32Capabilities(f.Data) is { } caps and not 0 ? Field(41, U32(caps)) : [];
    return Concat(
      Field(1, Encoding.Unicode.GetBytes(f.Target)),
      Field(1, []),
      capabilities,
      Field(25, Concat(U32(ESISHashAlgSHA1), Field(37, SHA1.HashData(f.Data)))),
      fixedPart);
  }

  /// <summary>The capability word of an E32 image header ("EPOC" at 0x10, capabilities at 0x88), or null.</summary>
  internal static uint? E32Capabilities(ReadOnlySpan<byte> image)
    => image.Length >= 0x8C && image[0x10..0x14].SequenceEqual("EPOC"u8)
      ? BinaryPrimitives.ReadUInt32LittleEndian(image[0x88..])
      : null;

  // makesis deflates a file only when that makes it smaller.
  private static byte[] Compress(byte[] data) {
    var deflated = ZlibAsMakesis(data);
    return deflated.Length < data.Length ? Concat(Header(1, data.Length), deflated) : Concat(Header(0, data.Length), data);
  }

  private static byte[] ZlibAsMakesis(byte[] data) => FileFormat.Zlib.ZlibStream.Compress(data, DeflateCompressionLevel.Default);

  // 4.1.3: compression algorithm, 64-bit uncompressed size.
  private static byte[] Header(uint algorithm, long size) {
    var h = new byte[12];
    BinaryPrimitives.WriteUInt32LittleEndian(h, algorithm);
    BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(4), size);
    return h;
  }

  // 3.1: UID 1, UID 2 (0), UID 3 and the checked-UID word: CRC-16s of the odd and of the even bytes.
  private static byte[] UidHeader(uint uid3) {
    var h = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(h, SymbianSisReader.Uid1);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), uid3);
    var even = new byte[6];
    var odd = new byte[6];
    for (var i = 0; i < 6; ++i) {
      even[i] = h[2 * i];
      odd[i] = h[2 * i + 1];
    }
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), (uint)Crc16Ccitt.Compute(odd) << 16 | Crc16Ccitt.Compute(even));
    return h;
  }

  // 2.12: type, 31-bit length (63-bit with the top bit set), value, zero padding to 4 bytes.
  private static byte[] Field(uint type, byte[] value) => Concat(U32(type), LengthValue(value));

  // 4.1.2: the element type once, then length and value of every element.
  private static byte[] Array(uint elementType, IReadOnlyList<byte[]> values)
    => Field(2, Concat([U32(elementType), .. values.Select(LengthValue)]));

  private static byte[] LengthValue(byte[] value) {
    var length = (ulong)value.Length;
    var prefix = length < 0x8000_0000 ? U32((uint)length) : Concat(U32((uint)(length & 0x7FFF_FFFF) | 0x8000_0000), U32((uint)(length >> 31)));
    return Concat(prefix, value, new byte[(4 - value.Length % 4) % 4]);
  }

  private static byte[] U32(uint v) {
    var b = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    return b;
  }

  private static byte[] U16(ushort v) => [(byte)v, (byte)(v >> 8)];

  private static byte[] Concat(params byte[][] parts) {
    var result = new byte[parts.Sum(p => p.Length)];
    var at = 0;
    foreach (var p in parts) {
      p.CopyTo(result, at);
      at += p.Length;
    }
    return result;
  }
}
