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

Where a JavaScript educational implementation, description, or test vector conflicts with the normative algorithm, the C# port follows the normative algorithm and records the correction in tests/documentation. Two concrete examples are CRC-8/MAXIM-DOW of `123456789`, which is `0xA1` rather than the source registry's `0xA2`, and `lrc.js`, which XORs its bytes before taking two's complement whereas the interoperable Modbus ASCII LRC is the two's complement of the 8-bit byte sum. Source-registry links in the References table therefore document provenance; the external specifications/oracles determine interoperability when the two disagree.

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

## 🔗 References

The conversion provenance is the JavaScript checksum registry at [`Hawkynt/Hawkynt.github.io`](https://github.com/Hawkynt/Hawkynt.github.io/tree/main/Cipher/algorithms/checksum). The source links below are pinned to the revision inspected for this inventory so later edits cannot silently change what “converted from” means. Those source files also contain `documentation`, `references`, and test-vector origins; useful entries from them are carried forward here after checking that they actually describe the managed algorithm. Normative or registration-authority documentation wins when source-registry metadata conflicts with a standard.

| Algorithm / family | Converted source(s) | Specification / documentation | Reference implementation / oracle |
| --- | --- | --- | --- |
| Adler / Adler-32 | [`adler.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/adler.js) | [RFC 1950 — ZLIB / Adler-32](https://www.rfc-editor.org/info/rfc1950/); [zlib manual](https://zlib.net/manual.html) | [zlib `adler32.c`](https://github.com/madler/zlib/blob/develop/adler32.c) |
| Fletcher | [`fletcher.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/fletcher.js) | [RFC 1146 — Fletcher checksum appendices](https://www.rfc-editor.org/info/rfc1146/); [Fletcher's original IEEE paper](https://ieeexplore.ieee.org/document/1094155); [Adler/Fletcher analysis](https://www.zlib.net/tech_report_96.pdf) | — |
| CRC (8–64-bit presets) | [`crc.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/crc.js) | [CRC RevEng catalogue](https://reveng.sourceforge.io/crc-catalogue/); [Peterson & Brown, “Cyclic Codes for Error Detection”](https://dl.acm.org/doi/10.1145/321075.321076); [Koopman CRC research](https://users.ece.cmu.edu/~koopman/crc/) | [CRC RevEng](https://reveng.sourceforge.io/) |
| CRC-128 registry variants | [`crc.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/crc.js) | `Standard`, `Hpc`, and `BigData` are parameterized source-registry variants rather than one external standard | [CRC RevEng](https://reveng.sourceforge.io/) can independently evaluate arbitrary parameter sets |
| BSD / System V `sum` | [`bsd-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/bsd-checksum.js), [`sysv-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/sysv-checksum.js), [`unix-sum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/unix-sum.js) | [GNU Coreutils `sum` documentation](https://www.gnu.org/software/coreutils/manual/html_node/sum-invocation.html); [FreeBSD `sum(1)`](https://man.freebsd.org/cgi/man.cgi?query=sum) | [GNU Coreutils `sum.c`](https://github.com/coreutils/coreutils/blob/master/src/sum.c); GNU `sum` CLI |
| Additive sum / complement / LRC | [`sum-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/sum-checksum.js), [`complement.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/complement.js), [`lrc.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/lrc.js) | Generic arithmetic helpers; [RFC 1071](https://www.rfc-editor.org/info/rfc1071/) and [RFC 1624](https://www.rfc-editor.org/info/rfc1624/) define Internet one's-complement arithmetic; [Modbus Serial Line Appendix B](https://www.modbus.org/file/secure/modbusoverserial.pdf) defines the interoperable 8-bit sum-based LRC | [GNU Coreutils `sum.c`](https://github.com/coreutils/coreutils/blob/master/src/sum.c); [Linux Internet checksum code](https://github.com/torvalds/linux/blob/master/lib/checksum.c); [minimalmodbus](https://github.com/pyhys/minimalmodbus/blob/master/minimalmodbus.py) for Modbus LRC |
| XOR / NMEA-0183 | [`xor-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/xor-checksum.js), [`nmea-0183.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/nmea-0183.js) | [NMEA 0183 standard page](https://www.nmea.org/content/STANDARDS/NMEA_0183_Standard); [GPSD AIVDM/AIVDO checksum documentation](https://gpsd.io/AIVDM.html) | [`pynmea2` checksum implementation](https://github.com/Knio/pynmea2/blob/master/pynmea2/nmea.py) |
| Internet checksum / one's complement | [`internet-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/internet-checksum.js), [`complement.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/complement.js) | [RFC 1071 — Computing the Internet Checksum](https://www.rfc-editor.org/info/rfc1071/); [RFC 1624 — incremental update](https://www.rfc-editor.org/info/rfc1624/) | [Linux `lib/checksum.c`](https://github.com/torvalds/linux/blob/master/lib/checksum.c) |
| Parity / constant-weight helpers | [`parity.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/parity.js), [`constant-weight.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/constant-weight.js) | [Hamming, “Error Detecting and Error Correcting Codes”](https://onlinelibrary.wiley.com/doi/10.1002/j.1538-7305.1950.tb00463.x); [Error Correction Zoo — constant-weight codes](https://errorcorrectionzoo.org/c/constant_weight); [IEEE constant-weight-code paper](https://ieeexplore.ieee.org/document/669415) | — |
| Luhn | [`check-digit.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/check-digit.js), [`luhn.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/luhn.js) | [US Patent 2,950,048](https://patents.google.com/patent/US2950048A/en); [ISO/IEC 7812](https://www.iso.org/standard/70484.html) is a major standardized use | [`python-stdnum` Luhn](https://github.com/arthurdejong/python-stdnum/blob/master/stdnum/luhn.py); [Apache Commons Validator `LuhnCheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/LuhnCheckDigit.html) |
| Verhoeff | [`check-digit.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/check-digit.js), [`verhoeff.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/verhoeff.js) | [J. Verhoeff, “Error detecting decimal codes” (CWI, 1969)](https://ir.cwi.nl/pub/13045); [TU Eindhoven scan](https://pure.tue.nl/ws/files/1951436/597473.pdf) | [`python-stdnum` Verhoeff](https://github.com/arthurdejong/python-stdnum/blob/master/stdnum/verhoeff.py); [Apache Commons Validator `VerhoeffCheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/VerhoeffCheckDigit.html) |
| Damm | [`check-digit.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/check-digit.js), [`damm.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/damm.js) | [H. Michael Damm, “Total anti-symmetrische Quasigruppen” (dissertation, 2004)](https://archiv.ub.uni-marburg.de/diss/z2004/0516/pdf/dhmd.pdf) | [`python-stdnum` Damm](https://github.com/arthurdejong/python-stdnum/blob/master/stdnum/damm.py) |
| ISBN-10 / ISBN-13 | [`isbn.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/isbn.js) | [ISO 2108](https://www.iso.org/standard/36563.html); [International ISBN Agency — ISBN Users' Manual](https://www.isbn-international.org/content/isbn-users-manual/29) | [Apache Commons Validator `ISBNCheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/ISBNCheckDigit.html) |
| GTIN / EAN / UPC | [`gtin-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/gtin-checksum.js), [`ean13-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/ean13-checksum.js), [`ean8-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/ean8-checksum.js), [`upc-ean.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/upc-ean.js), [`upca-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/upca-checksum.js) | [GS1 GTIN standard](https://www.gs1.org/standards/id-keys/gtin); [GS1 EAN/UPC barcodes](https://www.gs1.org/standards/barcodes/ean-upc); [GS1 check-digit calculator](https://www.gs1.org/services/check-digit-calculator) | [`python-stdnum` EAN/GTIN](https://github.com/arthurdejong/python-stdnum/blob/master/stdnum/ean.py); [Apache Commons Validator `EAN13CheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/EAN13CheckDigit.html) |
| IBAN | [`iban-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/iban-checksum.js) | [ISO 13616](https://www.iso.org/standard/81090.html); [SWIFT IBAN documentation / registry](https://www.swift.com/standards/data-standards/iban-international-bank-account-number) | [`python-stdnum` IBAN](https://github.com/arthurdejong/python-stdnum/blob/master/stdnum/iban.py); [Apache Commons Validator `IBANCheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/IBANCheckDigit.html) |
| ICCID | [`iccid-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/iccid-checksum.js) | [ITU-T E.118](https://www.itu.int/rec/T-REC-E.118/en); [GSMA TS.06 numbering material](https://www.gsma.com/aboutus/wp-content/uploads/2014/12/ts.06-v5.0.pdf) | [`python-stdnum` Luhn](https://github.com/arthurdejong/python-stdnum/blob/master/stdnum/luhn.py); [Apache Commons Validator `LuhnCheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/LuhnCheckDigit.html) |
| IMEI | [`imei-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/imei-checksum.js) | [3GPP TS 23.003 — Numbering, addressing and identification](https://portal.3gpp.org/desktopmodules/Specifications/SpecificationDetails.aspx?specificationId=729) | [`python-stdnum` IMEI](https://github.com/arthurdejong/python-stdnum/blob/master/stdnum/imei.py); [Apache Commons Validator `LuhnCheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/LuhnCheckDigit.html) |
| ISSN | [`issn-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/issn-checksum.js) | [ISO 3297](https://www.iso.org/standard/39601.html); [ISSN International Centre — ISSN Manual](https://www.issn.org/understanding-the-issn/assignment-rules/issn-manual-2/); [ISSN Portal](https://portal.issn.org/) | [`python-stdnum` ISSN](https://github.com/arthurdejong/python-stdnum/blob/master/stdnum/issn.py); [Apache Commons Validator `ISSNCheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/ISSNCheckDigit.html) |
| ISIN | [`isin-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/isin-checksum.js) | [ISO 6166:2021](https://www.iso.org/standard/78502.html) | [`python-stdnum` ISIN](https://github.com/arthurdejong/python-stdnum/blob/master/stdnum/isin.py); [Apache Commons Validator `ISINCheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/ISINCheckDigit.html) |
| CUSIP | [`cusip-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/cusip-checksum.js) | [CUSIP Global Services](https://www.cusip.com/identifiers.html) | [`python-stdnum` CUSIP](https://github.com/arthurdejong/python-stdnum/blob/master/stdnum/cusip.py); [Apache Commons Validator `CUSIPCheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/CUSIPCheckDigit.html) |
| SEDOL | [`sedol-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/sedol-checksum.js) | [LSEG SEDOL Masterfile](https://www.lseg.com/en/data-indices-analytics/sedol); [SEDOL technical specification, §8.4](https://www.lseg.com/content/dam/lseg/en_us/documents/sedol/sedol-masterfile-derivative-feed-technical-specification-v5.2.pdf) | [`python-stdnum` SEDOL](https://github.com/arthurdejong/python-stdnum/blob/master/stdnum/gb/sedol.py); [Apache Commons Validator `SedolCheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/SedolCheckDigit.html) |
| VIN | [`vin-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/vin-checksum.js) | [ISO 3779](https://www.iso.org/standard/52200.html); [49 CFR §565.15](https://www.law.cornell.edu/cfr/text/49/565.15); [NHTSA VIN decoder](https://www.nhtsa.gov/vin-decoder) | [`vininfo`](https://github.com/idlesign/vininfo); NHTSA decoder/check-digit tooling |
| ABA routing number | [`aba-routing.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/aba-routing.js) | [Federal Reserve Bank of Chicago Circular No. 2629 — modulus-10 3/7/1 routing-number check](https://fraser.stlouisfed.org/files/docs/historical/frbchi/circ/frbchi_circ_19771019_c2629.pdf); [Federal Reserve Financial Services](https://www.frbservices.org/) | [`python-stdnum` US RTN](https://github.com/arthurdejong/python-stdnum/blob/master/stdnum/us/rtn.py); [Apache Commons Validator `ABANumberCheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/ABANumberCheckDigit.html) |
| NPI | [`npi-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/npi-checksum.js) | [CMS — Requirements for NPI and NPI Check Digit](https://www.cms.gov/Regulations-and-Guidance/Administrative-Simplification/NationalProvIdentStand/Downloads/NPIcheckdigit.pdf); [CMS NPI Registry](https://npiregistry.cms.hhs.gov/) | [Presidio `UsNpiRecognizer`](https://github.com/data-privacy-stack/presidio/blob/main/presidio-analyzer/presidio_analyzer/predefined_recognizers/country_specific/us/us_npi_recognizer.py) — Luhn with CMS `80840` prefix |
| POSTNET / PLANET | [`postnet-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/postnet-checksum.js), [`planet-checksum.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/planet-checksum.js) | [USPS Postal Bulletin 22083](https://about.usps.com/postal-bulletin/2002/html/pb22083/a-d.html); [USPS Publication 25](https://pe.usps.com/text/pub25/welcome.htm); [USPS barcode systems](https://postalpro.usps.com/mailing/barcode-systems) | [`tc-lib-barcode` POSTNET](https://github.com/tecnickcom/tc-lib-barcode/blob/main/src/Type/Linear/Postnet.php); [`tc-lib-barcode` PLANET](https://github.com/tecnickcom/tc-lib-barcode/blob/main/src/Type/Linear/Planet.php) |
| Generic weighted modulo | [`modulo.js`](https://github.com/Hawkynt/Hawkynt.github.io/blob/e7f3ba2d5692935d4f6fb352a229d057d2984486/Cipher/algorithms/checksum/modulo.js) | Generic helper rather than one named standard | [Apache Commons Validator `ModulusCheckDigit`](https://commons.apache.org/proper/commons-validator/apidocs/org/apache/commons/validator/routines/checkdigit/ModulusCheckDigit.html) |
| Reed–Solomon compatibility helpers | Not part of the 37-file JavaScript checksum conversion | [Reed & Solomon, “Polynomial Codes Over Certain Finite Fields” (1960)](https://epubs.siam.org/doi/10.1137/0108018) | — |

The standardized references above define only their documented variants. Adler-16/64, the package's generalized Adler/Fletcher/sum/complement/parity widths, the byte-fed source-registry Fletcher-32/64 forms, and the source-registry CRC-128 presets are compatibility/extensions and should not be mistaken for additional variants defined by those standards.

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see the repository [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE).
