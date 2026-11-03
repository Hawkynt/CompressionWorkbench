# FLEX reference vectors

| File | What it is |
| --- | --- |
| `flexfloppy.dsk.gz` | 35 × 10 disk that `flexfloppy` (laurent-fr/flextools, commit `7cb69053`, built with gcc on Ubuntu 24.04) formatted, label `CWBFIXTURE`, volume 42, and filled with four files; recipe `generate.sh`; gzipped only to keep the repository small |
| `flexfloppy.manifest` | `sha256sum` of what the same `flexfloppy --extract` returns for that disk |
| `TSCFLX09.manifest` | `sha256sum` of what `flexfloppy --extract` returns for TSC's FLEX 9.1 distribution disk `TSCFLX09.DSK` |

The TSC disk is not committed, because the software on it belongs to its publisher. Get it
from the FUFU archive
(<http://www.evenson-consulting.com/swtpc/downloads/All_Sorted/tsc/TSCFLX09.dsk>;
89,600 bytes, sha256
`cd61d5f398703822a3c064fcd3db8e004f0dd94736c08a5c24eaaa174ac6da5c`) and put it at
`test-corpus/flex/TSCFLX09.dsk`. That directory is git-ignored; the test that reads the
disk is skipped while it is absent.

Quirk observed in `flexfloppy`: it stores a 252-byte file in two sectors, so `EXACT.BIN`
extracts as 504 bytes. Our reader reproduces exactly the sectors it wrote.
