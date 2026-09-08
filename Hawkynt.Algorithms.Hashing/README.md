# Hawkynt.Algorithms.Hashing

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.Algorithms.Hashing.svg)](https://www.nuget.org/packages/Hawkynt.Algorithms.Hashing/)
[![NuGet downloads](https://img.shields.io/nuget/dt/Hawkynt.Algorithms.Hashing.svg)](https://www.nuget.org/packages/Hawkynt.Algorithms.Hashing/)
[![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE)
[![CI](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml)
![Target](https://img.shields.io/badge/target-net10.0-blue)

> Pure-managed cryptographic and non-cryptographic hash functions for .NET, including the managed C# counterparts of `Cipher/algorithms/hash` from `Hawkynt/Hawkynt.github.io`.

## Hash or checksum?

A **hash function** deterministically maps arbitrary input to a fixed-size digest or, for XOF constructions, an arbitrary-length output. A good non-cryptographic hash is designed for speed and statistical distribution; examples are xxHash, FNV, CityHash, and MurmurHash. A **cryptographic hash** adds security requirements: finding preimages, second preimages, or collisions should be computationally infeasible at its intended security level.

A **checksum** is primarily an error-detection code. It is usually smaller and cheaper, and its job is to notice accidental corruption, transmission errors, or mistyped identifiers rather than resist an adversary who deliberately chooses colliding input. CRC, Adler, Fletcher, Internet checksums, Luhn, Verhoeff, and Damm therefore live in [`Hawkynt.Algorithms.Checksums`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.Algorithms.Checksums/README.md).

The distinction follows the algorithm's design, not the caller's use of it. SHA-256 used to verify a download is still a cryptographic hash; CRC-32 stored beside a file is still a checksum even when a tool labels the field “hash”. A checksum can be excellent engineering for accidental-error detection and still be completely inappropriate for hostile-input authentication.

## 📦 Installation

```bash
dotnet add package Hawkynt.Algorithms.Hashing
```

## ✨ Features

- One-shot and incremental APIs where the underlying construction naturally supports streaming.
- Cryptographic families including MD, SHA-1/SHA-2/SHA-3, Keccak-derived XOFs, BLAKE, RIPEMD, SM3, and legacy/interoperability hashes.
- Fast non-cryptographic hashing including xxHash, MurmurHash, FNV, SipHash, HighwayHash, and other source-registry families.
- Fixed and finite multi-output hash APIs expose valid digest sizes as enumerable `HashSizeRange` records through `SupportedHashSizes`; XOF constructions are excluded because their output length is intentionally arbitrary.
- Source-specific variants are preserved as distinct algorithms when their parameters or output differ from the published standard; they are not silently substituted with a similarly named digest.
- All 63 JavaScript hash implementation files in the source registry have managed counterparts; no JavaScript runtime is required by the package.

## 🚀 Quick start

```csharp
using Compression.Core.Checksums;
using Hawkynt.Algorithms.Hashing;

ReadOnlySpan<byte> data = "CompressionWorkbench"u8;

byte[] sha256 = Sha256.Compute(data);
byte[] sha512 = Sha512Family.Compute(data, 512);
byte[] sha3 = Sha3.Compute256(data);
byte[] kupyna384 = Kupyna.Compute(data, 384);
byte[] fugue512 = Fugue.Compute(data, 512);
byte[] hamsi224 = HamsiFamily.Compute(data, 224);
byte[] tiger192 = Tiger.Compute(data, 192);
byte[] knot256 = KnotHash.Compute(data, KnotHashVariant.KnotHash256_384);
byte[] dryGascon512 = DryGasconHash.Compute(data, 512);
byte[] skinny = SkinnyHash.Compute(data, SkinnyHashVariant.Tk3);
uint murmur = MurmurHash3.Compute32(data);
ulong fnv = Fnv.Compute1A_64(data);

foreach (int bits in Fugue.SupportedHashSizes.EnumerateSizes())
  Console.WriteLine($"Fugue-{bits}");
```

The historical `Compression.Core.Checksums` namespace is retained for hash types that already shipped there, so existing callers do not need a namespace migration merely because the implementation moved to its own assembly.

## 🧩 JavaScript source conversion

The source-of-truth inventory is the 63 `.js` implementation files in `Hawkynt/Hawkynt.github.io/Cipher/algorithms/hash`. Conversion is tracked file-for-file rather than by a vague algorithm count because one source file may register several variants and two similarly named variants may be intentionally incompatible.

**63/63 source implementation files now have managed counterparts.** The same 63-entry inventory is asserted by `JavaScriptHashCoverageTests`; the test also rejects any future `JS-only` bookkeeping row, so the number cannot stay green by merely documenting a missing port.

| JavaScript source | Managed counterpart / disposition |
| --- | --- |
| `ascon-hash.js` | `AsconHash` / `AsconXof` |
| `blake.js` | `Blake` |
| `blake2.js` | `Blake2s` / `Blake2xs` / `Blake2b` |
| `blake3-enhanced.js` | `Blake3Enhanced` |
| `blake3.js` | `Blake3` |
| `chc.js` | `ChcHash` |
| `cityhash.js` | `CityHash` |
| `comb4p.js` | `Comb4PMd4Md5` / `Comb4PSha1Ripemd160` |
| `cshake.js` | `CShake` |
| `cubehash.js` | `CubeHash256` / `CubeHash512` |
| `darkcrypt-keccak.js` | `DarkCryptKeccak` |
| `darkcrypt-md6.js` | `DarkCryptMd6` |
| `darkcrypt-skein.js` | `DarkCryptSkein` |
| `drygascon-hash.js` | `DryGasconHash` |
| `dstu7564.js` | `Kupyna` |
| `echo.js` | `Echo` |
| `esch256.js` | `Esch256` |
| `esch384.js` | `Esch384` |
| `fnv.js` | `Fnv` |
| `fugue.js` | `Fugue` |
| `gimli24-hash.js` | `Gimli24Hash` |
| `gost3411.js` | `Gost3411_94` |
| `groestl.js` | `Groestl` |
| `hamsi.js` | `HamsiFamily` |
| `haraka.js` | `Haraka256` / `Haraka512` |
| `haval.js` | `Haval` |
| `highway-hash.js` | `HighwayHash` — full Google reference algorithm rather than the registry file's malformed educational vectors |
| `isap-hash.js` | `IsapHash` |
| `jh.js` | `Jh` — registry-specific educational JH variant, named as such rather than substituted with standard JH |
| `kangaroo.js` | `KangarooTwelve` |
| `keccak.js` | `Keccak` |
| `knot-hash.js` | `KnotHash` with `KnotHashVariant` |
| `kupyna.js` | `Kupyna` |
| `lsh.js` | `Lsh256Family` / `Lsh512Family` |
| `luffa.js` | `Luffa` |
| `md.js` | `Md2` / `Md4` / `Md5` |
| `mdc2.js` | `Mdc2` |
| `murmurhash3.js` | `MurmurHash3` |
| `panama.js` | `PanamaLE` / `PanamaBE` / MAC variants |
| `parallelhash.js` | `ParallelHash` |
| `photon-beetle-hash.js` | `PhotonBeetleHash` |
| `radiogatun.js` | `RadioGatun32` |
| `ripemd.js` | `Ripemd` |
| `sha1.js` | `Sha1` |
| `sha256.js` | `Sha256` |
| `sha3.js` | `Sha3` |
| `sha512.js` | `Sha512Family` |
| `shabal.js` | `Shabal192` / `Shabal224` / `Shabal256` / `Shabal384` / `Shabal512` |
| `shake.js` | `Shake` |
| `siphash.js` | `SipHash24` |
| `skein.js` | `Skein512` |
| `skinny-hash.js` | `SkinnyHash` with `SkinnyHashVariant` |
| `sm3.js` | `Sm3` |
| `sparkle-hash.js` | `SparkleHash` / `Esch256` |
| `streebog.js` | `Streebog` |
| `subterranean-hash.js` | `SubterraneanHash` |
| `tiger.js` | `Tiger` |
| `tuplehash.js` | `TupleHash` |
| `whirlpool.js` | `Whirlpool` |
| `xoodyak-hash.js` | `XoodyakHash` |
| `xxhash.js` | `XxHash` / `XxHash32` / `XxHash64` |
| `xxhash3.js` | `XxHash3` |
| `xxhash32.js` | `XxHash32` |

Standard algorithms may share a parameterized managed implementation. Source-specific DarkCrypt/lightweight/educational variants receive dedicated managed implementations when their bytes intentionally differ from the standard construction. Where a registry implementation is demonstrably malformed but represents a real published algorithm, such as its HighwayHash placeholder, the managed package implements and tests the published reference algorithm instead. JavaScript wrappers are not used as an implementation shortcut.

## 📚 API reference

<!-- API:BEGIN generated by Hawkynt/RepositoryTemplate/package-readme — edit the XML docs in source, not here -->

Every public and protected member of all 180 types, generated from the built assembly and its XML documentation, is in [REFERENCE.md](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.Algorithms.Hashing/REFERENCE.md).

<!-- API:END -->

## 🏗 Architecture

`Hawkynt.Compression.Core` references this project normally, so Core consumers receive the hashing package transitively while callers that only require hashing can reference it directly.

For a standardized family with several digest sizes, the preferred API is a single `Compute(ReadOnlySpan<byte>, int hashSizeBits)` method plus an enumerable `IReadOnlyList<HashSizeRange> SupportedHashSizes`. A range describes valid **digest/output sizes**, not the algorithm's compression-block width. Discontiguous families expose several ranges; for example Hamsi exposes `224..256 step 32` and `384..512 step 128`. When a specification genuinely changes state width or round schedule at a boundary, the family dispatcher selects that internal core without duplicating an implementation for every individual output size.

Kupyna therefore shares one P/Q implementation across 256/384/512, Fugue shares one state machine across 224/256/384/512, and Tiger exposes the exact singleton range 192. KNOT is exposed as one variant-aware family because two standardized KNOT parameter sets both produce 256-bit digests and therefore cannot be selected by digest size alone. SKINNY-HASH similarly uses a variant-aware family because tk2 and tk3 both produce 256-bit output while differing in tweakey state and absorption rate. Compatibility wrappers may remain for other existing callers, but they delegate to shared family/core logic rather than becoming independent algorithm implementations.

Hash functions and checksums are separate packages intentionally. This prevents a convenience namespace from turning two materially different algorithm classes into one conceptual junk drawer.

## 🔌 Dependencies

| Dependency | Packaging behaviour |
| --- | --- |
| .NET | Targets `net10.0`, `net9.0`, and `net8.0` |
| Native hashing libraries | None |
| JavaScript runtime | None |

## ⚠️ Limitations

- Legacy hashes such as MD2/MD4/MD5/SHA-1 and historical competition candidates are provided for interoperability, research, and format compatibility; presence in this package is not a recommendation for new security designs.
- The `Jh` type intentionally preserves the educational source-registry construction and is not advertised as the standardized JH SHA-3 finalist.
- Non-cryptographic hashes such as xxHash, FNV, CityHash, and MurmurHash must not be used as authentication primitives.
- A bare cryptographic hash does not authenticate data against an active attacker when the expected digest can also be replaced; use a MAC or digital signature for that threat model.
- Source-specific variants are named explicitly because substituting the closest standard algorithm would produce the wrong bytes while looking deceptively plausible.

## 📖 References

The original JavaScript implementations carry provenance in comments, `documentation` / `references` metadata, and test-case `uri` fields. Those sources were mined first so the table can point at the exact reference implementation or KAT that informed a source conversion where one is recorded; additional standards and independent implementations fill gaps. Linked code is a compatibility/reference oracle, not a package dependency, and no third-party implementation code is incorporated merely by linking it. For source-specific variants, the registry file remains the byte-level definition when no independent implementation is known to match it exactly.

| Algorithm | Specifications / documentation | Reference code / third-party oracles |
| --- | --- | --- |
| Ascon-Hash / Ascon-XOF | [Registry source](https://github.com/Hawkynt/Hawkynt.github.io/blob/main/Cipher/algorithms/hash/ascon-hash.js), [Ascon v1.2 reference branch](https://github.com/ascon/ascon-c/tree/v1.2); [NIST SP 800-232](https://csrc.nist.gov/pubs/sp/800/232/final) is the later standardized variant and changes endianness/IVs | Source metadata names the [official Ascon C](https://github.com/ascon/ascon-c) and [Python](https://github.com/ascon/ascon-python) implementations; use the v1.2 branch for byte-compatible legacy Ascon-Hash/Ascon-XOF vectors |
| BLAKE | [BLAKE paper](https://www.aumasson.jp/blake/blake.pdf), [NIST SHA-3 project](https://csrc.nist.gov/projects/hash-functions/sha-3-project) | [Designers' reference C](https://github.com/veorq/BLAKE), [noble-hashes BLAKE tests](https://github.com/paulmillr/noble-hashes/blob/main/test/blake.test.ts) used by the source registry, [sphlib](https://github.com/pornin/sphlib) |
| BLAKE2b / BLAKE2s | [RFC 7693](https://www.rfc-editor.org/rfc/rfc7693.html), [BLAKE2 specification](https://www.blake2.net/blake2.pdf) | [Official BLAKE2 implementation](https://github.com/BLAKE2/BLAKE2), [official test vectors](https://github.com/BLAKE2/BLAKE2/tree/master/testvectors), [libsodium](https://github.com/jedisct1/libsodium) |
| BLAKE2X | [BLAKE2X specification](https://www.blake2.net/blake2x.pdf) | Source metadata points directly at Bouncy Castle's [`Blake2xsDigest`](https://github.com/bcgit/bc-java/blob/main/core/src/main/java/org/bouncycastle/crypto/digests/Blake2xsDigest.java) plus the [BLAKE2 official vectors](https://github.com/BLAKE2/BLAKE2/tree/master/testvectors) |
| BLAKE3 | [BLAKE3 specification](https://github.com/BLAKE3-team/BLAKE3-specs/blob/master/blake3.pdf), [official site](https://blake3.io/) | [Official BLAKE3 implementation](https://github.com/BLAKE3-team/BLAKE3) |
| BLAKE3 Enhanced | [Registry source](https://github.com/Hawkynt/Hawkynt.github.io/blob/main/Cipher/algorithms/hash/blake3-enhanced.js); registry-specific educational extension, not standard BLAKE3 | [BLAKE3 specification](https://github.com/BLAKE3-team/BLAKE3-specs), [official BLAKE3](https://github.com/BLAKE3-team/BLAKE3) for the underlying primitive only |
| CHC | [Registry source](https://github.com/Hawkynt/Hawkynt.github.io/blob/main/Cipher/algorithms/hash/chc.js), [Handbook of Applied Cryptography, Chapter 9](https://cacr.uwaterloo.ca/hac/about/chap9.pdf) for Matyas-Meyer-Oseas | Registry source is the exact construction/parameter oracle |
| CityHash | [Google CityHash documentation](https://github.com/google/cityhash) | [Google reference implementation](https://github.com/google/cityhash), [SMHasher](https://github.com/aappleby/smhasher) |
| COMB4P MD4+MD5 / SHA-1+RIPEMD-160 | [Registry source](https://github.com/Hawkynt/Hawkynt.github.io/blob/main/Cipher/algorithms/hash/comb4p.js), [RFC 1320](https://www.rfc-editor.org/rfc/rfc1320.html), [RFC 1321](https://www.rfc-editor.org/rfc/rfc1321.html), [FIPS 180-4](https://csrc.nist.gov/pubs/fips/180-4/upd1/final), [RIPEMD-160](https://homes.esat.kuleuven.be/~bosselae/ripemd160.html) | [sphlib](https://github.com/pornin/sphlib) for the component digests; the registry source defines the combiner |
| cSHAKE / TupleHash / ParallelHash | [NIST SP 800-185](https://csrc.nist.gov/pubs/sp/800/185/final) | [XKCP](https://github.com/XKCP/XKCP), [Bouncy Castle](https://github.com/bcgit/bc-csharp) |
| CubeHash | [NIST SHA-3 project](https://csrc.nist.gov/projects/hash-functions/sha-3-project) | [sphlib](https://github.com/pornin/sphlib) |
| DarkCrypt Keccak | [Registry source](https://github.com/Hawkynt/Hawkynt.github.io/blob/main/Cipher/algorithms/hash/darkcrypt-keccak.js); plugin-specific Keccak construction | [DarkCrypt Total Commander plugin](https://totalcmd.net/plugring/darkcrypttc.html) is the behavioral oracle; [XKCP](https://github.com/XKCP/XKCP) is useful only for the underlying Keccak permutation |
| DarkCrypt MD6 | [Registry source](https://github.com/Hawkynt/Hawkynt.github.io/blob/main/Cipher/algorithms/hash/darkcrypt-md6.js), [MIT MD6 site](https://groups.csail.mit.edu/cis/md6/), [MD6 report](https://groups.csail.mit.edu/cis/md6/docs/md6_report.pdf) | The source records the exact DarkCrypt parameters (`d=512`, `r=168`, `L=64`, no key) and deliberately reproduces the pre-15-Apr-2009 `md6_final()` output-order bug; [DarkCrypt](https://totalcmd.net/plugring/darkcrypttc.html) is therefore the matching oracle rather than post-fix MD6 implementations |
| DarkCrypt Skein | [Registry source](https://github.com/Hawkynt/Hawkynt.github.io/blob/main/Cipher/algorithms/hash/darkcrypt-skein.js); plugin-specific Skein-512-512 variant | [DarkCrypt](https://totalcmd.net/plugring/darkcrypttc.html) for plugin behavior; [Skein documentation](https://www.schneier.com/academic/skein/) and [sphlib](https://github.com/pornin/sphlib) for the standard primitive |
| DryGASCON | [NIST LWC round 2](https://csrc.nist.gov/projects/lightweight-cryptography/round-2-candidates), [DryGASCON project](https://github.com/sebastien-riou/DryGASCON) | Source metadata names the [official DryGASCON reference implementation](https://github.com/sebastien-riou/DryGASCON) and its official hash vectors |
| ECHO | [NIST SHA-3 project](https://csrc.nist.gov/projects/hash-functions/sha-3-project) | [sphlib](https://github.com/pornin/sphlib) |
| ESCH / SPARKLE | [NIST LWC round 2](https://csrc.nist.gov/projects/lightweight-cryptography/round-2-candidates), [SPARKLE project](https://sparkle-lwc.github.io/) | [Designers' SPARKLE/ESCH code](https://github.com/cryptolu/sparkle) |
| FNV-1 / FNV-1a | [RFC 9923](https://www.rfc-editor.org/rfc/rfc9923.html), [original FNV site/specification](http://www.isthe.com/chongo/tech/comp/fnv/) named by the source | [SMHasher](https://github.com/aappleby/smhasher) |
| Fugue | [NIST SHA-3 project](https://csrc.nist.gov/projects/hash-functions/sha-3-project) | [sphlib](https://github.com/pornin/sphlib) |
| Gimli-24 Hash | [Gimli specification/reference C](https://gimli.cr.yp.to/spec.html), [NIST LWC round 2](https://csrc.nist.gov/projects/lightweight-cryptography/round-2-candidates) | [Gimli reference software](https://gimli.cr.yp.to/spec.html) |
| GOST R 34.11-94 | [RFC 5831](https://www.rfc-editor.org/rfc/rfc5831.html) | [Bouncy Castle](https://github.com/bcgit/bc-csharp); the managed tests also use Bouncy Castle vectors |
| Grøstl | [NIST SHA-3 finalist report](https://csrc.nist.gov/pubs/ir/7896/final) | [sphlib](https://github.com/pornin/sphlib) |
| Hamsi | [NIST SHA-3 project](https://csrc.nist.gov/projects/hash-functions/sha-3-project) | [sphlib](https://github.com/pornin/sphlib) |
| Haraka | [Haraka paper/reference repository](https://github.com/kste/haraka) | [Haraka reference implementation](https://github.com/kste/haraka), [Bouncy Castle](https://github.com/bcgit/bc-csharp) |
| HAVAL | [HAVAL paper](https://doi.org/10.1007/3-540-57220-1_54) | [sphlib](https://github.com/pornin/sphlib), [independent BSD implementation](https://github.com/mikedld/haval) |
| HighwayHash | [HighwayHash paper](https://arxiv.org/abs/1612.06257), [registry source](https://github.com/Hawkynt/Hawkynt.github.io/blob/main/Cipher/algorithms/hash/highway-hash.js) | The managed implementation intentionally follows [Google HighwayHash](https://github.com/google/highwayhash), not the registry file's malformed educational vectors |
| ISAP-Hash | [Registry source](https://github.com/Hawkynt/Hawkynt.github.io/blob/main/Cipher/algorithms/hash/isap-hash.js), [ISAP specification/site](https://isap.isec.tugraz.at/), [ISAP v2.0 paper](https://tosc.iacr.org/index.php/ToSC/article/view/8625) | The source explicitly follows Bouncy Castle [`ISAPDigest`](https://github.com/bcgit/bc-java/blob/main/core/src/main/java/org/bouncycastle/crypto/digests/ISAPDigest.java), uses the official [ISAP code package](https://github.com/isap-lwc/isap-code-package) and NIST LWC hash KATs; its hash path is the same Ascon-Hash construction, so the managed `IsapHash` delegation is intentional |
| JH | [Registry source](https://github.com/Hawkynt/Hawkynt.github.io/blob/main/Cipher/algorithms/hash/jh.js) for the package's intentionally non-standard educational variant, [JH round-3 paper](https://www3.ntu.edu.sg/home/wuhj/research/jh/jh_round3.pdf), [JH homepage](https://www3.ntu.edu.sg/home/wuhj/research/jh/index.html) | [NIST SHA-3 project](https://csrc.nist.gov/projects/hash-functions/sha-3-project) and [sphlib standard JH](https://github.com/pornin/sphlib) are comparison references only; the registry source is the exact oracle for this managed variant |
| KangarooTwelve | [KangarooTwelve specification](https://keccak.team/kangarootwelve.html), [RFC 9861](https://www.rfc-editor.org/rfc/rfc9861.html) | [XKCP](https://github.com/XKCP/XKCP) |
| Keccak | [Keccak team specification](https://keccak.team/keccak.html) | Source metadata points to [Crypto++ `keccak.cpp`](https://github.com/weidai11/cryptopp/blob/master/keccak.cpp), [Crypto++ Keccak vectors](https://github.com/weidai11/cryptopp/blob/master/TestVectors/keccak.txt), and the historical [Keccak KAT archive](http://keccak.noekeon.org/KeccakKAT-3.zip); [XKCP](https://github.com/XKCP/XKCP) is the designers' implementation |
| SHA-3 / SHAKE | [FIPS 202](https://csrc.nist.gov/pubs/fips/202/final) | Source metadata/test URIs point to [Crypto++ `sha3.cpp`](https://github.com/weidai11/cryptopp/blob/master/sha3.cpp), [SHA-3 FIPS 202 vectors](https://github.com/weidai11/cryptopp/blob/master/TestVectors/sha3_224_fips_202.txt), [SHAKE vectors](https://github.com/weidai11/cryptopp/blob/master/TestVectors/shake.txt), and the [NIST CAVP](https://csrc.nist.gov/Projects/Cryptographic-Algorithm-Validation-Program/Secure-Hashing); [XKCP](https://github.com/XKCP/XKCP) is an additional oracle |
| KNOT-Hash | [NIST LWC round 2](https://csrc.nist.gov/projects/lightweight-cryptography/round-2-candidates) | NIST submission package on the round-2 page includes reference code and KATs |
| Kupyna / DSTU 7564 | [DSTU 7564 English translation](https://eprint.iacr.org/2015/885) | [Bouncy Castle](https://github.com/bcgit/bc-csharp); managed known-answer tests include Bouncy Castle vectors |
| LSH | [KISA LSH / KS X 3262 documentation](https://seed.kisa.or.kr/kisa/kcmvp/EgovVerification.do) | Source tests use Crypto++ [`lsh256.txt`](https://github.com/weidai11/cryptopp/blob/master/TestVectors/lsh256.txt) and [`lsh512.txt`](https://github.com/weidai11/cryptopp/blob/master/TestVectors/lsh512.txt); KISA/NSR publishes reference material and vectors |
| Luffa | [NIST SHA-3 project](https://csrc.nist.gov/projects/hash-functions/sha-3-project) | [sphlib](https://github.com/pornin/sphlib) |
| MD2 / MD4 / MD5 | [RFC 1319](https://www.rfc-editor.org/rfc/rfc1319.html), [RFC 1320](https://www.rfc-editor.org/rfc/rfc1320.html), [RFC 1321](https://www.rfc-editor.org/rfc/rfc1321.html) | RFC appendices include reference C and test vectors; [Bouncy Castle](https://github.com/bcgit/bc-csharp) |
| MDC-2 | Source metadata names [ISO/IEC 10118-2:2010](https://www.iso.org/standard/44737.html), [OpenSSL MDC2 documentation](https://docs.openssl.org/1.0.2/man3/mdc2/), and [MDC-2 research](https://link.springer.com/chapter/10.1007/3-540-39118-5_24) | [OpenSSL 1.0.2](https://github.com/openssl/openssl/tree/OpenSSL_1_0_2-stable) is the concrete implementation oracle |
| MurmurHash3 | [Austin Appleby's reference source](https://github.com/aappleby/smhasher/blob/master/src/MurmurHash3.cpp) | [SMHasher](https://github.com/aappleby/smhasher) |
| Panama | [Daemen/Clapp, “Fast Hashing and Stream Encryption with PANAMA”](https://doi.org/10.1007/3-540-69710-1_5) | Source metadata names Crypto++ [`panama.cpp`](https://github.com/weidai11/cryptopp/blob/master/panama.cpp) and [`TestVectors/panama.txt`](https://github.com/weidai11/cryptopp/blob/master/TestVectors/panama.txt); those vectors identify themselves as coming from the Panama reference implementation, with [sphlib](https://github.com/pornin/sphlib) as another oracle |
| PHOTON-Beetle | [NIST LWC finalists](https://csrc.nist.gov/Projects/lightweight-cryptography/finalists) | [Designers' software implementations](https://github.com/PHOTON-Beetle/Software) |
| RadioGatún | [RadioGatún paper](https://eprint.iacr.org/2006/369) | [sphlib](https://github.com/pornin/sphlib) |
| RIPEMD / RIPEMD-128/160/256/320 | [RIPEMD-160 design page and test vectors](https://homes.esat.kuleuven.be/~bosselae/ripemd160.html), [ISO/IEC 10118-3](https://www.iso.org/standard/67116.html) | Source metadata names Bouncy Castle's [`RIPEMD128Digest`](https://github.com/bcgit/bc-java/blob/master/core/src/main/java/org/bouncycastle/crypto/digests/RIPEMD128Digest.java) and the original RIPEMD-family material; [sphlib](https://github.com/pornin/sphlib) covers the family |
| SHA-1 / SHA-2 | [FIPS 180-4](https://csrc.nist.gov/pubs/fips/180-4/upd1/final) | Source metadata names [OpenSSL SHA-256](https://github.com/openssl/openssl/blob/master/crypto/sha/sha256.c) and the [NIST CAVP secure-hashing vectors](https://csrc.nist.gov/Projects/Cryptographic-Algorithm-Validation-Program/Secure-Hashing); [.NET](https://learn.microsoft.com/dotnet/api/system.security.cryptography) and [Bouncy Castle](https://github.com/bcgit/bc-csharp) provide independent checks |
| Shabal | [NIST SHA-3 project](https://csrc.nist.gov/projects/hash-functions/sha-3-project) | [sphlib](https://github.com/pornin/sphlib) |
| SipHash-2-4 | [SipHash paper/site](https://www.aumasson.jp/siphash/) | [Designers' reference implementation](https://github.com/veorq/siphash), [SMHasher](https://github.com/aappleby/smhasher) |
| Skein | [NIST SHA-3 finalist report](https://csrc.nist.gov/pubs/ir/7896/final), [Skein documentation](https://www.schneier.com/academic/skein/) | [sphlib](https://github.com/pornin/sphlib) |
| SKINNY-Hash | [Registry source](https://github.com/Hawkynt/Hawkynt.github.io/blob/main/Cipher/algorithms/hash/skinny-hash.js), [NIST LWC SKINNY specification](https://csrc.nist.gov/CSRC/media/Projects/lightweight-cryptography/documents/round-2/spec-doc-rnd2/SKINNY-spec-round2.pdf) | The source tests come directly from rweather's [`SKINNY-tk2-HASH.txt`](https://github.com/rweather/lightweight-crypto/blob/master/test/kat/SKINNY-tk2-HASH.txt) and [`SKINNY-tk3-HASH.txt`](https://github.com/rweather/lightweight-crypto/blob/master/test/kat/SKINNY-tk3-HASH.txt), making them the most useful byte-level KAT oracles for these variants |
| SM3 | [GB/T 32905-2016 / ISO/IEC 10118-3 context in RFC 8998](https://www.rfc-editor.org/rfc/rfc8998.html), [ISO/IEC 10118-3](https://www.iso.org/standard/67116.html) | Source metadata names [GmSSL](https://github.com/guanzhi/GmSSL) and Crypto++ [`TestVectors/sm3.txt`](https://github.com/weidai11/cryptopp/blob/master/TestVectors/sm3.txt); [Bouncy Castle](https://github.com/bcgit/bc-csharp) is another oracle |
| Streebog / GOST R 34.11-2012 | [RFC 6986](https://www.rfc-editor.org/rfc/rfc6986.html) | [Bouncy Castle](https://github.com/bcgit/bc-csharp) |
| Subterranean 2.0 | [NIST LWC round 2](https://csrc.nist.gov/projects/lightweight-cryptography/round-2-candidates) | NIST submission package on the round-2 page includes reference code and KATs |
| Tiger | [Designers' Tiger page, paper, reference code and vectors](https://biham.cs.technion.ac.il/Reports/Tiger/) | [sphlib](https://github.com/pornin/sphlib) |
| Whirlpool | [ISO/IEC 10118-3](https://www.iso.org/standard/67116.html) | [sphlib](https://github.com/pornin/sphlib), [Bouncy Castle](https://github.com/bcgit/bc-csharp) |
| Xoodyak | [NIST LWC finalists](https://csrc.nist.gov/Projects/lightweight-cryptography/finalists), [Xoodyak specification](https://keccak.team/xoodyak.html) | [XKCP](https://github.com/XKCP/XKCP) |
| xxHash32 / xxHash64 / XXH3 | [xxHash official repository/documentation](https://github.com/Cyan4973/xxHash), [algorithm documentation](https://github.com/Cyan4973/xxHash/tree/dev/doc) | [Official xxHash implementation](https://github.com/Cyan4973/xxHash), [SMHasher](https://github.com/aappleby/smhasher) |

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see the repository [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE).
