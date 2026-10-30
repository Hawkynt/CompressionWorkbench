using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FileFormat.Luks;

/// <summary>
/// The LUKS anti-forensic information splitter (AFsplit / AFmerge, LUKS1 specification §2.4):
/// a key is spread over <c>stripes</c> blocks joined by a hash-based diffusion.
/// </summary>
internal static class LuksAntiForensic {
  public static byte[] Merge(ReadOnlySpan<byte> material, int keyBytes, int stripes, HashAlgorithmName hash) {
    if (stripes < 1 || material.Length < (long)keyBytes * stripes)
      throw new InvalidDataException("LUKS: key material shorter than key size × stripes.");
    var d = new byte[keyBytes];
    for (var i = 0; i < stripes - 1; ++i) {
      Xor(d, material.Slice(i * keyBytes, keyBytes));
      Diffuse(d, hash);
    }
    Xor(d, material.Slice((stripes - 1) * keyBytes, keyBytes));
    return d;
  }

  public static byte[] Split(ReadOnlySpan<byte> key, int stripes, HashAlgorithmName hash) {
    var keyBytes = key.Length;
    var material = new byte[keyBytes * stripes];
    RandomNumberGenerator.Fill(material.AsSpan(0, keyBytes * (stripes - 1)));
    var d = new byte[keyBytes];
    for (var i = 0; i < stripes - 1; ++i) {
      Xor(d, material.AsSpan(i * keyBytes, keyBytes));
      Diffuse(d, hash);
    }
    var last = material.AsSpan((stripes - 1) * keyBytes, keyBytes);
    for (var k = 0; k < keyBytes; ++k) last[k] = (byte)(d[k] ^ key[k]);
    return material;
  }

  private static void Xor(Span<byte> target, ReadOnlySpan<byte> source) {
    for (var k = 0; k < target.Length; ++k) target[k] ^= source[k];
  }

  /// <summary>Hashes each digest-sized piece prefixed by its big-endian index; the last piece may be short.</summary>
  private static void Diffuse(Span<byte> block, HashAlgorithmName hash) {
    using var h = IncrementalHash.CreateHash(hash);
    var digestSize = h.HashLengthInBytes;
    Span<byte> index = stackalloc byte[4];
    Span<byte> digest = stackalloc byte[64];
    for (int i = 0, offset = 0; offset < block.Length; ++i, offset += digestSize) {
      var piece = block.Slice(offset, Math.Min(digestSize, block.Length - offset));
      BinaryPrimitives.WriteUInt32BigEndian(index, (uint)i);
      h.AppendData(index);
      h.AppendData(piece);
      h.GetHashAndReset(digest);
      digest[..piece.Length].CopyTo(piece);
    }
  }

  public static HashAlgorithmName HashName(string spec) => spec.ToLowerInvariant() switch {
    "sha1" => HashAlgorithmName.SHA1,
    "sha256" => HashAlgorithmName.SHA256,
    "sha384" => HashAlgorithmName.SHA384,
    "sha512" => HashAlgorithmName.SHA512,
    _ => throw new NotSupportedException($"LUKS hash '{spec}' is not supported (sha1/sha256/sha384/sha512 are)."),
  };
}
