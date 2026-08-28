using System.Buffers.Binary;
using System.Text;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// Tiger, the 192-bit hash by Ross Anderson and Eli Biham.
/// </summary>
/// <remarks>
/// Rather than vendoring 1,024 opaque 64-bit S-box constants, this implementation uses the
/// authors' published S-box generation algorithm. The four standard tables are deterministically
/// generated once from the identity tables and the 64-byte Tiger provenance string.
/// </remarks>
public static class Tiger {
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [HashSizeRange.Exact(192)];

  private const ulong InitialA = 0x0123456789ABCDEFUL;
  private const ulong InitialB = 0xFEDCBA9876543210UL;
  private const ulong InitialC = 0xF096A5B4C3B2E187UL;
  private static readonly ulong[] SBoxes = GenerateSBoxes();

  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 192) {
    if (!SupportedHashSizes.Supports(hashSizeBits))
      throw new ArgumentOutOfRangeException(nameof(hashSizeBits));

    var a = InitialA;
    var b = InitialB;
    var c = InitialC;
    var offset = 0;
    while (offset + 64 <= data.Length) {
      CompressBlock(data.Slice(offset, 64), ref a, ref b, ref c, SBoxes);
      offset += 64;
    }

    Span<byte> final = stackalloc byte[64];
    var remainder = data[offset..];
    remainder.CopyTo(final);
    var length = remainder.Length;
    final[length++] = 0x01; // Tiger padding; Tiger2 uses 0x80 instead.

    if (length > 56) {
      final[length..].Clear();
      CompressBlock(final, ref a, ref b, ref c, SBoxes);
      final.Clear();
      length = 0;
    }

    final.Slice(length, 56 - length).Clear();
    BinaryPrimitives.WriteUInt64LittleEndian(final[56..], checked((ulong)data.Length * 8UL));
    CompressBlock(final, ref a, ref b, ref c, SBoxes);

    var result = new byte[24];
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0, 8), a);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8, 8), b);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16, 8), c);
    return result;
  }

  private static void CompressBlock(ReadOnlySpan<byte> block, ref ulong a, ref ulong b, ref ulong c, ulong[] table) {
    Span<ulong> x = stackalloc ulong[8];
    for (var i = 0; i < x.Length; ++i)
      x[i] = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8));

    var aa = a;
    var bb = b;
    var cc = c;

    Pass(ref a, ref b, ref c, x, 5, table);
    KeySchedule(x);
    Pass(ref c, ref a, ref b, x, 7, table);
    KeySchedule(x);
    Pass(ref b, ref c, ref a, x, 9, table);

    a ^= aa;
    unchecked { b -= bb; }
    unchecked { c += cc; }
  }

  private static void Pass(ref ulong a, ref ulong b, ref ulong c, ReadOnlySpan<ulong> x, ulong multiplier, ulong[] table) {
    Round(ref a, ref b, ref c, x[0], multiplier, table);
    Round(ref b, ref c, ref a, x[1], multiplier, table);
    Round(ref c, ref a, ref b, x[2], multiplier, table);
    Round(ref a, ref b, ref c, x[3], multiplier, table);
    Round(ref b, ref c, ref a, x[4], multiplier, table);
    Round(ref c, ref a, ref b, x[5], multiplier, table);
    Round(ref a, ref b, ref c, x[6], multiplier, table);
    Round(ref b, ref c, ref a, x[7], multiplier, table);
  }

  private static void Round(ref ulong a, ref ulong b, ref ulong c, ulong x, ulong multiplier, ulong[] table) {
    c ^= x;
    unchecked {
      a -= table[(byte)c]
        ^ table[256 + (byte)(c >> 16)]
        ^ table[512 + (byte)(c >> 32)]
        ^ table[768 + (byte)(c >> 48)];
      b += table[768 + (byte)(c >> 8)]
        ^ table[512 + (byte)(c >> 24)]
        ^ table[256 + (byte)(c >> 40)]
        ^ table[(byte)(c >> 56)];
      b *= multiplier;
    }
  }

  private static void KeySchedule(Span<ulong> x) {
    unchecked {
      x[0] -= x[7] ^ 0xA5A5A5A5A5A5A5A5UL;
      x[1] ^= x[0];
      x[2] += x[1];
      x[3] -= x[2] ^ (~x[1] << 19);
      x[4] ^= x[3];
      x[5] += x[4];
      x[6] -= x[5] ^ (~x[4] >> 23);
      x[7] ^= x[6];
      x[0] += x[7];
      x[1] -= x[0] ^ (~x[7] << 19);
      x[2] ^= x[1];
      x[3] += x[2];
      x[4] -= x[3] ^ (~x[2] >> 23);
      x[5] ^= x[4];
      x[6] += x[5];
      x[7] -= x[6] ^ 0x0123456789ABCDEFUL;
    }
  }

  private static ulong[] GenerateSBoxes() {
    var table = new ulong[1024];
    for (var i = 0; i < table.Length; ++i)
      table[i] = (ulong)(i & 0xff) * 0x0101010101010101UL;

    var a = InitialA;
    var b = InitialB;
    var c = InitialC;
    var seed = Encoding.ASCII.GetBytes("Tiger - A Fast New Hash Function, by Ross Anderson and Eli Biham");
    var stateIndex = 2;

    for (var pass = 0; pass < 5; ++pass) {
      for (var i = 0; i < 256; ++i) {
        for (var box = 0; box < 1024; box += 256) {
          if (++stateIndex == 3) {
            stateIndex = 0;
            CompressBlock(seed, ref a, ref b, ref c, table);
          }

          var state = stateIndex switch { 0 => a, 1 => b, _ => c };
          var leftIndex = box + i;
          for (var column = 0; column < 8; ++column) {
            var rightIndex = box + (byte)(state >> (column * 8));
            SwapByte(table, leftIndex, rightIndex, column);
          }
        }
      }
    }

    return table;
  }

  private static void SwapByte(ulong[] table, int leftIndex, int rightIndex, int byteIndex) {
    var shift = byteIndex * 8;
    var mask = 0xffUL << shift;
    var leftByte = table[leftIndex] & mask;
    var rightByte = table[rightIndex] & mask;
    table[leftIndex] = (table[leftIndex] & ~mask) | rightByte;
    table[rightIndex] = (table[rightIndex] & ~mask) | leftByte;
  }
}
