# The Archive Model — capabilities, verbs, and the interfaces that unlock them

This is the single source of truth for **what a format can be asked to do** and
**which interface a descriptor must implement to make each capability appear** in
the UI and CLI. It defines the read/write tiers, the archive vs. pseudo-archive
distinction, the separated maintenance capabilities, the block-level layout/display contract,
and the streaming model that lets us operate on arbitrarily large images without
running out of memory.

A descriptor advertises what it *can* do two ways, which must agree:

- a `FormatCapabilities` bit (`Capabilities`), used for quick UI gating, and
- the concrete capability **interface** it implements, which carries the actual
  method the orchestrator calls (`if (ops is IArchiveDefragmentable d) …`).

> **Never advertise a capability you have not verified at its acceptance gate.**
> See `CONTRIBUTING.md` → *Format Capability Tiers* for the promotion ladder.

---

## 1. Read / WORM / Read-Write model

Write capability is a four-level scale (see `FormatCapabilities`):

| Tier      | Meaning                                                                          | Capability bits                                   | Interface(s) to implement |
|-----------|----------------------------------------------------------------------------------|---------------------------------------------------|---------------------------|
| **R/O**   | List / Extract / Test only. No creation.                                         | `CanList`, `CanExtract`, `CanTest`                | `IArchiveFormatOperations` |
| **WORM**  | Write-Once-Read-Many: produce a fresh archive/image from inputs; an existing instance is not offered for modification. | `+ CanCreate`            | `+ IArchiveCreatable` |
| **R/W**   | Modify an existing archive: add / replace / remove entries, producing a valid result. | `+ CanModify` (implies `CanCreate`)  | `+ IArchiveModifiable` |

**R/W means a working modify on an existing container.** The edit may be byte-preserving
in place (R/W filesystem block writes, ZIP/TAR/XAR member edits, byte-identity append, the
CVF in-place writers, a disk-image container delegating to a R/W inner filesystem) **or** it
may relayout / re-pack the container, moving existing data (NTFS/XFS/Btrfs/ReiserFS re-pack
the whole image; 7-Zip/CAB/RAR rewrite their solid streams via the verified extract →
re-create rebuild). Both are honest R/W for a *conceptually read-write* format — an edit that
must move data is still R/W, not a fake.

`CanModify` is **withheld** only from **read-only-by-design** formats (CramFS, SquashFS) and
**create-only** formats (e.g. the checksum-record archives Sqx/Wim/Swm/Ace) — they may still
back the verbs with a rebuild for convenience, but they do not present themselves as editable.
`WriteCapabilityHonestyTests` enforces the one hard rule: every `CanModify` claimant must
implement `IArchiveModifiable` (a real modify path — no unbacked flag).

Single-stream compression formats (gzip, xz, lzma…) are their own axis:
they implement `IStreamFormatOperations` (Compress/Decompress, plus
`Compress(…, FormatCreateOptions)` for level/dictionary tunables). They are
inherently streaming (see §5).

---

## 2. Archive vs. pseudo-archive

Both expose the **same interfaces** and the same R/O/WORM/R/W tiers. The
difference is purely about the container's *native purpose*:

- **Archive** — a container whose reason to exist is "hold N things": ZIP, 7z,
  TAR, LZH/ARJ, cabinet files, and **filesystem images** (FAT, ext, NTFS, …). The
  N entries are files/directories.
- **Pseudo-archive** — a file whose native purpose is something else (an image, a
  sound, an executable, a document) that we *also* expose as a list of N
  extractable parts, because the user-facing question is "can I list and extract
  the things inside?", not "is this called ZIP". Examples: PE resource DLLs (one
  entry per `RT_*` resource), multi-page TIFF / multi-frame GIF, font collections,
  PSD layer stacks, MPEG transport streams, and audio files exposed as
  per-channel / per-stream / per-tag entries (`AudioPseudoArchive`).

There is no separate API for pseudo-archives: a TIFF descriptor implements
`IArchiveFormatOperations` (+ `IArchiveCreatable`/`IArchiveModifiable` where the
format allows) exactly like ZIP. The README's *Archives and Pseudo-archives*
section is the catalogue; this section is the rule.

### 2.1 Structured serialization documents

