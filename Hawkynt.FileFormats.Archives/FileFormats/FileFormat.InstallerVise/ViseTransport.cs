using System.Buffers.Binary;

namespace FileFormat.InstallerVise;

/// <summary>
/// The fork transports an Installer VISE application arrives in: MacBinary and BinHex 4.0
/// (decoded recursively, any nesting), and AppleDouble sidecars for the resource fork.
/// </summary>
/// <remarks>Converted from unvise's <c>transport.c</c> and <c>io.c</c> (MIT); see THIRD-PARTY-NOTICE.unvise.txt.</remarks>
internal static class ViseTransport {
  /// <summary>Strips MacBinary and BinHex layers; returns the innermost data fork and its resource fork.</summary>
  public static (byte[] Data, byte[]? Resource) Unwrap(byte[] data, byte[]? resource) {
    for (var depth = 0; IsMacBinary(data) || IsBinHex(data); ++depth) {
      if (depth >= 16)
        throw new InvalidDataException("VISE: too many nested MacBinary or BinHex layers.");
      (data, resource) = IsMacBinary(data) ? DecodeMacBinary(data) : DecodeBinHex(data);
    }
    return (data, resource);
  }

  private static long Padded128(long n) => (n + 127) & ~127L;

  internal static bool IsMacBinary(ReadOnlySpan<byte> input) {
    if (input.Length < 128 || input[0] != 0 || input[74] != 0)
      return false;
    var nameSize = input[1];
    if (nameSize is 0 or > 63)
      return false;
    long dataSize = BinaryPrimitives.ReadUInt32BigEndian(input[83..]);
    long resourceSize = BinaryPrimitives.ReadUInt32BigEndian(input[87..]);
    var resourceOffset = 128 + Padded128(dataSize);
    return 128 + dataSize <= input.Length && resourceOffset + resourceSize <= input.Length;
  }

