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
  internal static readonly IReadOnlyList<HashSizeRange> Bits224To512 = [new(224, 256, 32), new(384, 512, 128)];
  internal static readonly IReadOnlyList<HashSizeRange> Bits8To512 = [new(8, 512, 8)];
}

// Static extension members keep compatibility wrappers source-compatible while making the same
// Type.SupportedHashSizes surface available everywhere. Each receiver uses its own containing
// class because C# lowers static extension properties to parameterless getter methods.
/// <summary>
/// Provides supported hash-output size metadata for <see cref="AsconHash"/>.
/// </summary>
public static class AsconHashSizeExtensions {
  extension(AsconHash) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Blake"/>.
/// </summary>
public static class BlakeHashSizeExtensions {
  extension(Blake) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224To512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Blake2s"/>.
/// </summary>
public static class Blake2sHashSizeExtensions {
  extension(Blake2s) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="CityHash"/>.
/// </summary>
public static class CityHashSizeExtensions {
  extension(CityHash) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits64;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Comb4PMd4Md5"/>.
/// </summary>
public static class Comb4PMd4Md5HashSizeExtensions {
  extension(Comb4PMd4Md5) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Comb4PSha1Ripemd160"/>.
/// </summary>
public static class Comb4PSha1Ripemd160HashSizeExtensions {
  extension(Comb4PSha1Ripemd160) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits320;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="CubeHash256"/>.
/// </summary>
public static class CubeHash256HashSizeExtensions {
  extension(CubeHash256) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="CubeHash512"/>.
/// </summary>
public static class CubeHash512HashSizeExtensions {
  extension(CubeHash512) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="DarkCryptMd6"/>.
/// </summary>
public static class DarkCryptMd6HashSizeExtensions {
  extension(DarkCryptMd6) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="DarkCryptSkein"/>.
/// </summary>
public static class DarkCryptSkeinHashSizeExtensions {
  extension(DarkCryptSkein) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Echo224"/>.
/// </summary>
public static class Echo224HashSizeExtensions {
  extension(Echo224) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Echo256"/>.
/// </summary>
public static class Echo256HashSizeExtensions {
  extension(Echo256) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Echo384"/>.
/// </summary>
public static class Echo384HashSizeExtensions {
  extension(Echo384) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits384;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Echo512"/>.
/// </summary>
public static class Echo512HashSizeExtensions {
  extension(Echo512) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Esch256"/>.
/// </summary>
public static class Esch256HashSizeExtensions {
  extension(Esch256) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Esch384"/>.
/// </summary>
public static class Esch384HashSizeExtensions {
  extension(Esch384) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits384;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="DarkCryptKeccak"/>.
/// </summary>
public static class DarkCryptKeccakHashSizeExtensions {
  extension(DarkCryptKeccak) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Gimli24Hash"/>.
/// </summary>
public static class Gimli24HashSizeExtensions {
  extension(Gimli24Hash) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="ChcHash"/>.
/// </summary>
public static class ChcHashSizeExtensions {
  extension(ChcHash) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Mdc2"/>.
/// </summary>
public static class Mdc2HashSizeExtensions {
  extension(Mdc2) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Fnv"/>.
/// </summary>
public static class FnvHashSizeExtensions {
  extension(Fnv) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits32And64;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="MurmurHash3"/>.
/// </summary>
public static class MurmurHash3HashSizeExtensions {
  extension(MurmurHash3) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits32And128;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="SipHash24"/>.
/// </summary>
public static class SipHash24HashSizeExtensions {
  extension(SipHash24) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits64;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Gost3411_94"/>.
/// </summary>
public static class Gost3411HashSizeExtensions {
  extension(Gost3411_94) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Groestl224"/>.
/// </summary>
public static class Groestl224HashSizeExtensions {
  extension(Groestl224) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Groestl256"/>.
/// </summary>
public static class Groestl256HashSizeExtensions {
  extension(Groestl256) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Groestl384"/>.
/// </summary>
public static class Groestl384HashSizeExtensions {
  extension(Groestl384) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits384;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Groestl512"/>.
/// </summary>
public static class Groestl512HashSizeExtensions {
  extension(Groestl512) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Haraka256"/>.
/// </summary>
public static class Haraka256HashSizeExtensions {
  extension(Haraka256) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Haraka512"/>.
/// </summary>
public static class Haraka512HashSizeExtensions {
  extension(Haraka512) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Haval"/>.
/// </summary>
public static class HavalHashSizeExtensions {
  extension(Haval) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128To256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="IsapHash"/>.
/// </summary>
public static class IsapHashSizeExtensions {
  extension(IsapHash) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Sha3"/>.
/// </summary>
public static class Sha3HashSizeExtensions {
  extension(Sha3) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224To512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Md2"/>.
/// </summary>
public static class Md2HashSizeExtensions {
  extension(Md2) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Md4"/>.
/// </summary>
public static class Md4HashSizeExtensions {
  extension(Md4) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Sm3"/>.
/// </summary>
public static class Sm3HashSizeExtensions {
  extension(Sm3) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Lsh224"/>.
/// </summary>
public static class Lsh224HashSizeExtensions {
  extension(Lsh224) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Lsh256"/>.
/// </summary>
public static class Lsh256HashSizeExtensions {
  extension(Lsh256) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Lsh384"/>.
/// </summary>
public static class Lsh384HashSizeExtensions {
  extension(Lsh384) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits384;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Lsh512"/>.
/// </summary>
public static class Lsh512HashSizeExtensions {
  extension(Lsh512) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Lsh512_256"/>.
/// </summary>
public static class Lsh512_256HashSizeExtensions {
  extension(Lsh512_256) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Luffa224"/>.
/// </summary>
public static class Luffa224HashSizeExtensions {
  extension(Luffa224) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Luffa256"/>.
/// </summary>
public static class Luffa256HashSizeExtensions {
  extension(Luffa256) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Luffa384"/>.
/// </summary>
public static class Luffa384HashSizeExtensions {
  extension(Luffa384) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits384;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Luffa512"/>.
/// </summary>
public static class Luffa512HashSizeExtensions {
  extension(Luffa512) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="PanamaLE"/>.
/// </summary>
public static class PanamaLEHashSizeExtensions {
  extension(PanamaLE) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="PanamaBE"/>.
/// </summary>
public static class PanamaBEHashSizeExtensions {
  extension(PanamaBE) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="PhotonBeetleHash"/>.
/// </summary>
public static class PhotonBeetleHashSizeExtensions {
  extension(PhotonBeetleHash) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="SparkleHash"/>.
/// </summary>
public static class SparkleHashSizeExtensions {
  extension(SparkleHash) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="XxHash"/>.
/// </summary>
public static class XxHashHashSizeExtensions {
  extension(XxHash) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits32And64;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Ripemd128"/>.
/// </summary>
public static class Ripemd128HashSizeExtensions {
  extension(Ripemd128) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Ripemd160"/>.
/// </summary>
public static class Ripemd160HashSizeExtensions {
  extension(Ripemd160) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits160;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Ripemd256"/>.
/// </summary>
public static class Ripemd256HashSizeExtensions {
  extension(Ripemd256) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Ripemd320"/>.
/// </summary>
public static class Ripemd320HashSizeExtensions {
  extension(Ripemd320) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits320;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Shabal192"/>.
/// </summary>
public static class Shabal192HashSizeExtensions {
  extension(Shabal192) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits192;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Shabal224"/>.
/// </summary>
public static class Shabal224HashSizeExtensions {
  extension(Shabal224) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits224;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Shabal256"/>.
/// </summary>
public static class Shabal256HashSizeExtensions {
  extension(Shabal256) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Shabal384"/>.
/// </summary>
public static class Shabal384HashSizeExtensions {
  extension(Shabal384) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits384;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Shabal512"/>.
/// </summary>
public static class Shabal512HashSizeExtensions {
  extension(Shabal512) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Skein512"/>.
/// </summary>
public static class Skein512HashSizeExtensions {
  extension(Skein512) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Streebog256"/>.
/// </summary>
public static class Streebog256HashSizeExtensions {
  extension(Streebog256) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Streebog512"/>.
/// </summary>
public static class Streebog512HashSizeExtensions {
  extension(Streebog512) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="SubterraneanHash"/>.
/// </summary>
public static class SubterraneanHashSizeExtensions {
  extension(SubterraneanHash) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Whirlpool"/>.
/// </summary>
public static class WhirlpoolHashSizeExtensions {
  extension(Whirlpool) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="XoodyakHash"/>.
/// </summary>
public static class XoodyakHashSizeExtensions {
  extension(XoodyakHash) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="XxHash3"/>.
/// </summary>
public static class XxHash3HashSizeExtensions {
  extension(XxHash3) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits64And128;
  }
}