JSON, XML, MessagePack, Python pickle, Perl Storable, MS-NRBF, `.reg` exports and
the two Windows registry hive layouts all project through one shared model in
`FileFormat.Structured`: a map/object becomes a folder, an array becomes a folder
of deterministic `[NNNNNN]` indices, and a scalar becomes a virtual file holding
its bytes. Path segments are UTF-8 percent-escaped, Windows device names
included, so a projected name is always a legal one.

None of them parses by handing the document to the format's own runtime. Pickle
is walked as opcodes, Storable and NRBF as records; no module is imported, no
serialized type is activated, no constructor or callback runs, and the live
Windows registry is never opened. `.reg` is an offline text interchange format
here and nothing more.

**Their capability tiers are gated on third-party bytes, in both directions.**
`Compression.Tests/StructuredPseudoArchives/ReferenceVectors` holds output from
CPython's `pickle` and `json`, python-msgpack, Windows `reg.exe export`, Perl
`nstore`, .NET `BinaryFormatter`, and two real registry hives. The reading
direction asserts our reader recovers the exact payload from those bytes; the
writing direction asserts our writer emits the very bytes the reference
implementation emits for the same value. Both run in the gating test tier, which
is the point: the predecessor of the CREG assertion sat in `ExternalInterop`,
failed against the only real hive it was pointed at, and merged regardless.

On top of the frozen bytes, `StructuredPseudoArchiveExternalToolTests` hands our
live output to CPython, Perl and `reg.exe` and compares what they recover. It has
its own `ci.yml` step with no `continue-on-error`, alongside the GFS2 and bcachefs
oracles: a missing tool skips its case and says so, a present tool that rejects
our bytes fails the build.

Where byte-for-byte parity is not a well-defined question the matrix says so
rather than implying a gate that does not exist. Two cases:

- **XML** — no two XML writers agree on declaration quoting, attribute order and
  namespace placement, so there is nothing to compare our envelope against. The
  reading direction is gated; the writing direction is exercised live against a
  real parser in the advisory `ExternalInterop` tier.
- **Pickle** — CPython's memo is keyed on object *identity*, so its output for a
  given value is not a function of that value alone. Our writer matches it for
  any graph whose leaves are distinct objects, which is what the vector pins, and
  the vector's inputs are chosen to keep that true.

---

## 3. Maintenance capabilities are orthogonal

"Optimize" is not a capability. It used to collapse unrelated byte-rewriting
operations into one UI verb, which made a geometry selector look equivalent to a
compressor or a physical defragmenter. Capability discovery is now effect-specific:

| UI action | What it changes | Interface that unlocks it |
|-----------|-----------------|---------------------------|
| **Compress** | Re-encodes live payloads using better compression choices. | `ICompressionOptimizable` |
| **Canonicalize** | Rewrites format-defined headers, padding, metadata placement, or other canonical representation details. | `IArchiveCanonicalizable` |
| **Repack** | Rebuilds the same logical entries without implying recompression. | `IArchiveRepackable` |
| **Sort directory entries** | Changes directory-table ordering without moving payload extents merely for ordering. | `IFilesystemDirectoryOrderer` |
| **Defragment extents** | Physically moves allocation extents to reduce fragmentation. | `IArchiveDefragmentable` + `IFilesystemBlockMover` |
| **Change allocation geometry** | Changes cluster/block/inode/FAT/geometry choices while preserving logical contents. | `ILayoutOptimizable` (+ `IArchiveCreatable` and a non-empty `IFormatOptionsSchema` for the current UI) |
| **Shrink** | Keeps the selected parameter set and reduces the stored footprint / trailing free space where supported. | `IArchiveShrinkable` |
| **Purge** | Erases all live data while leaving a valid empty container. | `IArchiveModifiable.Remove(all)` or an empty `IArchiveCreatable.Create` |
| **Wipe** | Overwrites only unused/slack/deleted space. | `IWipeEmpty` |
| **Scramble** | Deliberately scatters allocation blocks. | `IFilesystemScrambleable` |
| **Place** | Places one named owner at a chosen offset. | `IFilesystemPlaceable` |

These interfaces are intentionally independent. A descriptor may expose several,
but no capability is inferred merely because another rewrite is possible. In
particular, `ILayoutOptimizable` does **not** imply compression optimization, and
`IArchiveDefragmentable` without an exposed `IFilesystemBlockMover` does not
unlock the **Defragment extents** action.

