# Hawkynt.Algorithms.Hashing

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.Algorithms.Hashing.svg)](https://www.nuget.org/packages/Hawkynt.Algorithms.Hashing/)
[![NuGet downloads](https://img.shields.io/nuget/dt/Hawkynt.Algorithms.Hashing.svg)](https://www.nuget.org/packages/Hawkynt.Algorithms.Hashing/)
[![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE)
[![CI](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml)
![Target](https://img.shields.io/badge/target-net10.0-blue)

> Pure-managed cryptographic and non-cryptographic hash functions for .NET, including the managed C# counterparts of `Cipher/algorithms/hash` from `Hawkynt/Hawkynt.github.io`.

## Hash or checksum?

A **hash function** deterministically maps arbitrary input to a fixed-size digest or, for XOF constructions, an arbitrary-length output. A good non-cryptographic hash is designed for speed and statistical distribution; examples are xxHash, FNV, CityHash, and MurmurHash. A **cryptographic hash** adds security requirements: finding preimages, second preimages, or collisions should be computationally infeasible at its intended security level.

A **checksum** is primarily an error-detection code. It is usually smaller and cheaper, and its job is to notice accidental corruption, transmission errors, or mistyped identifiers rather than resist an adversary who deliberately chooses colliding input. CRC, Adler, Fletcher, Internet checksums, Luhn, Verhoeff, and Damm therefore live in [`Hawkynt.Algorithms.Checksums`](../Hawkynt.Algorithms.Checksums/README.md).

The distinction follows the algorithm's design, not the caller's use of it. SHA-256 used to verify a download is still a cryptographic hash; CRC-32 stored beside a file is still a checksum even when a tool labels the field “hash”. A checksum can be excellent engineering for accidental-error detection and still be completely inappropriate for hostile-input authentication.

## 📦 Installation

```bash
dotnet add package Hawkynt.Algorithms.Hashing
```

## ✨ Features

- One-shot and incremental APIs where the underlying construction naturally supports streaming.
- Cryptographic families including MD, SHA-1/SHA-2/SHA-3, Keccak-derived XOFs, BLAKE, RIPEMD, SM3, and legacy/interoperability hashes.
- Fast non-cryptographic hashing including xxHash, MurmurHash, FNV, SipHash, and other source-registry families.
- Source-specific variants are preserved as distinct algorithms when their parameters or output differ from the published standard; they are not silently substituted with a similarly named digest.
- Pure managed implementation surface; no JavaScript runtime is required by the package.

## 🚀 Quick start

```csharp
using Compression.Core.Checksums;
using Hawkynt.Algorithms.Hashing;

ReadOnlySpan<byte> data = "CompressionWorkbench"u8;

byte[] sha256 = Sha256.Compute(data);
byte[] sha512 = Sha512Family.Compute512(data);
byte[] sha3 = Sha3.Compute256(data);
uint murmur = MurmurHash3.Compute32(data);
ulong fnv = Fnv.Compute1A64(data);
```

The historical `Compression.Core.Checksums` namespace is retained for hash types that already shipped there, so existing callers do not need a namespace migration merely because the implementation moved to its own assembly.

## 🧩 JavaScript source conversion

The source-of-truth inventory is the 63 `.js` implementation files in `Hawkynt/Hawkynt.github.io/Cipher/algorithms/hash`. Conversion is tracked file-for-file rather than by a vague algorithm count because one source file may register several variants and two similarly named variants may be intentionally incompatible.

The final coverage table is kept in this README and must account for every source file. Standard algorithms may share a parameterized managed implementation; source-specific DarkCrypt/lightweight/educational variants receive dedicated managed implementations whenever their test vectors differ from the standard construction. JavaScript wrappers are not used as an implementation shortcut.

## 📚 API / architecture

`Hawkynt.Compression.Core` references this project normally, so Core consumers receive the hashing package transitively while callers that only require hashing can reference it directly.

Hash functions and checksums are separate packages intentionally. This prevents a convenience namespace from turning two materially different algorithm classes into one conceptual junk drawer.

## 🔌 Dependencies

| Dependency | Packaging behaviour |
| --- | --- |
| .NET | Targets the repository-wide `net10.0` framework setting |
| Native hashing libraries | None |
| JavaScript runtime | None |

## ⚠️ Limitations

- Legacy hashes such as MD2/MD4/MD5/SHA-1 and historical competition candidates are provided for interoperability, research, and format compatibility; presence in this package is not a recommendation for new security designs.
- Non-cryptographic hashes such as xxHash, FNV, CityHash, and MurmurHash must not be used as authentication primitives.
- A bare cryptographic hash does not authenticate data against an active attacker when the expected digest can also be replaced; use a MAC or digital signature for that threat model.
- Source-specific variants are named explicitly because substituting the closest standard algorithm would produce the wrong bytes while looking deceptively plausible.

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see the repository [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE).
