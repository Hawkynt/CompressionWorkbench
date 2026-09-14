# TFS / historical “BBN Trans-FS” label — provenance notes

> **Verification status (2026-09-14): unverified format identity.**
>
> CompressionWorkbench historically registered this format as **BBN Trans-FS
> (TFS)** and used the four bytes `54 46 53 01` (`"TFS\x01"`) at offset zero as
> a detector. This audit did not locate a normative BBN on-disk specification, a
> reference implementation, a genuine sample image, or an independent source
> establishing that signature. The value therefore no longer participates in
> automatic format detection. `.tfs` remains as an explicit extension-routing
> hint and the input is exposed byte-for-byte as an opaque image.

## Repository provenance

The TFS stub first entered this repository in commit
[`aa11965e987070b56216ab14d70f50cde02d389a`](https://github.com/Hawkynt/CompressionWorkbench/commit/aa11965e987070b56216ab14d70f50cde02d389a),
`+ stub-tier File-System detection (9)` (2026-06-01).

That initial descriptor stated:

- `0x54465301` (`"TFS\x01"`) at offset zero was the magic;
- the block size was 1024 bytes “per the BBN papers”; and
- the on-disk layout was too poorly documented to walk.

The introducing commit contained no citation identifying those papers or a
sample/reference implementation from which either the magic or the block size
could be independently checked. The unsupported 1024-byte geometry claim was
removed earlier; this audit additionally removes the unverified magic from the
registry detector.

The four historical bytes are still reported in `metadata.ini` as
`legacy_magic_hex` / `legacy_magic_match`. That is provenance information only:
a match does not establish format identity or increase parse confidence.

## Historical-source audit

The following public material was checked for a BBN filesystem called Trans-FS
and for an on-disk definition matching the repository assumptions:

- IETF **RFC 832**, *Who Talks TCP?* (1982):
  <https://datatracker.ietf.org/doc/rfc832/>. BBN host rows contain strings such
  as `-tfs`, but the RFC defines the letters independently as `t` = TCP/Telnet,
  `f` = TCP/FTP and `s` = TCP/SMTP. Those rows are service availability flags,
  not evidence of a filesystem named TFS.
- Daniel L. Murphy, **Storage Organization and Management in TENEX** (BBN,
  1972), archived at Bitsavers:
  <https://bitsavers.org/pdf/bbn/tenex/Murphy_TEXEX_storage_1972.pdf>.
  This documents TENEX storage/file-system organization but did not establish
  the Trans-FS label, `TFS\x01` signature or the repository's former 1024-byte
  claim.
- BBN Advanced Computers, **Chrysalis 4.0 Technical Notes** (1988), archived at
  Bitsavers:
  <https://bitsavers.org/pdf/bbn/bbnaci/butterfly_plus/Chrysalis_4.0_Technical_Notes_198801.pdf>.
  The notes describe the Butterfly/Chrysalis software environment, including a
  STREAMS remote-file-system library, but did not provide an on-disk Trans-FS
  format matching the repository assumptions.

Additional searches covered BBN, Bolt Beranek and Newman, Butterfly, Chrysalis,
TENEX, `Trans-FS`, `TransFS`, `TFS`, `0x54465301`, `54 46 53 01` and
`"TFS\x01"`. No source found in this audit established the claimed format.
This is deliberately a statement about the evidence located, not proof that no
such historical system ever existed.

## Current contract

`TfsFormatDescriptor` therefore follows these conservative rules:

1. **No magic-based auto-detection.** `MagicSignatures` is empty. Explicit
   `.tfs` routing remains available.
2. **Opaque read only.** `FULL.tfs` is a byte-exact copy of the supplied stream;
   no inode, directory, allocation or transaction structure is guessed.
3. **Unverified metadata.** `metadata.ini` reports the historical repository
   heuristic only as provenance and always marks parsing/identity as opaque and
   unverified.
4. **No write or maintenance verbs.** Create, modify, compact, defrag, wipe,
   shrink, layout and purge remain unavailable because no verified allocation or
   empty-volume semantics exist from which to implement them safely.

## What would justify deeper support

Before promoting this descriptor beyond the opaque `R` state, obtain at least
one independently attributable artifact and enough structure to cross-check it:

- a genuine BBN manual/paper/source tree that names the filesystem and defines
  its disk structures;
- a genuine image with known file contents plus an independently verifiable
  reader/tool; or
- multiple independent images from which a structure can be derived and then
  validated against an implementation or contemporary documentation.

Only after allocation ownership and namespace traversal are byte-precise should
`IWipeEmpty`, shrinking or defragmentation be considered. R/W additionally needs
transaction/publication semantics so an edit cannot leave an image in a state a
genuine implementation would reject.

## Licensing / implementation method

No external implementation code, comments, tables or expressive structure were
copied or translated. The sources above were used only to test historical claims
and disambiguate public behavior/documentation. The implementation remains
clean-room and is based on the repository's opaque-image contract.
