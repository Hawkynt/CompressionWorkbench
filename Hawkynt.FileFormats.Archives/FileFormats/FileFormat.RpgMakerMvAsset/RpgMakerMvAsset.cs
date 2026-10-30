#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.RpgMakerMvAsset;

/// <summary>
/// An RPG Maker MV/MZ deployment-encrypted asset (<c>.rpgmvp</c>/<c>.png_</c>, <c>.rpgmvo</c>/<c>.ogg_</c>,
/// <c>.rpgmvm</c>/<c>.m4a_</c>): a fixed 16-byte header, then the original file with its first 16 bytes
/// XORed with the game's 16-byte key (<c>encryptionKey</c> in <c>data/System.json</c>, as 32 hex digits).
/// </summary>
/// <remarks>
/// Layout as the engine's decrypter and Petschko's RPG-Maker-MV-Decrypter (MIT) read it:
/// <code>
/// "RPGMV" 00 00 00   00 03 01   00 00 00 00 00      — signature, version, padding
/// original[0..16) XOR key[0..16)                    — the rest of the file is plain
/// </code>
/// Without the key the first 16 bytes are still recoverable where the original's own header is fixed:
/// a PNG always opens with its 8-byte signature and the 13-byte IHDR chunk header; an Ogg stream with
/// <c>"OggS"</c>, version 0, the beginning-of-stream flag and a zero granule position — the two
/// serial-number bytes left over are pinned by the first page's CRC.
/// </remarks>
public static class RpgMakerMvAsset {

  /// <summary>The fixed header every encrypted asset starts with.</summary>
  public static ReadOnlySpan<byte> Header => [0x52, 0x50, 0x47, 0x4D, 0x56, 0x00, 0x00, 0x00, 0x00, 0x03, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00];

  /// <summary>Length of the header and of the encrypted prefix.</summary>
  public const int Length = 16;

  private static ReadOnlySpan<byte> PngStart => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

