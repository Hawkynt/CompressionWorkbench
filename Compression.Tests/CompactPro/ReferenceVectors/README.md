# Compact Pro reference vectors

| File | Origin |
| --- | --- |
| `trashcht.cpt` | "Trash Chute 2.0" (freeware Mac utility) as distributed on the Garbo Mac archive CD (`cd.textfiles.com/garbo/MAC/SYSUTIL/TRASHCHT.CPT`), MacBinary-unwrapped to its data fork. Written by Compact Pro: an LZH+RLE resource fork and an LZH+RLE data fork. |
| `simsonit.cpt` | "Simsonite 1.1" (freeware), same archive (`UTIL/SIMSONIT.CPT`), unwrapped likewise: LZH+RLE resource and data forks. |
| `dgsmrmcr.cpt` | "DigSim RAM Circuit" sample files, same archive (`UTIL/DGSMRMCR.CPT`): ten data-fork members, RLE-only and LZH+RLE. |
| `ours-flat.cpt`, `ours-folders.cpt` | written by our `CompactProWriter` from the inputs in `CompactProTests.OursArchive`; unar extracts them. The writer must keep reproducing them byte for byte. |
| `unar-listing.txt` | The Unarchiver 1.10.1 (`unar -k visible`, Ubuntu under WSL) extracting every archive above: path, size and SHA-256 of each file; resource forks, which unar writes as AppleDouble `name.rsrc`, are measured on their fork payload. unar's per-archive folder prefix is removed. |

## Encrypted archives

| File | Origin |
| --- | --- |
| `enc-trashcht.cpt` | `trashcht.cpt` with both entries encrypted under `ohsecret` by `encrypt-cpt.py`: LZH resource and data forks, the preamble in the resource fork. |
| `enc-dgsmrmcr.cpt` | `dgsmrmcr.cpt` with all ten entries encrypted under `correct horse battery` (longer than the eight-byte key): data forks only, RLE and LZH, the preamble in the data fork. |
| `enc-ours-flat.cpt` | `ours-flat.cpt` with only `readme.txt` (two forks) encrypted, under `Kennwört` in Mac Roman (`ö` = 0x9A); the other entries stay clear. |
| `macextract-synth.cpt` | written by MacExtract's `tools/synth_cpt.py --password ohsecret` (revision `4b507c21c03377e2224c5c0f4a7eb9be69bbb30e`, 0BSD): RLE-only, data-only, resource-only and two-fork entries. |
| `macextract-listing.txt` | MacExtract's command-line extractor (`macextract x <archive> -p <password>`, same revision, built under WSL) on each archive above with its password and with a wrong one: SHA-256 of every file it wrote, its exit status and its refusals. Under the right password every fork equals the clear original's (see `unar-listing.txt`); under the wrong one it refuses every encrypted entry and still extracts the clear ones. |

`encrypt-cpt.py` lays an archive out afresh with its entries encrypted. It uses MacExtract's
reading of the cipher and a fixed preamble, so its output is reproducible.

**Decryption verified against MacExtract (rev 4b507c2…) only; no real Compact Pro-encrypted sample.**
MacExtract is the implementation the cipher was converted from, so it is not an independent oracle.
No archive encrypted by Compact Pro itself is committed: encrypting needs a registered Compact Pro
under 68k Mac emulation, and no encrypted `.cpt` with a known password is in the public corpora.

The real archives also prove the per-file CRC covers the resource fork followed by the data fork
(macutils' convention), which our reader verifies and XADMaster does not.

## Large archives (not committed)

`CompactProCorpusTests` reads large Compact Pro archives from Info-Mac (the ftp.funet.fi
mirror). Their LZH forks run past one 0x1FFF0-unit block, which none of the fixtures above do.
They are not committed because their redistribution terms vary from package to package.

* `../fetch-info-mac.py <repo>/test-corpus/compactpro` downloads all 14 and BinHex-decodes
  them.
* The test extracts each archive with unar inside Linux (`../unar-manifest.py`), so Mac names
  survive, and compares every data and resource fork.

Measured: 1181 forks identical, and 12 entries with an LZH stream spanning more than one block.
