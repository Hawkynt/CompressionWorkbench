# Compact Pro reference vectors

| File | Origin |
| --- | --- |
| `trashcht.cpt` | "Trash Chute 2.0" (freeware Mac utility) as distributed on the Garbo Mac archive CD (`cd.textfiles.com/garbo/MAC/SYSUTIL/TRASHCHT.CPT`), MacBinary-unwrapped to its data fork. Written by Compact Pro: an LZH+RLE resource fork and an LZH+RLE data fork. |
| `simsonit.cpt` | "Simsonite 1.1" (freeware), same archive (`UTIL/SIMSONIT.CPT`), unwrapped likewise: LZH+RLE resource and data forks. |
| `dgsmrmcr.cpt` | "DigSim RAM Circuit" sample files, same archive (`UTIL/DGSMRMCR.CPT`): ten data-fork members, RLE-only and LZH+RLE. |
| `ours-flat.cpt`, `ours-folders.cpt` | written by our `CompactProWriter` from the inputs in `CompactProTests.OursArchive`; unar extracts them. The writer must keep reproducing them byte for byte. |
| `unar-listing.txt` | The Unarchiver 1.10.1 (`unar -k visible`, Ubuntu under WSL) extracting every archive above: path, size and SHA-256 of each file; resource forks, which unar writes as AppleDouble `name.rsrc`, are measured on their fork payload. unar's per-archive folder prefix is removed. |

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
