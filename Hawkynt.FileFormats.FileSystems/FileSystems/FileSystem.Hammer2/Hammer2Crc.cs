namespace FileSystem.Hammer2;

/// <summary>
/// The two integrity primitives HAMMER2 (DragonFly BSD) stamps on disk,
/// mirroring <c>sys/libkern/icrc32.c</c> and <c>sys/libkern/xxhash/xxhash.c</c>.
///
/// <list type="bullet">
///   <item><description><see cref="Iscsi32"/> — the iSCSI / Castagnoli reflected
///   CRC-32C (polynomial <c>0x82F63B78</c>, init/xorout <c>0xFFFFFFFF</c>). Used
///   for the volume-header sector iCRCs (<c>icrc_sects[]</c> and
///   <c>icrc_volheader</c>), for the directory-name hash
///   (<c>hammer2_dirhash</c>) and for blockrefs whose check method is
///   <c>HAMMER2_CHECK_ISCSI32</c>.</description></item>
///   <item><description><see cref="XxHash64"/> — the 64-bit xxHash that backs
///   <c>HAMMER2_CHECK_XXHASH64</c> (the default), seeded with HAMMER2's fixed
///   <c>XXH_HAMMER2_SEED = 0x4d617474446c6c6e</c>.</description></item>
/// </list>
/// </summary>
internal static class Hammer2Crc {
  private static readonly uint[] Table32C = BuildTable(0x82F63B78u);

  private static uint[] BuildTable(uint poly) {
    var table = new uint[256];
    for (var n = 0u; n < 256; ++n) {
      var c = n;
      for (var k = 0; k < 8; ++k)
        c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
      table[n] = c;
    }
    return table;
  }

  /// <summary>iSCSI / Castagnoli reflected CRC-32C (<c>hammer2_icrc32</c>).</summary>
  public static uint Iscsi32(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data)
      crc = Table32C[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
  }

  /// <summary>
  /// HAMMER2's directory-name hash (<c>hammer2_dirhash</c> in
  /// <c>sys/vfs/hammer2/hammer2_subr.c</c>): the top 32 bits hold a CRC over the
  /// name split on <c>. - _ ~</c> with bit 63 forced set; bits 31..16 hold a
  /// full-name CRC self-xored with its &lt;&lt;16; bit 15 is forced set so
  /// readdir can reserve the 0x0000-0x7FFF range for "." and "..".
  /// </summary>
  public static ulong DirHash(ReadOnlySpan<byte> name) {
    uint crcx = 0;
    var j = 0;
    for (var i = 0; i < name.Length; ++i) {
      var ch = name[i];
      if (ch is (byte)'.' or (byte)'-' or (byte)'_' or (byte)'~') {
        if (i != j)
          crcx += Iscsi32(name[j..i]);
        j = i + 1;
      }
    }
    if (name.Length != j)
      crcx += Iscsi32(name[j..]);

    crcx |= 0x80000000u;
    var key = (ulong)crcx << 32;

    var full = Iscsi32(name);
    full ^= full << 16;
    key |= full & 0xFFFF0000u;
    key |= 0x8000u;
    return key;
  }

  // ===== xxHash64 (seeded), HAMMER2_CHECK_XXHASH64 =====
  private const ulong Prime1 = 0x9E3779B185EBCA87UL;
  private const ulong Prime2 = 0xC2B2AE3D27D4EB4FUL;
  private const ulong Prime3 = 0x165667B19E3779F9UL;
  private const ulong Prime4 = 0x85EBCA77C2B2AE63UL;
  private const ulong Prime5 = 0x27D4EB2F165667C5UL;

  /// <summary>HAMMER2's fixed xxHash seed (<c>XXH_HAMMER2_SEED</c>).</summary>
  public const ulong Hammer2Seed = 0x4d617474446c6c6eUL;

  private static ulong Rotl(ulong x, int r) => (x << r) | (x >> (64 - r));

  private static ulong Round(ulong acc, ulong input) {
    acc += input * Prime2;
    acc = Rotl(acc, 31);
    acc *= Prime1;
    return acc;
  }

  private static ulong MergeRound(ulong acc, ulong val) {
    val = Round(0, val);
    acc ^= val;
    acc = acc * Prime1 + Prime4;
    return acc;
  }

  /// <summary>64-bit xxHash of <paramref name="data"/> with the given seed.</summary>
  public static ulong XxHash64(ReadOnlySpan<byte> data, ulong seed) {
    var len = (ulong)data.Length;
    ulong h64;
    var p = 0;

    if (data.Length >= 32) {
      var v1 = seed + Prime1 + Prime2;
      var v2 = seed + Prime2;
      var v3 = seed;
      var v4 = seed - Prime1;

      var limit = data.Length - 32;
      do {
        v1 = Round(v1, Read64(data, p)); p += 8;
        v2 = Round(v2, Read64(data, p)); p += 8;
        v3 = Round(v3, Read64(data, p)); p += 8;
        v4 = Round(v4, Read64(data, p)); p += 8;
      } while (p <= limit);

      h64 = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
      h64 = MergeRound(h64, v1);
      h64 = MergeRound(h64, v2);
      h64 = MergeRound(h64, v3);
      h64 = MergeRound(h64, v4);
    } else {
      h64 = seed + Prime5;
    }

    h64 += len;

    while (p + 8 <= data.Length) {
      var k1 = Round(0, Read64(data, p));
      h64 ^= k1;
      h64 = Rotl(h64, 27) * Prime1 + Prime4;
      p += 8;
    }

    if (p + 4 <= data.Length) {
      h64 ^= Read32(data, p) * Prime1;
      h64 = Rotl(h64, 23) * Prime2 + Prime3;
      p += 4;
    }

    while (p < data.Length) {
      h64 ^= data[p] * Prime5;
      h64 = Rotl(h64, 11) * Prime1;
      ++p;
    }

    h64 ^= h64 >> 33;
    h64 *= Prime2;
    h64 ^= h64 >> 29;
    h64 *= Prime3;
    h64 ^= h64 >> 32;
    return h64;
  }

  private static ulong Read64(ReadOnlySpan<byte> d, int off) =>
    System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(off, 8));

  private static uint Read32(ReadOnlySpan<byte> d, int off) =>
    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(off, 4));
}
