# EWF reference vectors (segment sets)

Made by `EwfSegmentSetTests.Given_Ewfacquire_When_TheSetsAreRegenerated_Then_WeAndEwfexportReadTheMedium`
with `CWB_EWF_WRITE_VECTORS=1`: libewf's `ewfacquire` (Ubuntu 24.04's ewf-tools, in WSL) acquired the
2.5 MiB medium `EwfSegmentSetTests.Medium()` builds, with

    ewfacquire -u -q -t <set> -f <format> -c none -S 1MiB -b 64 medium.raw

| Files | `-f` | Layout |
| --- | --- | --- |
| `encase6.E01.gz` … `encase6.E03.gz` | `encase6` | EnCase 6 tables: chunk offsets relative to a table base offset |
| `encase5.E01.gz` … `encase5.E03.gz` | `encase5` | EnCase 5 tables: chunk offsets relative to the start of the segment file |

Chunks are stored (with their Adler-32), so each 1 MiB segment gzips to a few KiB. The same test
also acquires a deflate-compressed set, checks that `ewfexport` reproduces the medium from every set
(`cmp`), and reads all three with our reader; it ran on the machine that made these files. The
acquisition header carries the acquisition time, so a rerun produces different bytes with the same
media.