**purge vs. wipe:** *purge* removes the **live** data (you end up with an empty
container); *wipe* removes only the **dead** data (you keep every live file, but
no recoverable remnants survive in the gaps).

### compact — the one-click composite

**compact** chains the three size-affecting verbs so the user gets "make this as
small as possible while keeping the contents" in a single action:

1. **defragment extents** where a real block mover exists;
2. **compress** where the format exposes compression optimization;
3. **shrink** the freed tail / step down to the smallest canonical size that still fits.

Contents are preserved byte-for-byte. The default compact yields the smallest
**standard, still-valid** container.

**`--minimal` (opt-in).** Replaces the trio with a single **minimal-geometry
rebuild**: the contents are extracted and the container is re-created at the
smallest geometry the format allows — auto-fit image size, smallest allocation
unit, and a root directory / metadata area sized to exactly the entries present.
This is driven generically: `CompactOperation` selects the minimal value for each
geometry knob the descriptor's `IFormatOptionsSchema` declares (image size →
auto-fit, cluster/block → smallest, root/inode count → smallest) and sets a
universal `MinimalGeometry=true` create flag that writers honour by dropping
their free-space headroom. A 1.44 MB FAT floppy holding a few KB collapses to a
few KB — but the result is **no longer a standard, mountable floppy** (the FAT
table and root directory are crippled to the minimum). The rebuild only swaps in
the new image when it both round-trips (lists every entry) and is actually
smaller; otherwise the original is left untouched.

Compact is surfaced as `cwb compact <file> [--minimal]` and as the explorer's
**Maintenance → Compact** entry (with a *Minimal geometry* checkbox). It is not a
new interface — it composes existing maintenance capabilities. Allocation-geometry
changes remain explicit and are not silently folded into compact.

### Naming note

`IWipeEmpty` continues to back **wipe**. A dedicated purge interface is still
optional because purge can be realised through existing mutation/create contracts.

---

## 4. Declaring tunable options — `IFormatOptionsSchema`

For *optimize* and *shrink* (and creation) to expose method/level/parameter
choices in the Convert dialog and the CLI's `--opt key=value`, the descriptor
implements **`IFormatOptionsSchema`**, returning a list of
`FormatOptionDescriptor`:

```
new FormatOptionDescriptor(
    Key: "Method", DisplayName: "Compression", Kind: FormatOptionKind.Enum,
    Default: "Auto", AllowedValues: ["Stored", "JM", "SQ", "Auto"],
    Description: "…explain each value + 3rd-party/OS compatibility…",
    DependsOn: "Compatibility=Genuine")   // cascading: only shown when applicable
```

`Kind` ∈ `String | Integer | Boolean | Enum`; `DependsOn` gates an option on
another's value. The dialog/CLI collect values into
`FormatCreateOptions.FormatSpecific`; the writer reads them via
`options.GetOption / GetOptionInt / GetOptionBool`. Each option's `Description`
**must** state what it means and, where relevant, which third-party software / OS
(and which versions) can read the result.

---

## 5. Block-based layout & display contract

The Maintenance window draws a **block map** of the real on-disk layout so
the user sees the actual fragmentation/free/metadata picture *before* acting. A
descriptor feeds that map by enumerating `DefragBlockInfo` runs:

- **`IFilesystemExtentMap`** — for filesystem images. Yields one block per
  contiguous cluster run per file, per metadata-reserved region (boot/FAT/bitmap/
  superblock/MFT/root/inode table/…), and optionally free regions.
- **`IArchiveLayoutMap`** — the archive equivalent: every entry header, compressed
  payload, and inter-entry gap at its real byte offset.

Both may be **sparse** — gaps in the yielded set are treated as
`DefragBlockKind.Free` by the caller, which sorts and gap-fills. Enumeration must
never throw on a malformed image (yield what you can and return) and must **not**
dispose the stream.

`DefragBlockKind` = `Free | Used | Bad | MetadataReserved | InProgress`;
`DefragBlockInfo` also carries `FileName` and a thermal `DefragBlockClass`
(hot/normal/frozen by mtime) for tile colouring and placement.

**True in-place defrag** (move extents without a full rebuild) additionally
implements **`IFilesystemBlockMover`**: `MoveExtent` does the raw byte copy and
`UpdateAllocationAfterMove` patches the allocation metadata (FAT chain, dir
start-cluster, bitmap bits) so the file stays reachable. Without it, defrag falls
back to a rebuild.

