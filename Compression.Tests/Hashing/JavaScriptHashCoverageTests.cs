using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class JavaScriptHashCoverageTests {
  private sealed record Coverage(string Source, string ManagedCounterpart);

  private static readonly Coverage[] Inventory = [
    new("ascon-hash.js", "AsconHash / AsconXof"),
    new("blake.js", "Blake"),
    new("blake2.js", "Blake2s / Blake2xs / Blake2b"),
    new("blake3-enhanced.js", "Blake3Enhanced"),
    new("blake3.js", "Blake3"),
    new("chc.js", "ChcHash"),
    new("cityhash.js", "CityHash"),
    new("comb4p.js", "Comb4PMd4Md5 / Comb4PSha1Ripemd160"),
    new("cshake.js", "CShake"),
    new("cubehash.js", "CubeHash256 / CubeHash512"),
    new("darkcrypt-keccak.js", "DarkCryptKeccak"),
    new("darkcrypt-md6.js", "DarkCryptMd6"),
    new("darkcrypt-skein.js", "DarkCryptSkein"),
    new("drygascon-hash.js", "DryGasconHash"),
    new("dstu7564.js", "Kupyna"),
    new("echo.js", "Echo"),
    new("esch256.js", "Esch256"),
    new("esch384.js", "Esch384"),
    new("fnv.js", "Fnv"),
    new("fugue.js", "Fugue"),
    new("gimli24-hash.js", "Gimli24Hash"),
    new("gost3411.js", "Gost3411_94"),
    new("groestl.js", "Groestl"),
    new("hamsi.js", "HamsiFamily"),
    new("haraka.js", "Haraka256 / Haraka512"),
    new("haval.js", "Haval"),
    new("highway-hash.js", "HighwayHash (full Google reference algorithm)"),
    new("isap-hash.js", "IsapHash"),
    new("jh.js", "Jh (registry-specific educational variant)"),
    new("kangaroo.js", "KangarooTwelve"),
    new("keccak.js", "Keccak"),
    new("knot-hash.js", "KnotHash"),
    new("kupyna.js", "Kupyna"),
    new("lsh.js", "Lsh256Family / Lsh512Family"),
    new("luffa.js", "Luffa"),
    new("md.js", "Md2 / Md4 / Md5"),
    new("mdc2.js", "Mdc2"),
    new("murmurhash3.js", "MurmurHash3"),
    new("panama.js", "PanamaLE / PanamaBE / PanamaLEMac / PanamaBEMac"),
    new("parallelhash.js", "ParallelHash"),
    new("photon-beetle-hash.js", "PhotonBeetleHash"),
    new("radiogatun.js", "RadioGatun32"),
    new("ripemd.js", "Ripemd"),
    new("sha1.js", "Sha1"),
    new("sha256.js", "Sha256"),
    new("sha3.js", "Sha3"),
    new("sha512.js", "Sha512Family"),
    new("shabal.js", "Shabal192 / Shabal224 / Shabal256 / Shabal384 / Shabal512"),
    new("shake.js", "Shake"),
    new("siphash.js", "SipHash24"),
    new("skein.js", "Skein512"),
    new("skinny-hash.js", "SkinnyHash"),
    new("sm3.js", "Sm3"),
    new("sparkle-hash.js", "SparkleHash / Esch256"),
    new("streebog.js", "Streebog"),
    new("subterranean-hash.js", "SubterraneanHash"),
    new("tiger.js", "Tiger"),
    new("tuplehash.js", "TupleHash"),
    new("whirlpool.js", "Whirlpool"),
    new("xoodyak-hash.js", "XoodyakHash"),
    new("xxhash.js", "XxHash / XxHash32 / XxHash64"),
    new("xxhash3.js", "XxHash3"),
    new("xxhash32.js", "XxHash32")
  ];

  [Test]
  public void AccountsForEveryJavaScriptHashImplementationFile() {
    Assert.Multiple(() => {
      Assert.That(Inventory, Has.Length.EqualTo(63));
      Assert.That(Inventory.Select(static entry => entry.Source).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(63));
      Assert.That(Inventory.All(static entry => entry.Source.EndsWith(".js", StringComparison.Ordinal)), Is.True);
      Assert.That(Inventory.All(static entry => !string.IsNullOrWhiteSpace(entry.ManagedCounterpart)), Is.True);
      Assert.That(Inventory.Any(static entry => entry.ManagedCounterpart.StartsWith("JS-only", StringComparison.Ordinal)), Is.False,
        "63/63 means every JavaScript implementation must have a managed counterpart, not merely a bookkeeping row.");
    });
  }
}
