# Symbian SIS reference packages

All four packages come from the test data of symdev (https://github.com/4akloon/symdev, MIT,
revision 4d7899ebfef4ad815cb5a35393f6f99d1cdad964, `crates/symdev-sis/src/testdata/`). symdev's
tests say who made them: the Symbian S60 3rd Edition SDK's own `makesis.exe` (and `signsis.exe` for
the `.sisx`), run under Wine. They were not built by CompressionWorkbench. symdev keeps them as hex;
`python make-fixtures.py <symdev checkout> .` writes them back as binaries, unchanged.

| File | Made by | Contents |
| --- | --- | --- |
| `hello.sis` | makesis | `hello.exe` (stored) |
| `hello.sisx` | makesis + signsis (DSA-SHA1) | the same `hello.exe`, signed |
| `hello_reg.sis` | makesis | `hello.exe` and a `hello_reg.rsc` (deflated) |
| `gui.sis` | makesis | `gui.exe`, `gui.rsc`, `gui_reg.rsc` (the two `.rsc` deflated) |

`oracle.tsv` gives the target, size and SHA-256 of the input files the packages were built from,
which symdev's tests keep next to them (`hello_type30.hex` from byte 60 on is `hello.exe`;
`exp51_gui_*.hex` are the gui inputs). symdev has no copy of the `hello_reg.rsc` input on its own,
so that file is checked only against the SHA-1 its controller records.

The symdev copyright and licence are in `THIRD-PARTY-NOTICE.symdev.txt`.
