# Now Compress reference vectors

`macroman-names.now` is an authored, directory-only archive: a 24-byte header
and five 110-byte directory records. Each name starts with `n`, followed by up
to 30 successive bytes covering `80` through `FF`. The header starts with
`00 02 00 60 00 01`, stores five records at offset 8 (big-endian), and each
record has directory flag `10` at offset 36, no children, and the sum of its
first 106 bytes at offset 106 (big-endian).

Native `lsar` 1.10.7 accepts all five directories. With `-ja -e MacRoman`, its
names agree with Python's `mac_roman` mapping for every high byte. The C# test
compares with these captured native names in `macroman-names.json`. Unlike
Apple's mapping, .NET's legacy code page uses the Ohm sign for byte `BD`.
Apple's authoritative constants
are in [ROMAN.TXT](https://www.unicode.org/Public/MAPPINGS/VENDORS/APPLE/ROMAN.TXT).
This guards the former missing `F0` mapping (`U+F8FF`), shifted final entries,
`FF` index exception, and incorrect space at `CA` (`U+00A0`).

`native-oracle-facts.json` captures results from eleven public archives in the
[dexvert sample collection](https://sembiance.com/fileFormatSamples/archive/nowCompress/).
The commercial installers and their extracted contents are not redistributed.
Each input is pinned by URL, length, and SHA-256. Native `unar` 1.10.7 and the
C# reader produced byte-identical multisets of paired data/resource forks:
1,596 files, retaining duplicates and each file's fork association.

The committed fingerprint sorts `dataLength:dataSha256:resourceLength:resourceSha256`
lines ordinally, joins them with LF without a trailing LF, then hashes the ASCII
bytes with SHA-256. Directories and filenames are excluded: native extraction
guesses localized encodings and sanitizes host filenames, whereas this reader
uses MacRoman and preserves virtual archive paths. Other Macintosh name
encodings are not guessed.

The native oracle used unmodified
[XADMaster](https://github.com/MacPaw/XADMaster) revision
`7cb9ee0abbb163f261e4cb74501e15067032319c` and
[UniversalDetector](https://github.com/MacPaw/universal-detector) revision
`4eb832d999628edcd3d134e46bd35357c8c99a85`, built using `Makefile.linux` with
Clang, GNUstep, zlib, bzip2, WavPack, ICU and Objective-C runtime development
dependencies. Keep UniversalDetector beside XADMaster, as its makefile expects.
The C# conversion and native oracle share upstream ancestry; this comparison
checks the conversion against the separately compiled native implementation,
not an independently designed decoder. Older unar 1.10.1 fails these inputs.

To reproduce the captured native results on Linux, build those pinned sources,
then run:

```sh
python3 capture-oracle.py --unar /path/to/XADMaster/unar --samples /tmp/nowcompress-samples --download
```

Without `--download`, the script requires previously obtained inputs. It checks
every input and extraction result and fails on any mismatch. No C# code is used
to obtain the expected fingerprints. To compare the C# reader with all eleven
captured results, set `CWB_NOWCOMPRESS_SAMPLES` to that sample directory and run
`NowCompressOracleTests`. Without those inputs the eleven captured cases skip;
the authored filename regression always runs. Ordinary tests never download
commercial samples.