  /// <summary>Encrypts <paramref name="plain"/> with <paramref name="key"/>.</summary>
  public static byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key) {
    CheckKey(key);
    var output = new byte[Length + plain.Length];
    Header.CopyTo(output);
    plain.CopyTo(output.AsSpan(Length));
    Xor(output.AsSpan(Length), key);
    return output;
  }

  /// <summary>Decrypts an asset with <paramref name="key"/>.</summary>
  public static byte[] Decrypt(ReadOnlySpan<byte> asset, ReadOnlySpan<byte> key) {
    CheckKey(key);
    var plain = Body(asset).ToArray();
    Xor(plain, key);
    return plain;
  }

  /// <summary>
  /// Recovers the key from an asset whose original header is fixed (PNG, Ogg); null when the content
  /// is something else or the evidence does not single out one key.
  /// </summary>
  public static byte[]? RecoverKey(ReadOnlySpan<byte> asset) {
    var body = Body(asset);
    if (body.Length < Length) return null;
    var key = new byte[Length];
    // A PNG's first 16 bytes are always the same.
    for (var i = 0; i < Length; ++i) key[i] = (byte)(body[i] ^ PngStart[i]);
    if (LooksLikePngAfterHeader(body))
      return key;
    return RecoverOggKey(body);
  }

  /// <summary>The asset's stored body (original bytes, first 16 still encrypted).</summary>
  public static ReadOnlySpan<byte> Body(ReadOnlySpan<byte> asset) {
    if (asset.Length < Length || !asset[..Length].SequenceEqual(Header))
      throw new InvalidDataException("RPG Maker MV asset: missing the RPGMV header.");
    return asset[Length..];
  }

  /// <summary>Parses a key given as 32 hexadecimal digits.</summary>
  public static byte[] ParseKey(string hex) {
    ArgumentNullException.ThrowIfNull(hex);
    hex = hex.Trim();
    if (hex.Length != 2 * Length || !hex.All(char.IsAsciiHexDigit))
      throw new ArgumentException("RPG Maker MV asset: the key is 32 hexadecimal digits (System.json encryptionKey).", nameof(hex));
    return Convert.FromHexString(hex);
  }

  /// <summary>Names the decrypted content by its leading bytes: png, ogg or m4a.</summary>
  public static string? SniffExtension(ReadOnlySpan<byte> plain)
    => plain.StartsWith(PngStart[..8]) ? "png"
      : plain.StartsWith("OggS"u8) ? "ogg"
      : plain.Length >= 8 && plain[4..8].SequenceEqual("ftyp"u8) ? "m4a"
      : null;

  // After the 16 fixed bytes the IHDR payload follows (width, height, depth, colour type, …) and its CRC
  // at offset 29; a correct CRC over "IHDR" + payload confirms the file is a PNG, so the key is right.
  private static bool LooksLikePngAfterHeader(ReadOnlySpan<byte> body) {
    if (body.Length < 33) return false;
    Span<byte> chunk = stackalloc byte[17];
    PngStart[12..].CopyTo(chunk);
    body[16..29].CopyTo(chunk[4..]);
    return Compression.Core.Checksums.Crc32.Compute(chunk) == BinaryPrimitives.ReadUInt32BigEndian(body[29..]);
  }

  private static byte[]? RecoverOggKey(ReadOnlySpan<byte> body) {
    // First page: "OggS" 0 0x02, granule 0 (8 bytes), serial (4), sequence (4), CRC (4), segments (1), lacing.
    if (body.Length < 27) return null;
    var segments = body[26];
    if (body.Length < 27 + segments) return null;
    var pageLength = 27 + segments;
    for (var i = 0; i < segments; ++i) pageLength += body[27 + i];
    if (body.Length < pageLength) return null;

    Span<byte> known = [0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0, 0, 0, 0, 0, 0, 0, 0];
    var key = new byte[Length];
    for (var i = 0; i < known.Length; ++i) key[i] = (byte)(body[i] ^ known[i]);

    // The page CRC is linear in the page bytes (no initial value, no final XOR), so the CRC of the page
    // with candidate serial bytes s14,s15 is CRC(page with zeros there) XOR the CRC of those two bytes
    // alone at their offsets.
    var page = body[..pageLength].ToArray();
    known.CopyTo(page);
    page[14] = page[15] = 0;
    var stored = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(22));
    page[22] = page[23] = page[24] = page[25] = 0;
    var baseCrc = OggCrc(page);
    Span<uint> unit = stackalloc uint[16];
    var probe = new byte[pageLength];
    for (var bit = 0; bit < 16; ++bit) {
      probe[14 + bit / 8] = (byte)(1 << (bit % 8));
      unit[bit] = OggCrc(probe);
      probe[14 + bit / 8] = 0;
    }
    int found = -1, matches = 0;
    for (var candidate = 0; candidate < 0x10000; ++candidate) {
      var crc = baseCrc;
      for (var bit = 0; bit < 16; ++bit)
        if ((candidate & (1 << bit)) != 0) crc ^= unit[bit];
      if (crc != stored) continue;
      found = candidate;
      ++matches;
    }
    if (matches != 1) return null;
    key[14] = (byte)(body[14] ^ (found & 0xFF));
    key[15] = (byte)(body[15] ^ (found >> 8));
    return key;
  }

  private static uint OggCrc(ReadOnlySpan<byte> data) {
    var crc = 0u;
    foreach (var b in data) {
      crc ^= (uint)b << 24;
      for (var i = 0; i < 8; ++i)
        crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
    }
    return crc;
  }

  private static void Xor(Span<byte> data, ReadOnlySpan<byte> key) {
    for (var i = 0; i < Length && i < data.Length; ++i) data[i] ^= key[i];
  }

  private static void CheckKey(ReadOnlySpan<byte> key) {
    if (key.Length != Length)
      throw new ArgumentException($"RPG Maker MV asset: the key is {Length} bytes.", nameof(key));
  }

  /// <summary>Formats a key as the 32 lower-case hex digits System.json stores.</summary>
  public static string FormatKey(ReadOnlySpan<byte> key) => Convert.ToHexStringLower(key);
}
