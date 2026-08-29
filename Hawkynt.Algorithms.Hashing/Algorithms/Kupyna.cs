using System.Buffers.Binary;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// DSTU 7564:2014 (Kupyna) hash family. The 256-, 384- and 512-bit digests share one
/// permutation/compression implementation; the digest size selects the state width and truncation.
/// </summary>
public static class Kupyna {
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [new(256, 512, 128)];

  private static readonly byte[] S0 = Convert.FromHexString("A8435F066B756C5971DF879517F0D8096DF31DCBC94D2CAF79E097FD6F4B45393EDDA34FB4B69A0E1FBF15E149D293C692729E61D163FAEEF419D5AD58A4BBA1DCF2833742E47A329CCCAB4A8F6E04272EE7E25A9616232BC265660FBCA947413448FCB76A88A55386F95BDB387BC31E2233242836C7B23B8E77BAF5149F08559B4CFE605CDA1846CD7D21B03F1B89FFEB84693A9DD7D3706740B5DE5D3091B1781101E5006898A0C502A6742D0BA276B3BECEBDAEE98A311CECF19994AAF6262FEFE88C3503D47FFB05C15E90203D82F7EA0A0D7EF8501AC40757B83C62E3C8AC526410D0D9130C122951B9CFD6738D8154C0ED4E44A72A8525E6CA7C8B5680");
  private static readonly byte[] S1 = Convert.FromHexString("CEBBEB92EACB13C1E93AD6B2D29017F8421556B4651C8843C55C36BAF557678D31F664589EF422AA750F02B1DF6D734D7C262EF7085D443E9F14C8AE5410D8BC1A6B69F3BD33ABFAD19B684E169591EE4C638E5BCC3C19A181497BD96F3760CAE72B48FD9645FC41120D79E5898CE32030DCB76C4AB53F97D4622D06A4A5835F2ADAC9007EA255BF11D59CCF0E0A3D517D931BFEC44709860B8F9D6A07B9B0981832714BEF3B70A0E440FFC3A9E678F98B46801E38E1B8A8E00C23761D252405F16E94289A84E8A34F77D385E252F282507A2F7453B361AF3935DECD1F99ACAD722CDDD087BE5EA6EC04C60334FBDB59B6C201F05AEDA766217F8A27C7C029D7");
  private static readonly byte[] S2 = Convert.FromHexString("93D99AB5982245FCBA6ADF029FDC51594A172BC294F4BBA362E471D4CD7016E1493CC0D85C9BAD8553A17AC82DE0D172A62CC4E37678B7B4093B0E414CDEB29025A5D7031100C32E92EF4E129D7DCB3510D54F9E4DA955C6D07B1897D336E64856818F77CC9CB9E2ACB82F15A47CDA381E0B05D6146E6C7E66FDB1E560AF5E3387C9F05D6D3F888DC7F71DE9ECED802927CF99A8500F3724283095D23E5B4083B369571F071C8ABC20EBCE8EABEE31A273F9CA3A1AFB0DC1FEFAF26FBD96DD4352B608F3AEBE19893226B0EA4B6484826BF579BF015F75631B233D682A65E891F6FF1358F1470A7FC5A7E7615A0646444204A0DB398654AA8C34218BF80C7467");
  private static readonly byte[] S3 = Convert.FromHexString("688DCA4D734B4E2AD45226B3541E191F2203463D2D4A5383138AB7D52579F5BD582F0D02ED519E11F23E555ED1163C66705DF34540CCE8945608CE1A3AD2E1DFB5386E0EE5F4F986E94FD68523CF32993114AEEEC848D330A19241B118C42C71724415FD37BE5FAA9B88D8AB899CFA60EABC620C24A6A8EC6720DB7C28DDAC5B347E10F17B8F63A0059A437721BF2709C39FB6D729C2EBC0A48B8C1DFBFFC1B2972EF865F67507044933E4D9B9D042C76C90008E6F5001C5DA473FCD69A2E27AA7C6930F0A06E62B96A31CAF6A128439E7B082F7FE9D875C8135DEB4A5FC80EFCBBB6B76BA5A7D780B95E3AD74983B36646DDCF059A94C177F91B8C9571BE061");

  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 256) {
    if (!SupportedHashSizes.Supports(hashSizeBits))
      throw new ArgumentOutOfRangeException(nameof(hashSizeBits));

    var columns = hashSizeBits <= 256 ? 8 : 16;
    var rounds = columns == 8 ? 10 : 14;
    var blockSize = columns * sizeof(ulong);
    var state = new ulong[columns];
    state[0] = (ulong)blockSize;

    var offset = 0;
    while (offset + blockSize <= data.Length) {
      ProcessBlock(state, data.Slice(offset, blockSize), rounds);
      offset += blockSize;
    }

    Span<byte> final = stackalloc byte[128];
    var remainder = data[offset..];
    remainder.CopyTo(final);
    var length = remainder.Length;
    final[length++] = 0x80;

    var lengthPosition = blockSize - 12;
    if (length > lengthPosition) {
      final.Slice(length, blockSize - length).Clear();
      ProcessBlock(state, final[..blockSize], rounds);
      final[..blockSize].Clear();
      length = 0;
    }

    final.Slice(length, lengthPosition - length).Clear();
    var bitLength = checked((ulong)data.Length * 8UL);
    BinaryPrimitives.WriteUInt32LittleEndian(final.Slice(lengthPosition, 4), (uint)bitLength);
    BinaryPrimitives.WriteUInt64LittleEndian(final.Slice(lengthPosition + 4, 8), bitLength >> 32);
    ProcessBlock(state, final[..blockSize], rounds);

    var transformed = (ulong[])state.Clone();
    P(transformed, rounds);
    for (var i = 0; i < columns; ++i)
      state[i] ^= transformed[i];

    var result = new byte[hashSizeBits / 8];
    var neededWords = result.Length / sizeof(ulong);
    var firstWord = columns - neededWords;
    for (var i = 0; i < neededWords; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(i * sizeof(ulong)), state[firstWord + i]);
    return result;
  }

  private static void ProcessBlock(ulong[] state, ReadOnlySpan<byte> block, int rounds) {
    var columns = state.Length;
    var p = new ulong[columns];
    var q = new ulong[columns];
    for (var column = 0; column < columns; ++column) {
      var word = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(column * sizeof(ulong), sizeof(ulong)));
      p[column] = state[column] ^ word;
      q[column] = word;
    }

    P(p, rounds);
    Q(q, rounds);
    for (var column = 0; column < columns; ++column)
      state[column] ^= p[column] ^ q[column];
  }

  private static void P(ulong[] state, int rounds) {
    for (var round = 0; round < rounds; ++round) {
      ulong constant = (uint)round;
      for (var column = 0; column < state.Length; ++column) {
        state[column] ^= constant;
        constant += 0x10;
      }
      Transform(state);
    }
  }

  private static void Q(ulong[] state, int rounds) {
    for (var round = 0; round < rounds; ++round) {
      var constant = ((ulong)(((state.Length - 1) << 4) ^ round) << 56) | 0x00F0F0F0F0F0F0F3UL;
      for (var column = 0; column < state.Length; ++column) {
        unchecked { state[column] += constant; }
        unchecked { constant -= 0x1000000000000000UL; }
      }
      Transform(state);
    }
  }

  private static void Transform(ulong[] state) {
    ShiftRows(state);
    SubBytes(state);
    for (var column = 0; column < state.Length; ++column)
      state[column] = MixColumn(state[column]);
  }

  private static void SubBytes(ulong[] state) {
    for (var column = 0; column < state.Length; ++column) {
      var value = state[column];
      state[column] =
        (ulong)S0[(byte)value] |
        ((ulong)S1[(byte)(value >> 8)] << 8) |
        ((ulong)S2[(byte)(value >> 16)] << 16) |
        ((ulong)S3[(byte)(value >> 24)] << 24) |
        ((ulong)S0[(byte)(value >> 32)] << 32) |
        ((ulong)S1[(byte)(value >> 40)] << 40) |
        ((ulong)S2[(byte)(value >> 48)] << 48) |
        ((ulong)S3[(byte)(value >> 56)] << 56);
    }
  }

  private static ulong MixColumn(ulong value) {
    var x1 = ((value & 0x7F7F7F7F7F7F7F7FUL) << 1)
      ^ (((value & 0x8080808080808080UL) >> 7) * 0x1DUL);

    var u = RotateRight(value, 8) ^ value;
    u ^= RotateRight(u, 16);
    u ^= RotateRight(value, 48);

    var v = u ^ value ^ x1;
    v = ((v & 0x3F3F3F3F3F3F3F3FUL) << 2)
      ^ (((v & 0x8080808080808080UL) >> 6) * 0x1DUL)
      ^ (((v & 0x4040404040404040UL) >> 6) * 0x1DUL);

    return u ^ RotateRight(v, 32) ^ RotateRight(x1, 40) ^ RotateRight(x1, 48);
  }

  private static void ShiftRows(ulong[] state) {
    if (state.Length == 8) {
      Swap(ref state[0], ref state[4], 0xFFFFFFFF00000000UL);
      Swap(ref state[1], ref state[5], 0x00FFFFFFFF000000UL);
      Swap(ref state[2], ref state[6], 0x0000FFFFFFFF0000UL);
      Swap(ref state[3], ref state[7], 0x000000FFFFFFFF00UL);

      Swap(ref state[0], ref state[2], 0xFFFF0000FFFF0000UL);
      Swap(ref state[1], ref state[3], 0x00FFFF0000FFFF00UL);
      Swap(ref state[4], ref state[6], 0xFFFF0000FFFF0000UL);
      Swap(ref state[5], ref state[7], 0x00FFFF0000FFFF00UL);

      Swap(ref state[0], ref state[1], 0xFF00FF00FF00FF00UL);
      Swap(ref state[2], ref state[3], 0xFF00FF00FF00FF00UL);
      Swap(ref state[4], ref state[5], 0xFF00FF00FF00FF00UL);
      Swap(ref state[6], ref state[7], 0xFF00FF00FF00FF00UL);
      return;
    }

    Swap(ref state[0], ref state[8], 0xFF00000000000000UL);
    Swap(ref state[1], ref state[9], 0xFF00000000000000UL);
    Swap(ref state[2], ref state[10], 0xFFFF000000000000UL);
    Swap(ref state[3], ref state[11], 0xFFFFFF0000000000UL);
    Swap(ref state[4], ref state[12], 0xFFFFFFFF00000000UL);
    Swap(ref state[5], ref state[13], 0x00FFFFFFFF000000UL);
    Swap(ref state[6], ref state[14], 0x00FFFFFFFFFF0000UL);
    Swap(ref state[7], ref state[15], 0x00FFFFFFFFFFFF00UL);

    Swap(ref state[0], ref state[4], 0x00FFFFFF00000000UL);
    Swap(ref state[1], ref state[5], 0xFFFFFFFFFF000000UL);
    Swap(ref state[2], ref state[6], 0xFF00FFFFFFFF0000UL);
    Swap(ref state[3], ref state[7], 0xFF0000FFFFFFFF00UL);
    Swap(ref state[8], ref state[12], 0x00FFFFFF00000000UL);
    Swap(ref state[9], ref state[13], 0xFFFFFFFFFF000000UL);
    Swap(ref state[10], ref state[14], 0xFF00FFFFFFFF0000UL);
    Swap(ref state[11], ref state[15], 0xFF0000FFFFFFFF00UL);

    Swap(ref state[0], ref state[2], 0xFFFF0000FFFF0000UL);
    Swap(ref state[1], ref state[3], 0x00FFFF0000FFFF00UL);
    Swap(ref state[4], ref state[6], 0xFFFF0000FFFF0000UL);
    Swap(ref state[5], ref state[7], 0x00FFFF0000FFFF00UL);
    Swap(ref state[8], ref state[10], 0xFFFF0000FFFF0000UL);
    Swap(ref state[9], ref state[11], 0x00FFFF0000FFFF00UL);
    Swap(ref state[12], ref state[14], 0xFFFF0000FFFF0000UL);
    Swap(ref state[13], ref state[15], 0x00FFFF0000FFFF00UL);

    for (var column = 0; column < 16; column += 2)
      Swap(ref state[column], ref state[column + 1], 0xFF00FF00FF00FF00UL);
  }

  private static void Swap(ref ulong left, ref ulong right, ulong mask) {
    var delta = (left ^ right) & mask;
    left ^= delta;
    right ^= delta;
  }

  private static ulong RotateRight(ulong value, int count) =>
    (value >> count) | (value << (64 - count));
}
