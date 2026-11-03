# PPMd (`.pmd`) reference vectors

These files were written by Dmitry Shkarin's own PPMd programs. They are the test data for
`PmdShkarinReferenceTests`.

| | |
|---|---|
| Written by | `PPMd.exe` var.H (Apr 21 2001, `ppmdh.rar`) and var.I rev. 1 (Apr 28 2002, `ppmdi1.rar`), both from compression.ru/ds; public domain ("all other things are allowed") |
| Inputs | deterministic generators: 100000 bytes of text, 40000 bytes of mixed text and random runs, the single byte `A`, and 7000 bytes of reversed text |
| Checked | 7-Zip 26.04 extracts the first member of each file to the hash in `manifest.tsv`; it refuses `shkarin-i-freeze.pmd` |

`manifest.tsv` lists the file, the size of the decoded data and its SHA-256. A file with two
members decodes to both members' data, one after the other.

| File | Command |
|---|---|
| `shkarin-h-text.pmd` | `e -o6 -m16` text |
| `shkarin-h-text-1mb.pmd` | `e -o16 -m1` text (the model restarts twice) |
| `shkarin-h-mixed-1mb.pmd` | `e -o4 -m1` mixed |
| `shkarin-h-one.pmd` | `e -o2 -m1` one byte |
| `shkarin-h-two.pmd` | `e -o5 -m4` one byte, then reversed text: two members |
| `shkarin-i-text-r1.pmd` | `e -o8 -m16 -r1` text |
| `shkarin-i-text-cutoff-1mb.pmd` | `e -o16 -m1 -r1` text (the model is cut off) |
| `shkarin-i-text-restart-1mb.pmd` | `e -o16 -m1 -r0` text (the model restarts) |
| `shkarin-i-one.pmd` | `e -o2 -m1` one byte |
| `shkarin-i-two.pmd` | `e -o6 -m4 -r1` reversed text, then one byte: two members |
| `shkarin-h-two-big.pmd` | `e -o6 -m4` text, then reversed text: two members, the first one large |
| `shkarin-i-two-big.pmd` | `e -o6 -m4 -r0` text, then reversed text |
| `shkarin-i-data-o6.pmd` | `e -o6 -m16 -r0` text stored as `data`, the member our writer produces: equal to ours except for the time stamp |
| `shkarin-i-freeze.pmd` | `e -o6 -m1 -r2` text: the "freeze" method, which is refused |