---

## 6. Streaming — arbitrary sizes without OOM

Nothing in the pipeline should require holding a whole image (or a whole entry) in
RAM. The streaming contracts:

- **Create:** `IArchiveCreatable.CreateFromStreams(target, IEnumerable<StreamingArchiveInput>, options)`
  — a two-pass writer: pass 1 uses each input's pre-known *size* to plan
  layout/geometry; pass 2 copies each entry through a 64 KB chunk buffer. Inputs
  arrive as `(name, size, openStream)`; the streams are typically
  `BoundedEntryStream`s, so the writer physically cannot read past an entry's
  logical size (slack/padding/neighbours are unreachable). The default
  implementation buffers to memory and calls `Create`; FAT/ext/ZIP-store override
  it. Peak memory is the chunk buffer + the format's own metadata tables.
- **Optimize / structural rebuild:** `ILayoutOptimizable` —
  `AnalyzeLayout` reads only the superblock/BPB (never the whole image);
  `ApplyMetadata` patches a handful of bytes in place (label/serial/geometry);
  `RebuildStreaming(source, target, options)` does cluster/block-size/FAT-type
  changes reading source sequentially and writing target sequentially, with peak
  memory bounded by `O(max(FAT table, directory tree))`, not image size.
- **Extract:** `IArchiveInMemoryExtract.ExtractEntry(input, name, output, password)`
  streams one entry straight to a `Stream` with no temp-dir round-trip (used by
  the recursive-descent driver for nested containers).
- **Single-stream codecs:** `IStreamFormatOperations` Compress/Decompress are
  stream-to-stream by construction.
- **Helpers** (`Compression.Registry/Streaming/`): `BoundedEntryStream`,
  `BoundedWriteStream`, `DeferredLengthWriteStream`, `ReadOnlyStreamSlice` — use
  these so a reader/writer is physically bounded to one entry's bytes.

A format that does not override the streaming paths still works (the defaults
buffer), but is bounded by RAM; override them to handle multi-GB/TB images.

---

## 7. Quick reference — interface ⇒ what it unlocks

| Implement…                 | …and the format gains |
|----------------------------|-----------------------|
| `IArchiveFormatOperations` | List / Extract / Test (R/O) |
| `IArchiveInMemoryExtract`  | temp-free single-entry extraction (nested-archive descent) |
| `IArchiveCreatable`        | Create (WORM); override `CreateFromStreams` for OOM-free creation |
| `IArchiveModifiable`       | Add / Replace / Remove + **purge** (Remove-all). Advertise `CanModify` (R/W) when the format is a mutable container with a working modify — in place **or** relayout/rebuild (see §1); withhold it from read-only-by-design / create-only formats. |
| `IArchiveDefragmentable`   | **defrag** (with optional `DefragOptions` modes) |
| `IFilesystemBlockMover`    | true in-place defrag (extent moves, no rebuild) |
| `IFilesystemScrambleable`  | **scramble** (seeded scatter; no rebuild fallback, refuses instead) |
| `IFilesystemPlaceable`     | **place** (one owner at one offset; no rebuild fallback, refuses instead) |
| `IArchiveShrinkable`       | **shrink** (smallest canonical size / tight-pack) |
| `ICompressionOptimizable` | **compress** |
| `IArchiveCanonicalizable` | **canonicalize** |
| `IArchiveRepackable` | **repack** |
| `IFilesystemDirectoryOrderer` | **sort directory entries** |
| `IFilesystemBlockMover` + `IArchiveDefragmentable` | **defragment extents** |
| `ILayoutOptimizable`       | **change allocation geometry** (parameter retune, in-place or streaming) |
| `IWipeEmpty`               | **wipe** (zero unused/slack/deleted) |
| `IFormatOptionsSchema`     | per-format Method/Level/… choices in the create + optimize/shrink dialogs |
| `IFilesystemExtentMap` / `IArchiveLayoutMap` | the block-map preview in the Maintenance window |
| `IStreamFormatOperations`  | single-stream (de)compression with level/dictionary options |

How each verb is provided — the verified rebuild engine most formats inherit it
from — is in `docs/MAINTENANCE-MECHANISMS.md`. Per-format coverage and R/O/WORM/R-W
state live in the package README tables and are audited against the actual
interface implementations, not advertised intent.
