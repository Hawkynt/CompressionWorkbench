# Hawkynt.Algorithms.Checksums

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.Algorithms.Checksums.svg)](https://www.nuget.org/packages/Hawkynt.Algorithms.Checksums/)
[![NuGet downloads](https://img.shields.io/nuget/dt/Hawkynt.Algorithms.Checksums.svg)](https://www.nuget.org/packages/Hawkynt.Algorithms.Checksums/)
[![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE)
[![CI](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml)
![Target](https://img.shields.io/badge/target-net10.0-blue)

> Pure-managed checksums, CRCs, parity/error-detection codes, and check-digit/identifier validators for .NET. The package contains the complete managed C# counterpart of `Cipher/algorithms/checksum` from `Hawkynt/Hawkynt.github.io`.

## Checksum or hash?

A **checksum** is primarily an error-detection code. It maps data to a usually small value so accidental corruption, transmission errors, mistyped identifiers, or damaged storage can be noticed cheaply. CRCs, Adler, Fletcher, Internet checksums, LRC, Luhn, Verhoeff, and Damm belong here. A checksum is generally **not designed to resist an attacker who deliberately constructs a collision**.

A **hash function** is the broader concept of deterministically mapping arbitrary input to a fixed-size digest or an extendable output. Non-cryptographic hashes such as xxHash, FNV, CityHash, or MurmurHash optimize speed and distribution. **Cryptographic hashes** additionally aim for preimage, second-preimage, and collision resistance. Those algorithms live in [`Hawkynt.Algorithms.Hashing`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.Algorithms.Hashing/README.md).

The distinction is about the algorithm's design goal, not what a caller happens to call the output. Using SHA-256 to verify a downloaded file does not turn SHA-256 into a checksum algorithm; it is still a cryptographic hash being used for an integrity-checking purpose. Conversely, a CRC stored beside a file remains a checksum even though people often colloquially call any verification value a “hash”.

## 📦 Installation

```bash
dotnet add package Hawkynt.Algorithms.Checksums
```

## ✨ Features

- Generalized Adler, Fletcher, additive sum, complement, and parity families: runtime widths may be powers of two or any multiple of 8 bits. Adler/Fletcher exclude only the degenerate 1-bit case because their result consists of two equal-width accumulators.
- The historical Adler-16/32/64, Fletcher-8/16/32/64, sum, complement, and parity entry points remain available and are bit-for-bit compatible with the generalized family entry points.
- Generic CRC engine from 8 through 64 bits plus the JavaScript collection's CRC-128 variants.
- Named CRC presets for SMBus, MAXIM/Dallas, AUTOSAR, CDMA2000, CCITT/XMODEM, ARC/IBM, OpenPGP, FlexRay, Interlaken, IEEE, POSIX, BZIP2, Castagnoli/CRC-32C, XZ, ECMA-182, and WE.
- BSD/System-V/Unix sum, XOR, LRC, Internet checksum, and NMEA-0183.
- Luhn, Verhoeff, Damm, modulo, and constant-weight validation helpers.
- ISBN, GTIN/EAN/UPC, IBAN, ICCID, IMEI, ISIN, ISSN, CUSIP, SEDOL, VIN, ABA routing, NPI, POSTNET, and PLANET validation/check-digit helpers.
- Existing CompressionWorkbench checksum APIs retain the `Compression.Core.Checksums` namespace for source compatibility.
- `SupportedChecksumSizes` advertises each bit-oriented checksum family's finite or rule-based output-width domain, with a contract test preventing future checksum APIs from omitting the metadata.

## 🧩 JavaScript source coverage

This table is intentionally file-for-file. Multiple JavaScript files that expose the same mathematical family are implemented by one shared C# type rather than duplicated code.

| JavaScript source | Managed C# counterpart |
| --- | --- |
| `aba-routing.js` | `AbaRouting` |
| `adler.js` | `Adler`; compatibility `Adler32` |
| `bsd-checksum.js` | `BsdChecksum` |
| `check-digit.js` | shared `Luhn`, `Verhoeff`, `Damm` implementations |
| `complement.js` | `ComplementChecksum` |
| `constant-weight.js` | `ConstantWeight` |
| `crc.js` | `Crc`, `CrcParameters`, `Crc128`, preset catalogs |
| `cusip-checksum.js` | `Cusip` |
| `damm.js` | `Damm` |
| `ean13-checksum.js` | `Gtin` EAN-13 helpers |
| `ean8-checksum.js` | `Gtin` EAN-8 helpers |
| `fletcher.js` | `Fletcher` |
| `gtin-checksum.js` | `Gtin` |
| `iban-checksum.js` | `Iban` |
| `iccid-checksum.js` | `Iccid` |
| `imei-checksum.js` | `Imei` |
| `internet-checksum.js` | `InternetChecksum` |
| `isbn.js` | `Isbn` |
| `isin-checksum.js` | `Isin` |
| `issn-checksum.js` | `Issn` |
| `lrc.js` | `Lrc` |
| `luhn.js` | `Luhn` |
| `modulo.js` | `ModuloCheckDigit` |
| `nmea-0183.js` | `Nmea0183` |
| `npi-checksum.js` | `Npi` |
| `parity.js` | `Parity` |
| `planet-checksum.js` | `PostalBarcode` PLANET helpers |
| `postnet-checksum.js` | `PostalBarcode` POSTNET helpers |
| `sedol-checksum.js` | `Sedol` |
| `sum-checksum.js` | `SumChecksum` |
| `sysv-checksum.js` | `SysVChecksum` |
| `unix-sum.js` | `BsdChecksum` / `SysVChecksum` variants |
| `upc-ean.js` | shared `Gtin` implementation |
| `upca-checksum.js` | `Gtin` UPC-A helpers |
| `verhoeff.js` | `Verhoeff` |
| `vin-checksum.js` | `Vin` |
| `xor-checksum.js` | `XorChecksum` |

**Coverage: 37 / 37 JavaScript checksum implementation files.** The JavaScript README is documentation and is not counted as an implementation.

Where a JavaScript educational test vector conflicts with a normative algorithm definition, the C# port follows the normative algorithm and records the corrected vector in tests. For example, CRC-8/MAXIM-DOW of `123456789` is `0xA1`, not `0xA2`.

## 🚀 Quick start

```csharp
using Hawkynt.Algorithms.Checksums;

ReadOnlySpan<byte> data = "CompressionWorkbench"u8;

uint adler32 = Adler.Compute32(data);
byte[] adler40 = Adler.Compute(data, 40);
byte[] fletcher24 = Fletcher.Compute(data, 24);
byte[] sum128 = SumChecksum.Compute(data, 128);
byte[] onesComplement24 = ComplementChecksum.Compute(data, 24, ComplementKind.OnesComplement);
byte[] parity256 = Parity.Compute(data, 256);

uint crc32 = Crc.Compute32(data, CrcPresets.Crc32Ieee);
uint crc32c = Crc.Compute32(data, CrcPresets.Crc32Castagnoli);
ushort internet = InternetChecksum.Compute(data);

bool validIban = Iban.Validate("DE89370400440532013000");
bool validIsbn = Isbn.Validate("9780306406157");
```

Generic family results are returned big-endian. Sub-byte power-of-two results use the low bits of the first byte; unused high bits are zero.

Legacy CompressionWorkbench call sites may continue to use the compatibility types in `Compression.Core.Checksums`.

## 📚 API reference

<!-- API:BEGIN generated by Hawkynt/RepositoryTemplate/package-readme — edit the XML docs in source, not here -->

Every public and protected member of all 69 types, generated from the built assembly and its XML documentation, is in [REFERENCE.md](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.Algorithms.Checksums/REFERENCE.md).

<!-- API:END -->

## 🏗 Architecture

Low-level byte-oriented checksums are separate from textual/check-digit helpers so hot CRC paths do not pull identifier parsing into their implementation. Family variants are parameterized rather than copied into near-identical classes.

The generalized simple-checksum paths use `BigInteger` only where the requested width exceeds primitive storage or the arithmetic requires it; the existing fixed-width entry points remain available for hot paths. Adler derives the largest applicable prime modulus for widths where reduction can affect a `ReadOnlySpan<byte>` input and caches that modulus per half-width.

`Hawkynt.Compression.Core` references this package normally. NuGet consumers therefore receive it as a transitive dependency rather than as a DLL hidden inside the Core package.

## 🔌 Dependencies

| Dependency | Packaging behaviour |
| --- | --- |
| .NET | Targets the repository-wide `net10.0` framework setting |
| Native libraries | None |

## ⚠️ Limitations

- Checksums and check digits detect accidental errors; they do not authenticate hostile input. Use a cryptographic MAC or authenticated signature when an attacker is in scope.
- CRC parameter sets are not interchangeable merely because their bit widths match; polynomial, initialization, reflection, and final-XOR parameters are part of the algorithm identity.
- Extremely large requested generalized checksum widths necessarily allocate an output buffer proportional to that width.
- Reed-Solomon compatibility helpers are retained because existing formats use them, but Reed-Solomon is an error-correcting code rather than a checksum proper.

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see the repository [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE).
