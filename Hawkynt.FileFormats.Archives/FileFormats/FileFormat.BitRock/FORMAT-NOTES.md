# BitRock / InstallBuilder installer — format notes

Clean-room notes recovered from binary inspection of real InstallBuilder installers
(the `InCAMPro-2.0SP*` Windows installers). Extraction is static — files are recovered
**without executing or installing** the package.

## Top-level layout

A BitRock installer is a **PE stub + appended overlay**:

```
[ PE stub (tclkit interpreter) ]           # code; overlay begins right after the last section
[ content region                ]          # cookfs CFS0002 page archive (see below)
[ 16-byte trailer               ]          # 4× big-endian int32: a,b,c,d
[ "bitrock-lzma-4.0"            ]           # compressor id string
[ 32-byte end magic             ]          # "mFC3acAOJrQinu5aEHu0uH7N5XSQ3Z14"
```

Detection is content-based (extension-independent): the 32-byte end magic, or the
`bitrock-lzma` id together with the Metakit schema string
`dirs[name:S,parent:I,files[name:S,size:I,date:I,contents:B]]`.

### Trailer + Metakit VFS locate

The 16-byte trailer holds four big-endian int32 `a,b,c,d` with `a = 0x80000000`,
top byte of `c = 0x80`, and `b = the Metakit (Mk4) VFS byte length`. The runtime VFS
("JL" datafile) starts at `EOF - 16 - b - 48`. Metakit metadata is base-128
big-endian (high bit marks the final byte); its `dirs`/`files` catalog enumerates the
**tclkit runtime** (~508 files: boot.tcl, images, project/license XML — surfaced under
`runtime/`), *not* the application payload.

## Content region = cookfs `CFS0002` page archive

The real application data lives in the content region as a **cookfs** page archive —
the same on-disk form the tclkit runtime uses. It is decoded deterministically (there
is no guesswork / "filler" heuristic):

```
[ page 0 ][ page 1 ] … [ page N-1 ][ hash table ][ page-size table ][ fsindex ][ footer ]
```

- **Page** = `[1-byte cid][data]`:
  - `cid 0` — stored (raw bytes)
  - `cid 1` — raw DEFLATE, no zlib wrapper (`inflate` with `windowBits = -15`)
  - `cid 2` — bzip2
- **Footer** (last 16 bytes of the content region, ending exactly at the VFS start):
  `idxsize` (BE int32) · `numpages` (BE int32) · 1 byte · `"CFS0002"`.
- Immediately before the footer: a per-page **16-byte MD5** table, then the per-page
  **big-endian int32 size** table, then the (compressed) fsindex.
- Pages are laid out consecutively from
  `startoffset = indexoffset − Σ pageSizes`, which equals the overlay start.
- Typical page is `0x40001` bytes = `cid` + `0x40000` (256 KiB) of data. In the sample:
  `numpages = 2305` (2242 stored + 63 raw-DEFLATE).

**Reconstruction:** strip each page's `cid` byte, decompress the page body per its
`cid`, and concatenate. The result is a plain **gzip member per application component**
(`1f 8b 08 08` + FNAME = the tar name, e.g. `InCAMPro.2.0SP1.246831.Win64.tar`). Each
gzip member wraps a standard `ustar` tar of the application files.

> A naive reader that treats the content region as one gzip stream sees the per-page
> `cid` bytes as stray bytes every ~256 KiB and de-syncs. Reading the cookfs page-size
> table makes the boundaries exact and the decode fully deterministic.

## Tar layer quirks

BitRock's embedded ustar tars have irregular inter-file spacing (small NUL gaps) and a
few files stored a couple of bytes under their declared header size. The reader anchors
on each checksum-validated `ustar` header and takes `min(declaredSize, gap-to-next-header)`,
so every emitted file is byte-exact.

## Extraction output

`List`/`Extract` surface two namespaces:
- `payload/<component>.tar/…` — the application files (one folder per component).
- `runtime/…` — the tclkit runtime VFS.

Verified on `InCAMPro-2.0SP1`: 3 components, **11,952 payload files / ~925 MB**, all
byte-exact and checksum-gated (spot-checked PE / XML / PNG / ZIP members).

## Implementation

- `CookfsArchive.cs` — parses the `CFS0002` footer + page-size table, streams the
  reconstructed per-component content (bounded memory; never holds the full payload).
- `BitRockContentScanner.cs` — reconstructs cookfs content, scans for the gzip-tar
  members, extracts each with the shared Gzip + Tar building blocks straight to disk.
- `BitRockReader.cs` / `BitRockFormatDescriptor.cs` — detection, VFS locate, and List/Extract.
