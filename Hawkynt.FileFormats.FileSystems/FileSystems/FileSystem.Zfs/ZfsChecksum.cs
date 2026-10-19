using System.Buffers.Binary;
using System.Security.Cryptography;
using Hawkynt.Algorithms.Hashing;

namespace FileSystem.Zfs;

/// <summary>Outcome of checking a block against the checksum its pointer carries.</summary>
internal enum ZfsChecksumResult {
  Match,
  Mismatch,
  /// <summary>
  /// A salted algorithm (Skein, Edon-R, BLAKE3 are keyed with the pool's checksum salt),
  /// which is not implemented; the block is read unverified.
  /// </summary>
  NotVerified,
}

/// <summary>
/// The block checksums the reader verifies (<c>docs/ZFS-ON-DISK.md</c>, "Digests as stored").
/// </summary>
internal static class ZfsChecksum {
  public static ZfsChecksumResult Verify(byte algorithm, ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected) {
    Span<byte> actual = stackalloc byte[32];
    switch (algorithm) {
      case ZfsConstants.ChecksumOff or ZfsConstants.ChecksumNoParity:
        return ZfsChecksumResult.Match;
      case ZfsConstants.ChecksumFletcher4:
        Fletcher4(data, actual);
        break;
      case ZfsConstants.ChecksumFletcher2:
        Fletcher2(data, actual);
        break;
      case ZfsConstants.ChecksumSha256:
        Sha256Words(data, actual);
        break;
      case ZfsConstants.ChecksumSha512:
        // Unlike SHA-256, SHA-512/256 is stored as the digest bytes in order.
        Sha512Family.Compute512_256(data).CopyTo(actual);
        break;
      default:
        return ZfsChecksumResult.NotVerified;
    }
    return actual.SequenceEqual(expected) ? ZfsChecksumResult.Match : ZfsChecksumResult.Mismatch;
  }

  /// <summary>Fletcher-4 over little-endian <c>u32</c> words, four <c>u64</c> sums.</summary>
  public static void Fletcher4(ReadOnlySpan<byte> data, Span<byte> destination) {
    ulong a = 0, b = 0, c = 0, d = 0;
    for (var i = 0; i + 4 <= data.Length; i += 4) {
      a += BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
      b += a;
      c += b;
      d += c;
    }
    WriteWords(destination, a, b, c, d);
  }

  /// <summary>Fletcher-2 over pairs of little-endian <c>u64</c> words.</summary>
  public static void Fletcher2(ReadOnlySpan<byte> data, Span<byte> destination) {
    ulong a0 = 0, a1 = 0, b0 = 0, b1 = 0;
    for (var i = 0; i + 16 <= data.Length; i += 16) {
      a0 += BinaryPrimitives.ReadUInt64LittleEndian(data[i..]);
      a1 += BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 8)..]);
      b0 += a0;
      b1 += a1;
    }
    WriteWords(destination, a0, a1, b0, b1);
  }

  /// <summary>SHA-256 as ZFS stores it: each eight digest bytes read big-endian into a native word.</summary>
  public static void Sha256Words(ReadOnlySpan<byte> data, Span<byte> destination) {
    Span<byte> digest = stackalloc byte[32];
    SHA256.HashData(data, digest);
    DigestWords(digest, destination);
  }

  private static void DigestWords(ReadOnlySpan<byte> digest, Span<byte> destination) {
    for (var i = 0; i < 32; i += 8)
      BinaryPrimitives.WriteUInt64LittleEndian(destination[i..], BinaryPrimitives.ReadUInt64BigEndian(digest[i..]));
  }

  /// <summary>
  /// Checks a label-area block that carries its own SHA-256 in a trailing <c>zio_eck_t</c>,
  /// with the device offset of the block as the verifier.
  /// </summary>
  public static bool VerifyEmbedded(ReadOnlySpan<byte> block, long deviceOffset) {
    if (block.Length < 40) return false;
    var tail = block.Length - 40;
    if (BinaryPrimitives.ReadUInt64LittleEndian(block[tail..]) != ZfsConstants.EmbeddedChecksumMagic)
      return false;
    var copy = block.ToArray();
    var checksum = copy.AsSpan(tail + 8, 32);
    var expected = checksum.ToArray();
    checksum.Clear();
    BinaryPrimitives.WriteInt64LittleEndian(checksum, deviceOffset);
    Span<byte> actual = stackalloc byte[32];
    Sha256Words(copy, actual);
    return actual.SequenceEqual(expected);
  }

  private static void WriteWords(Span<byte> destination, ulong w0, ulong w1, ulong w2, ulong w3) {
    BinaryPrimitives.WriteUInt64LittleEndian(destination, w0);
    BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], w1);
    BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], w2);
    BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], w3);
  }
}
