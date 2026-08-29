using System.Reflection;
using Compression.Core.Checksums;
using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class SupportedHashSizesContractTests {
  private static readonly HashSet<Type> NonFiniteOrNonHashTypes = [
    typeof(AsconXof),
    typeof(Blake2xs),
    typeof(Blake3),
    typeof(Blake3Enhanced),
    typeof(KangarooTwelve),
    typeof(Shake),
    typeof(CShake),
    typeof(TupleHash),
    typeof(ParallelHash),
    typeof(RadioGatun32),
    typeof(PanamaLEMac),
    typeof(PanamaBEMac)
  ];

  // Reflection discovers candidates independently. The accessor table intentionally references
  // Type.SupportedHashSizes at compile time, so a future hash omitted from the contract fails the
  // test and a fake table entry without the public metadata fails compilation.
  private static readonly IReadOnlyDictionary<Type, Func<IReadOnlyList<HashSizeRange>>> MetadataAccessors =
    new Dictionary<Type, Func<IReadOnlyList<HashSizeRange>>> {
      [typeof(AsconHash)] = static () => AsconHash.SupportedHashSizes,
      [typeof(Blake)] = static () => Blake.SupportedHashSizes,
      [typeof(Blake2s)] = static () => Blake2s.SupportedHashSizes,
      [typeof(CityHash)] = static () => CityHash.SupportedHashSizes,
      [typeof(Comb4PMd4Md5)] = static () => Comb4PMd4Md5.SupportedHashSizes,
      [typeof(Comb4PSha1Ripemd160)] = static () => Comb4PSha1Ripemd160.SupportedHashSizes,
      [typeof(CubeHash256)] = static () => CubeHash256.SupportedHashSizes,
      [typeof(CubeHash512)] = static () => CubeHash512.SupportedHashSizes,
      [typeof(DarkCryptMd6)] = static () => DarkCryptMd6.SupportedHashSizes,
      [typeof(DarkCryptSkein)] = static () => DarkCryptSkein.SupportedHashSizes,
      [typeof(DryGasconHash)] = static () => DryGasconHash.SupportedHashSizes,
      [typeof(Echo)] = static () => Echo.SupportedHashSizes,
      [typeof(Echo224)] = static () => Echo224.SupportedHashSizes,
      [typeof(Echo256)] = static () => Echo256.SupportedHashSizes,
      [typeof(Echo384)] = static () => Echo384.SupportedHashSizes,
      [typeof(Echo512)] = static () => Echo512.SupportedHashSizes,
      [typeof(Esch256)] = static () => Esch256.SupportedHashSizes,
      [typeof(Esch384)] = static () => Esch384.SupportedHashSizes,
      [typeof(DarkCryptKeccak)] = static () => DarkCryptKeccak.SupportedHashSizes,
      [typeof(Gimli24Hash)] = static () => Gimli24Hash.SupportedHashSizes,
      [typeof(ChcHash)] = static () => ChcHash.SupportedHashSizes,
      [typeof(Mdc2)] = static () => Mdc2.SupportedHashSizes,
      [typeof(Fnv)] = static () => Fnv.SupportedHashSizes,
      [typeof(MurmurHash3)] = static () => MurmurHash3.SupportedHashSizes,
      [typeof(SipHash24)] = static () => SipHash24.SupportedHashSizes,
      [typeof(Fugue)] = static () => Fugue.SupportedHashSizes,
      [typeof(Gost3411_94)] = static () => Gost3411_94.SupportedHashSizes,
      [typeof(Groestl)] = static () => Groestl.SupportedHashSizes,
      [typeof(Groestl224)] = static () => Groestl224.SupportedHashSizes,
      [typeof(Groestl256)] = static () => Groestl256.SupportedHashSizes,
      [typeof(Groestl384)] = static () => Groestl384.SupportedHashSizes,
      [typeof(Groestl512)] = static () => Groestl512.SupportedHashSizes,
      [typeof(Hamsi)] = static () => Hamsi.SupportedHashSizes,
      [typeof(HamsiFamily)] = static () => HamsiFamily.SupportedHashSizes,
      [typeof(Haraka256)] = static () => Haraka256.SupportedHashSizes,
      [typeof(Haraka512)] = static () => Haraka512.SupportedHashSizes,
      [typeof(Haval)] = static () => Haval.SupportedHashSizes,
      [typeof(HighwayHash)] = static () => HighwayHash.SupportedHashSizes,
      [typeof(IsapHash)] = static () => IsapHash.SupportedHashSizes,
      [typeof(Jh)] = static () => Jh.SupportedHashSizes,
      [typeof(Sha3)] = static () => Sha3.SupportedHashSizes,
      [typeof(KnotHash)] = static () => KnotHash.SupportedHashSizes,
      [typeof(Keccak)] = static () => Keccak.SupportedHashSizes,
      [typeof(Kupyna)] = static () => Kupyna.SupportedHashSizes,
      [typeof(Md2)] = static () => Md2.SupportedHashSizes,
      [typeof(Md4)] = static () => Md4.SupportedHashSizes,
      [typeof(Sm3)] = static () => Sm3.SupportedHashSizes,
      [typeof(Lsh224)] = static () => Lsh224.SupportedHashSizes,
      [typeof(Lsh256)] = static () => Lsh256.SupportedHashSizes,
      [typeof(Lsh384)] = static () => Lsh384.SupportedHashSizes,
      [typeof(Lsh512)] = static () => Lsh512.SupportedHashSizes,
      [typeof(Lsh512_256)] = static () => Lsh512_256.SupportedHashSizes,
      [typeof(Lsh256Family)] = static () => Lsh256Family.SupportedHashSizes,
      [typeof(Lsh512Family)] = static () => Lsh512Family.SupportedHashSizes,
      [typeof(Luffa)] = static () => Luffa.SupportedHashSizes,
      [typeof(Luffa224)] = static () => Luffa224.SupportedHashSizes,
      [typeof(Luffa256)] = static () => Luffa256.SupportedHashSizes,
      [typeof(Luffa384)] = static () => Luffa384.SupportedHashSizes,
      [typeof(Luffa512)] = static () => Luffa512.SupportedHashSizes,
      [typeof(PanamaLE)] = static () => PanamaLE.SupportedHashSizes,
      [typeof(PanamaBE)] = static () => PanamaBE.SupportedHashSizes,
      [typeof(PhotonBeetleHash)] = static () => PhotonBeetleHash.SupportedHashSizes,
      [typeof(SparkleHash)] = static () => SparkleHash.SupportedHashSizes,
      [typeof(XxHash)] = static () => XxHash.SupportedHashSizes,
      [typeof(Ripemd)] = static () => Ripemd.SupportedHashSizes,
      [typeof(Ripemd128)] = static () => Ripemd128.SupportedHashSizes,
      [typeof(Ripemd160)] = static () => Ripemd160.SupportedHashSizes,
      [typeof(Ripemd256)] = static () => Ripemd256.SupportedHashSizes,
      [typeof(Ripemd320)] = static () => Ripemd320.SupportedHashSizes,
      [typeof(Sha512Family)] = static () => Sha512Family.SupportedHashSizes,
      [typeof(Shabal192)] = static () => Shabal192.SupportedHashSizes,
      [typeof(Shabal224)] = static () => Shabal224.SupportedHashSizes,
      [typeof(Shabal256)] = static () => Shabal256.SupportedHashSizes,
      [typeof(Shabal384)] = static () => Shabal384.SupportedHashSizes,
      [typeof(Shabal512)] = static () => Shabal512.SupportedHashSizes,
      [typeof(Skein512)] = static () => Skein512.SupportedHashSizes,
      [typeof(SkinnyHash)] = static () => SkinnyHash.SupportedHashSizes,
      [typeof(Streebog)] = static () => Streebog.SupportedHashSizes,
      [typeof(Streebog256)] = static () => Streebog256.SupportedHashSizes,
      [typeof(Streebog512)] = static () => Streebog512.SupportedHashSizes,
      [typeof(SubterraneanHash)] = static () => SubterraneanHash.SupportedHashSizes,
      [typeof(Tiger)] = static () => Tiger.SupportedHashSizes,
      [typeof(Whirlpool)] = static () => Whirlpool.SupportedHashSizes,
      [typeof(XoodyakHash)] = static () => XoodyakHash.SupportedHashSizes,
      [typeof(XxHash3)] = static () => XxHash3.SupportedHashSizes,
      [typeof(Blake2b)] = static () => Blake2b.SupportedHashSizes,
      [typeof(Md5)] = static () => Md5.SupportedHashSizes,
      [typeof(Sha1)] = static () => Sha1.SupportedHashSizes,
      [typeof(Sha256)] = static () => Sha256.SupportedHashSizes,
      [typeof(XxHash32)] = static () => XxHash32.SupportedHashSizes,
      [typeof(XxHash64)] = static () => XxHash64.SupportedHashSizes
    };

  [Test]
  public void EveryFixedOrFiniteMultiOutputHashExposesSupportedHashSizes() {
    var candidates = typeof(HashSizeRange).Assembly
      .GetExportedTypes()
      .Where(IsHashApiType)
      .OrderBy(static type => type.FullName, StringComparer.Ordinal)
      .ToArray();

    var missing = candidates
      .Where(type => !MetadataAccessors.ContainsKey(type))
      .Select(static type => type.FullName)
      .ToArray();

    Assert.That(missing, Is.Empty,
      "Every public fixed-size or finite multi-output hash must expose SupportedHashSizes. Missing: " + string.Join(", ", missing));

    Assert.Multiple(() => {
      foreach (var type in candidates) {
        var ranges = MetadataAccessors[type]();
        Assert.That(ranges, Is.Not.Null.And.Not.Empty, type.FullName);
        Assert.That(ranges.SelectMany(static range => range).Distinct().All(static bits => bits > 0 && bits % 8 == 0),
          Is.True, $"{type.FullName} advertises an invalid hash size");
      }
    });
  }

  private static bool IsHashApiType(Type type) {
    if (NonFiniteOrNonHashTypes.Contains(type))
      return false;
    if (type.Namespace is not ("Hawkynt.Algorithms.Hashing" or "Compression.Core.Checksums"))
      return false;
    return type.GetMethods(BindingFlags.Public | BindingFlags.Static)
      .Any(static method => method.Name.StartsWith("Compute", StringComparison.Ordinal));
  }
}
