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
  /// <summary>
  /// Checks <paramref name="data"/>. A <paramref name="protectedBlock"/> (native encryption, not an
  /// object set) keeps only two words of checksum — the other two are its MAC — and a weak
  /// checksum (one not fit for dedup: Fletcher, Edon-R) folds its last two words into them.
  /// </summary>
  /// <param name="bigEndian">The block was written by a big-endian host: Fletcher sums run over big-endian words.</param>
  public static ZfsChecksumResult Verify(byte algorithm, ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected, bool protectedBlock = false, bool bigEndian = false) {
    Span<byte> actual = stackalloc byte[32];
    switch (algorithm) {
      case ZfsConstants.ChecksumOff or ZfsConstants.ChecksumNoParity:
        return ZfsChecksumResult.Match;
      case ZfsConstants.ChecksumFletcher4:
        Fletcher4(data, actual, bigEndian);
        break;
      case ZfsConstants.ChecksumFletcher2:
        Fletcher2(data, actual, bigEndian);
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
    if (!protectedBlock)
      return actual.SequenceEqual(expected) ? ZfsChecksumResult.Match : ZfsChecksumResult.Mismatch;
    Truncate(algorithm, actual);
    return actual[..16].SequenceEqual(expected[..16]) ? ZfsChecksumResult.Match : ZfsChecksumResult.Mismatch;
  }

  /// <summary>A protected block's two checksum words: weak checksums XOR words 2 and 3 into 0 and 1.</summary>
  public static void Truncate(byte algorithm, Span<byte> checksum) {
    if (algorithm is not (ZfsConstants.ChecksumFletcher2 or ZfsConstants.ChecksumFletcher4 or ZfsConstants.ChecksumEdonR)) return;
    BinaryPrimitives.WriteUInt64LittleEndian(checksum, BinaryPrimitives.ReadUInt64LittleEndian(checksum) ^ BinaryPrimitives.ReadUInt64LittleEndian(checksum[16..]));
    BinaryPrimitives.WriteUInt64LittleEndian(checksum[8..], BinaryPrimitives.ReadUInt64LittleEndian(checksum[8..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(checksum[24..]));
  }

  /// <summary>Fletcher-4 over little-endian (or, from a big-endian host, big-endian) <c>u32</c> words, four <c>u64</c> sums.</summary>
  public static void Fletcher4(ReadOnlySpan<byte> data, Span<byte> destination, bool bigEndian = false) {
    ulong a = 0, b = 0, c = 0, d = 0;
    for (var i = 0; i + 4 <= data.Length; i += 4) {
      a += bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[i..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
      b += a;
      c += b;
      d += c;
    }
    WriteWords(destination, a, b, c, d);
  }

  /// <summary>Fletcher-2 over pairs of little-endian <c>u64</c> words.</summary>
  public static void Fletcher2(ReadOnlySpan<byte> data, Span<byte> destination, bool bigEndian = false) {
    ulong a0 = 0, a1 = 0, b0 = 0, b1 = 0;
    for (var i = 0; i + 16 <= data.Length; i += 16) {
      a0 += bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(data[i..]) : BinaryPrimitives.ReadUInt64LittleEndian(data[i..]);
      a1 += bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(data[(i + 8)..]) : BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 8)..]);
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
  /// <param name="bigEndian">A big-endian host wrote the block: the magic, the verifier and the digest words are big-endian.</param>
  public static bool VerifyEmbedded(ReadOnlySpan<byte> block, long deviceOffset, bool bigEndian = false) {
    if (block.Length < 40) return false;
    var tail = block.Length - 40;
    var magic = bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(block[tail..]) : BinaryPrimitives.ReadUInt64LittleEndian(block[tail..]);
    if (magic != ZfsConstants.EmbeddedChecksumMagic)
      return false;
    var copy = block.ToArray();
    var checksum = copy.AsSpan(tail + 8, 32);
    var expected = checksum.ToArray();
    checksum.Clear();
    if (bigEndian) BinaryPrimitives.WriteInt64BigEndian(checksum, deviceOffset);
    else BinaryPrimitives.WriteInt64LittleEndian(checksum, deviceOffset);
    Span<byte> digest = stackalloc byte[32];
    SHA256.HashData(copy, digest);
    // the digest's words as the host stored them: big-endian readings, written in the host's order
    return bigEndian ? digest.SequenceEqual(expected) : SequenceEqualWords(digest, expected);
  }

  private static bool SequenceEqualWords(ReadOnlySpan<byte> digest, ReadOnlySpan<byte> expected) {
    Span<byte> words = stackalloc byte[32];
    DigestWords(digest, words);
    return words.SequenceEqual(expected);
  }

  private static void WriteWords(Span<byte> destination, ulong w0, ulong w1, ulong w2, ulong w3) {
    BinaryPrimitives.WriteUInt64LittleEndian(destination, w0);
    BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], w1);
    BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], w2);
    BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], w3);
  }
}
