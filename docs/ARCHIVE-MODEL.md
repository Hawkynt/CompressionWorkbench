# The Archive Model — capabilities, verbs, and the interfaces that unlock them

This is the single source of truth for **what a format can be asked to do** and
**which interface a descriptor must implement to make each capability appear** in
the UI and CLI. It defines the read/write tiers, the archive vs. pseudo-archive
distinction, the maintenance operations, the block-level layout/display contract,
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

## 3. The maintenance operations

Every maintenance operation is **lossless or refuses**: it keeps every name, byte,
timestamp, attribute and container property it was not asked to change, or it
throws `NotSupportedException` and leaves the target byte for byte as it was. Each
operation is backed by exactly one interface; a format offers it by implementing
that interface and by nothing else. The one discovery surface is
`MaintenanceCapabilities.Describe(descriptor)` (or `MaintenanceOperations.Describe(path)`
in `Compression.Lib`), which returns a `MaintenanceProfile`: the `MaintenanceCapability`
flags, the `DefragFeature` modes the extent defragmenter honours, and the geometry keys.
The CLI, the shell and both package support matrices read it.

The former umbrella **optimize** verb meant five different things depending on the
format — re-encode, canonicalize, rebuild, re-tune geometry, move metadata. It is split
by effect:

| Operation | Effect | Outer size | Interface (`MaintenanceCapability`) |
|---|---|---|---|
| **compress** | Re-encode the payload with the best compression, same format; headers, names, times and attributes carried across. | Reduced, or unchanged when nothing smaller verifies | `ICompressionOptimizable` (`Compress`) |
| **canonicalize** | Rewrite into the canonical representation (MP4 fast start, metadata chunk order, MacBinary header normal form) without re-encoding. | Usually unchanged | `IArchiveCanonicalizable` (`Canonicalize`); every `IFileInternalChunkMover` is one |
| **repack** | Rebuild a container from its own entries, stored bytes copied verbatim, dead space dropped. No recompression. | Reduced or unchanged | `IArchiveRepackable` (`Repack`) |
| **sort entries** | Sort every directory by name in place; only directory records move. | Preserved | `IFilesystemDirectoryOrderer` (`SortDirectoryEntries`) |
| **defrag** | Move file extents so each is contiguous (consolidate at start/end, fill holes, carve a region) or merely reads forwards (ascending order). | Preserved | `IArchiveDefragmentable` + an `IFilesystemBlockMover` (`DefragmentExtents`); the honoured modes are `SupportedDefragFeatures` |
| **change geometry** | Lay the volume out again at another cluster/block/image size. | Any | `ILayoutOptimizable` with `RelayoutPreservesEverything` + options tagged `IsAllocationGeometry` (`ChangeGeometry`) |
| **shrink** | Keep the parameter set; drop trailing free space / step to the smallest canonical size. | Reduced | `IArchiveShrinkable` (`Shrink`) |
| **purge** | Erase all live data, leaving a valid empty container. | Preserved | `IArchivePurgeable` (`Purge`) |
| **wipe** | Overwrite only unused space — free clusters, slack, deleted entries, dead bytes. | Preserved | `IWipeEmpty` (`WipeUnused`) |
| **scramble** | Scatter every allocation block on purpose, so *defrag* has something real to undo. | Preserved | `IFilesystemScrambleable` (`Scramble`) |
| **place** | Put one named owner at one chosen offset, relocating what is in the way. | Preserved | `IFilesystemPlaceable` |
| **compact** | Composite: *defrag → compress → shrink*, each stage only where offered. Never changes geometry. | Reduced | none of its own (`Compact` when any stage is offered) |

**What is deliberately not offered.** An `IArchiveDefragmentable` that only writes the
container out again is a rebuild, not an extent move, and does not count as
`DefragmentExtents`. `ILayoutOptimizable` by itself is not a geometry change: its rebuild
extracts to a folder and creates a new volume from it, and the create API carries a path,
the bytes and a modification time — nothing more. `RelayoutPreservesEverything` defaults to
false and no format sets it yet, so `ChangeGeometry` (and `cwb reconfigure`) refuses
everywhere until a format ships a relayout that carries its whole metadata. The old
`compact --minimal` rebuild is gone for the same reason. `FormatCapabilities.SupportsOptimize`
is the creation-time `method+` hint and claims none of the above.

**purge vs. wipe:** *purge* removes the **live** data (you end up with an empty
container); *wipe* removes only the **dead** data (you keep every live file, but
no recoverable remnants survive in the gaps).

**sort entries vs. defrag:** sorting reorders directory records and nothing else — no
cluster moves, so a defragmented volume stays defragmented and the image keeps its size.
It is for firmware, players and boot loaders that list a directory in on-disk order.

### How the result is verified

Staged operations (compress, canonicalize, repack, change geometry) write a candidate to
scratch space and keep it only when `ArchiveSemanticManifest` — every path, kind, length,
SHA-256, modification time, link target and container property the format's own reader
reports — matches the input's (`MaintenanceVerbs`); a single stream must decode to the same
bytes. In-place sorting runs through `DefragContentGuard.RunVerifiedInPlace`, which journals
every write, checks the manifest and the image length afterwards, and rolls the writes back
on any mismatch. What the reader does not report — DOS attribute bits, owners — is covered
by the real-volume evidence matrix (`docs/MAINTENANCE-MECHANISMS.md`).

## 4. Declaring tunable options — `IFormatOptionsSchema`

For creation, *compress* and *shrink* to expose method/level/parameter
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

The maintenance view draws a **block map** of the real on-disk layout so
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
- **Structural rebuild:** `ILayoutOptimizable` —
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
| `IArchiveDefragmentable`   | **defrag** (with optional `DefragOptions` modes; `SupportedDefragFeatures` names them) |
| `IFilesystemBlockMover` / `[FilesystemBlockMover]` | true in-place defrag (extent moves, no rebuild) — the `DefragmentExtents` capability |
| `ICompressionOptimizable`  | **compress** (lossless re-encode, same format) |
| `IArchiveCanonicalizable`  | **canonicalize** (canonical form, nothing re-encoded) |
| `IArchiveRepackable`       | **repack** (entries copied verbatim, dead space dropped) |
| `IFilesystemDirectoryOrderer` | **sort entries** (directory records only, in place) |
| `IFilesystemScrambleable`  | **scramble** (seeded scatter; no rebuild fallback, refuses instead) |
| `IFilesystemPlaceable`     | **place** (one owner at one offset; no rebuild fallback, refuses instead) |
| `IArchiveShrinkable`       | **shrink** (smallest canonical size / tight-pack) |
| `ILayoutOptimizable`       | geometry analysis and the conversion rebuild; **change geometry** only with `RelayoutPreservesEverything` and options tagged `IsAllocationGeometry` |
| `IWipeEmpty`               | **wipe** (zero unused/slack/deleted) |
| `IFormatOptionsSchema`     | per-format Method/Level/… choices in the create dialog and the compress search |
| `IFilesystemExtentMap` / `IArchiveLayoutMap` | the block-map preview in the maintenance view |
| `IStreamFormatOperations`  | single-stream (de)compression with level/dictionary options |

How each verb is provided — the verified rebuild engine most formats inherit it
from — is in `docs/MAINTENANCE-MECHANISMS.md`. Per-format coverage and R/O/WORM/R-W
state live in the package README tables and are audited against the actual
interface implementations, not advertised intent.
