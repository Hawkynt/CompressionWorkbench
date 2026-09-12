using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Ba2;

/// <summary>
/// Bob Jenkins' lookup3 hash, byte-stream variant. Bethesda hashes BA2 directory and basename strings
/// with this function over the lowercase UTF-8 (effectively ASCII) bytes of the path component.
/// </summary>
/// <remarks>
/// Reference: lookup3.c, hashlittle(), Bob Jenkins, public domain.
/// We deliberately do not use the aligned fast path — Bethesda hashes paths that are rarely 4-aligned
/// in practice anyway, and the byte-tail variant is portable across endianness without word-boundary
/// reads. The mix/final permutations are unchanged from Jenkins.
/// </remarks>
public static class BethesdaLookup3 {

  /// <summary>Hashes the given UTF-8/ASCII bytes with lookup3 (initval = 0).</summary>
  public static uint Hash(ReadOnlySpan<byte> bytes) {
    var len = bytes.Length;

    // Each accumulator starts at the magic constant + length. The "+ length" is what makes
    // empty input still produce a non-trivial result, and what binds the hash to the byte count.
    var a = 0xDEADBEEFu + (uint)len;
    var b = a;
    var c = a;

    var i = 0;
    while (len > 12) {
      a += BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i, 4));
      b += BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i + 4, 4));
      c += BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i + 8, 4));
      Mix(ref a, ref b, ref c);
      i += 12;
      len -= 12;
    }

    // Tail packing follows lookup3.c verbatim. Length 0 is the only case where Final is skipped:
    // an empty input returns the seeded `c` (= 0xDEADBEEF) directly per Jenkins' implementation.
    switch (len) {
      case 12:
        c += (uint)bytes[i + 11] << 24;
        c += (uint)bytes[i + 10] << 16;
        c += (uint)bytes[i + 9] << 8;
        c += bytes[i + 8];
        b += (uint)bytes[i + 7] << 24;
        b += (uint)bytes[i + 6] << 16;
        b += (uint)bytes[i + 5] << 8;
        b += bytes[i + 4];
        a += (uint)bytes[i + 3] << 24;
        a += (uint)bytes[i + 2] << 16;
        a += (uint)bytes[i + 1] << 8;
        a += bytes[i + 0];
        break;
      case 11:
        c += (uint)bytes[i + 10] << 16;
        c += (uint)bytes[i + 9] << 8;
        c += bytes[i + 8];
        b += (uint)bytes[i + 7] << 24;
        b += (uint)bytes[i + 6] << 16;
        b += (uint)bytes[i + 5] << 8;
        b += bytes[i + 4];
        a += (uint)bytes[i + 3] << 24;
        a += (uint)bytes[i + 2] << 16;
        a += (uint)bytes[i + 1] << 8;
        a += bytes[i + 0];
        break;
      case 10:
        c += (uint)bytes[i + 9] << 8;
        c += bytes[i + 8];
        b += (uint)bytes[i + 7] << 24;
        b += (uint)bytes[i + 6] << 16;
        b += (uint)bytes[i + 5] << 8;
        b += bytes[i + 4];
        a += (uint)bytes[i + 3] << 24;
        a += (uint)bytes[i + 2] << 16;
        a += (uint)bytes[i + 1] << 8;
        a += bytes[i + 0];
        break;
      case 9:
        c += bytes[i + 8];
        b += (uint)bytes[i + 7] << 24;
        b += (uint)bytes[i + 6] << 16;
        b += (uint)bytes[i + 5] << 8;
        b += bytes[i + 4];
        a += (uint)bytes[i + 3] << 24;
        a += (uint)bytes[i + 2] << 16;
        a += (uint)bytes[i + 1] << 8;
        a += bytes[i + 0];
        break;
      case 8:
        b += (uint)bytes[i + 7] << 24;
        b += (uint)bytes[i + 6] << 16;
        b += (uint)bytes[i + 5] << 8;
        b += bytes[i + 4];
        a += (uint)bytes[i + 3] << 24;
        a += (uint)bytes[i + 2] << 16;
        a += (uint)bytes[i + 1] << 8;
        a += bytes[i + 0];
        break;
      case 7:
        b += (uint)bytes[i + 6] << 16;
        b += (uint)bytes[i + 5] << 8;
        b += bytes[i + 4];
        a += (uint)bytes[i + 3] << 24;
        a += (uint)bytes[i + 2] << 16;
        a += (uint)bytes[i + 1] << 8;
        a += bytes[i + 0];
        break;
      case 6:
        b += (uint)bytes[i + 5] << 8;
        b += bytes[i + 4];
        a += (uint)bytes[i + 3] << 24;
        a += (uint)bytes[i + 2] << 16;
        a += (uint)bytes[i + 1] << 8;
        a += bytes[i + 0];
        break;
      case 5:
        b += bytes[i + 4];
        a += (uint)bytes[i + 3] << 24;
        a += (uint)bytes[i + 2] << 16;
        a += (uint)bytes[i + 1] << 8;
        a += bytes[i + 0];
        break;
      case 4:
        a += (uint)bytes[i + 3] << 24;
        a += (uint)bytes[i + 2] << 16;
        a += (uint)bytes[i + 1] << 8;
        a += bytes[i + 0];
        break;
      case 3:
        a += (uint)bytes[i + 2] << 16;
        a += (uint)bytes[i + 1] << 8;
        a += bytes[i + 0];
        break;
      case 2:
        a += (uint)bytes[i + 1] << 8;
        a += bytes[i + 0];
        break;
      case 1:
        a += bytes[i + 0];
        break;
      case 0:
        return c;
    }

    Final(ref a, ref b, ref c);
    return c;
  }

  /// <summary>Hashes the lowercase ASCII form of <paramref name="text"/>. Used for both directory and basename hashes.</summary>
  public static uint HashLower(string text) {
    var lower = text.ToLowerInvariant();
    return Hash(Encoding.UTF8.GetBytes(lower));
  }

  private static void Mix(ref uint a, ref uint b, ref uint c) {
    a -= c; a ^= Rot(c, 4);  c += b;
    b -= a; b ^= Rot(a, 6);  a += c;
    c -= b; c ^= Rot(b, 8);  b += a;
    a -= c; a ^= Rot(c, 16); c += b;
    b -= a; b ^= Rot(a, 19); a += c;
    c -= b; c ^= Rot(b, 4);  b += a;
  }

  private static void Final(ref uint a, ref uint b, ref uint c) {
    c ^= b; c -= Rot(b, 14);
    a ^= c; a -= Rot(c, 11);
    b ^= a; b -= Rot(a, 25);
    c ^= b; c -= Rot(b, 16);
    a ^= c; a -= Rot(c, 4);
    b ^= a; b -= Rot(a, 14);
    c ^= b; c -= Rot(b, 24);
  }

  private static uint Rot(uint x, int k) => (x << k) | (x >> (32 - k));
}
