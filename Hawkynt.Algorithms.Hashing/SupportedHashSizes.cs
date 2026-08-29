namespace Hawkynt.Algorithms.Hashing;

internal static class HashSizeSets {
  internal static readonly IReadOnlyList<HashSizeRange> Bits32 = [HashSizeRange.Exact(32)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits64 = [HashSizeRange.Exact(64)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits128 = [HashSizeRange.Exact(128)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits160 = [HashSizeRange.Exact(160)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits192 = [HashSizeRange.Exact(192)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits224 = [HashSizeRange.Exact(224)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits256 = [HashSizeRange.Exact(256)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits320 = [HashSizeRange.Exact(320)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits384 = [HashSizeRange.Exact(384)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits512 = [HashSizeRange.Exact(512)];

  internal static readonly IReadOnlyList<HashSizeRange> Bits32And64 = [new(32, 64, 32)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits32And128 = [HashSizeRange.Exact(32), HashSizeRange.Exact(128)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits64And128 = [new(64, 128, 64)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits128To256 = [new(128, 256, 32)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits192To512 = [new(192, 256, 32), new(384, 512, 128)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits224To512 = [new(224, 256, 32), new(384, 512, 128)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits8To512 = [new(8, 512, 8)];
}

/// <summary>
/// Adds uniform output-size metadata to fixed-size and finite multi-output hash APIs that predate
/// the family metadata convention. C# 14 static extension properties keep compatibility wrappers
/// lightweight while making <c>HashType.SupportedHashSizes</c> available to callers consistently.
/// Extendable-output functions are intentionally excluded because their output length is not a
/// finite set of hash sizes.
/// </summary>
public static class HashOutputSizeExtensions {
  extension(AsconHash) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Blake) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224To512; }
  extension(Blake2s) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(CityHash) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits64; }
  extension(Comb4PMd4Md5) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Comb4PSha1Ripemd160) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits320; }
  extension(CubeHash256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(CubeHash512) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512; }
  extension(DarkCryptMd6) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512; }
  extension(DarkCryptSkein) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512; }
  extension(Echo224) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224; }
  extension(Echo256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Echo384) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits384; }
  extension(Echo512) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512; }
  extension(Esch256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Esch384) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits384; }
  extension(DarkCryptKeccak) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512; }
  extension(Gimli24Hash) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(ChcHash) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128; }
  extension(Mdc2) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128; }
  extension(Fnv) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits32And64; }
  extension(MurmurHash3) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits32And128; }
  extension(SipHash24) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits64; }
  extension(Gost3411_94) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Groestl224) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224; }
  extension(Groestl256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Groestl384) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits384; }
  extension(Groestl512) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512; }
  extension(Haraka256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Haraka512) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Haval) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128To256; }
  extension(IsapHash) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Sha3) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224To512; }
  extension(Md2) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128; }
  extension(Md4) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128; }
  extension(Sm3) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Lsh224) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224; }
  extension(Lsh256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Lsh384) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits384; }
  extension(Lsh512) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512; }
  extension(Lsh512_256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Luffa224) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224; }
  extension(Luffa256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Luffa384) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits384; }
  extension(Luffa512) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512; }
  extension(PanamaLE) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(PanamaBE) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(PhotonBeetleHash) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(SparkleHash) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(XxHash) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits32And64; }
  extension(Ripemd128) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128; }
  extension(Ripemd160) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits160; }
  extension(Ripemd256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Ripemd320) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits320; }
  extension(Shabal192) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits192; }
  extension(Shabal224) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224; }
  extension(Shabal256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Shabal384) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits384; }
  extension(Shabal512) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512; }
  extension(Skein512) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512; }
  extension(Streebog256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Streebog512) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512; }
  extension(SubterraneanHash) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(Whirlpool) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512; }
  extension(XoodyakHash) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(XxHash3) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits64And128; }
}