  private static (byte[], byte[]?) DecodeMacBinary(byte[] input) {
    var dataSize = (int)BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(83));
    var resourceSize = (int)BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(87));
    var resourceOffset = (int)(128 + Padded128(dataSize));
    return (input.AsSpan(128, dataSize).ToArray(), resourceSize > 0 ? input.AsSpan(resourceOffset, resourceSize).ToArray() : null);
  }

  private const string HqxAlphabet = "!\"#$%&'()*+,-012345689@ABCDEFGHIJKLMNPQRSTUVXYZ[`abcdefhijklmpqr";
  private static ReadOnlySpan<byte> BinHexMarker => "(This file must be converted with BinHex 4.0)"u8;

  private static int HqxValue(byte c) => c == 0 ? -1 : HqxAlphabet.IndexOf((char)c);

  private static int MarkerEnd(ReadOnlySpan<byte> input) {
    var limit = Math.Min(input.Length, 65536);
    var at = input[..limit].IndexOf(BinHexMarker);
    return at < 0 ? -1 : at + BinHexMarker.Length;
  }

  internal static bool IsBinHex(ReadOnlySpan<byte> input)
    => MarkerEnd(input) >= 0 || (input.Length > 2 && input[0] == ':' && HqxValue(input[1]) >= 0);

  private static byte[] DecodeHqxText(ReadOnlySpan<byte> input) {
    var start = MarkerEnd(input);
    if (start < 0)
      start = 0;
    while (start < input.Length && input[start] != ':')
      ++start;
    if (start == input.Length)
      throw new InvalidDataException("VISE: BinHex input lacks an encoded-data delimiter.");

    var packed = new List<byte>(input.Length);
    uint bits = 0, value = 0;
    var ended = false;
    for (var i = start + 1; i < input.Length; ++i) {
      if (input[i] == ':') {
        ended = true;
        break;
      }
      var digit = HqxValue(input[i]);
      if (digit < 0) {
        if (input[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
          continue;
        throw new InvalidDataException("VISE: invalid character in BinHex data.");
      }
      value = (value << 6) | (uint)digit;
      bits += 6;
      if (bits >= 8) {
        bits -= 8;
        packed.Add((byte)(value >> (int)bits));
        value &= bits != 0 ? (1u << (int)bits) - 1 : 0;
      }
    }
    if (!ended)
      throw new InvalidDataException("VISE: truncated BinHex input.");

    var output = new List<byte>(packed.Count);
    for (var i = 0; i < packed.Count; ++i) {
      var b = packed[i];
      if (b != 0x90) {
        output.Add(b);
        continue;
      }
      if (++i == packed.Count)
        throw new InvalidDataException("VISE: truncated BinHex run.");
      b = packed[i];
      if (b == 0) {
        output.Add(0x90);
        continue;
      }
      if (output.Count == 0)
        throw new InvalidDataException("VISE: invalid BinHex run.");
      while (--b != 0)
        output.Add(output[^1]);
    }
    return [.. output];
  }

  private static ushort BinHexCrc(ReadOnlySpan<byte> p) {
    ushort crc = 0;
    foreach (var b in p) {
      crc ^= (ushort)(b << 8);
      for (var i = 0; i < 8; ++i)
        crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
    }
    return crc;
  }

  private static void CheckCrc(byte[] decoded, long start, long size, string part) {
    if (start + size + 2 > decoded.Length)
      throw new InvalidDataException("VISE: truncated BinHex file.");
    if (BinHexCrc(decoded.AsSpan((int)start, (int)size)) != BinaryPrimitives.ReadUInt16BigEndian(decoded.AsSpan((int)(start + size))))
      throw new InvalidDataException($"VISE: BinHex {part} CRC mismatch.");
  }

  private static (byte[], byte[]?) DecodeBinHex(byte[] input) {
    var decoded = DecodeHqxText(input);
    if (decoded.Length == 0)
      throw new InvalidDataException("VISE: empty BinHex data.");
    var nameSize = decoded[0];
    if (nameSize is 0 or > 63)
      throw new InvalidDataException("VISE: invalid BinHex filename length.");
    var headerSize = 1 + nameSize + 1 + 4 + 4 + 2 + 4 + 4;
    if (headerSize + 2 > decoded.Length)
      throw new InvalidDataException("VISE: truncated BinHex header.");
    long dataSize = BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(headerSize - 8));
    long resourceSize = BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(headerSize - 4));
    CheckCrc(decoded, 0, headerSize, "header");
    long dataOffset = headerSize + 2;
    CheckCrc(decoded, dataOffset, dataSize, "data fork");
    var resourceOffset = dataOffset + dataSize + 2;
    CheckCrc(decoded, resourceOffset, resourceSize, "resource fork");
    return (decoded.AsSpan((int)dataOffset, (int)dataSize).ToArray(),
      resourceSize > 0 ? decoded.AsSpan((int)resourceOffset, (int)resourceSize).ToArray() : null);
  }

  /// <summary>The resource fork an AppleDouble container holds (entry 2), null when it has none; throws when it is not AppleDouble.</summary>
  public static byte[]? AppleDoubleResource(ReadOnlySpan<byte> sidecar, out bool isAppleDouble) {
    isAppleDouble = sidecar.Length >= 26 && BinaryPrimitives.ReadUInt32BigEndian(sidecar) == 0x00051607;
    if (!isAppleDouble)
      return null;
    int count = BinaryPrimitives.ReadUInt16BigEndian(sidecar[24..]);
    if (26 + count * 12 > sidecar.Length)
      throw new InvalidDataException("VISE: truncated AppleDouble entry table.");
    for (var i = 0; i < count; ++i) {
      var entry = sidecar.Slice(26 + i * 12, 12);
      if (BinaryPrimitives.ReadUInt32BigEndian(entry) != 2)
        continue;
      long offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
      long length = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
      if (offset + length > sidecar.Length)
        throw new InvalidDataException("VISE: truncated AppleDouble resource fork.");
      return sidecar.Slice((int)offset, (int)length).ToArray();
    }
    return null;
  }

  /// <summary>
  /// Finds the resource fork beside a data fork on disk the way <c>unar -k hidden</c> and
  /// <c>macunpack -f</c> leave it: <c>._name</c> AppleDouble, or <c>name.rsrc</c> (raw or
  /// AppleDouble) beside <c>name.data</c>.
  /// </summary>
  public static byte[]? SidecarResource(string path) {
    var directory = Path.GetDirectoryName(path) ?? "";
    var hidden = Path.Combine(directory, "._" + Path.GetFileName(path));
    if (File.Exists(hidden)) {
      var found = AppleDoubleResource(File.ReadAllBytes(hidden), out _);
      if (found != null)
        return found;
    }
    if (!path.EndsWith(".data", StringComparison.Ordinal))
      return null;
    var raw = path[..^5] + ".rsrc";
    if (!File.Exists(raw))
      return null;
    var bytes = File.ReadAllBytes(raw);
    var resource = AppleDoubleResource(bytes, out var appleDouble);
    if (appleDouble && resource == null)
      throw new InvalidDataException("VISE: AppleDouble sidecar lacks a resource fork.");
    return appleDouble ? resource : bytes;
  }
}
