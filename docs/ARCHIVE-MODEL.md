# The Archive Model — capabilities, verbs, and the interfaces that unlock them

This is the single source of truth for **what a format can be asked to do** and
**which interface a descriptor must implement to make each capability appear** in
the UI and CLI. It defines the read/write tiers, the archive vs. pseudo-archive
distinction, the five maintenance verbs, the block-level layout/display contract,
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

---

## 3. The five maintenance verbs

All five share one **invariant: live logical content is preserved byte-identical,
the result stays valid (and stays tool-/driver-readable where a checker exists),
and the outer container size is *not increased*.** Each verb says whether it may
*decrease* the outer size.

| Verb         | What it does                                                                                                   | Outer size            | Interface that unlocks it |
|--------------|----------------------------------------------------------------------------------------------------------------|-----------------------|---------------------------|
| **optimize** | Find and apply the **best parameter set** for the data (cluster/block/inode size, FAT bits, geometry, alignment). Does not change which files exist or their bytes. | Preserved if possible | `ILayoutOptimizable` (+ `IFormatOptionsSchema` to declare the tunables) |
| **shrink**   | **Keep the parameter set**; reduce the *stored* footprint by re-encoding payloads with better methods/levels and/or dropping trailing free space / stepping to the smallest canonical container size that still fits. | Preserved, or reduced to the smallest size that holds the content | `IArchiveShrinkable` (+ `IFormatOptionsSchema` for method/level) |
| **defrag**   | **Re-order** the things inside so each file/extent is contiguous (consolidate at start/end, fill holes, carve a region). | Preserved              | `IArchiveDefragmentable`; true in-place moves via `IFilesystemBlockMover` |
| **purge**    | **Erase all live data** from within the container — empty the filesystem / drop every entry — leaving a valid empty container. | Preserved              | `IArchiveModifiable.Remove(all)` or an empty `IArchiveCreatable.Create` *(no dedicated `IArchivePurgeable` yet — see Naming note)* |
| **wipe**     | Overwrite **only unused space** — free clusters/sectors, cluster-tip slack, deleted directory entries, inter-entry padding, dead trailer bytes. Live data untouched. | Preserved              | `IWipeEmpty` (`WipeUnusedSpace(wipeClusterTips, wipeDeletedEntries)`) |
| **compact**  | **Composite**: run *defrag → optimize → shrink* in one pass to produce the smallest valid container that still holds the same contents. With `--minimal`, replace the trio with a single **minimal-geometry rebuild**. | Reduced | Any of `IArchiveDefragmentable` / `IArchiveCreatable` / `IArchiveShrinkable`; `--minimal` also needs `IArchiveCreatable` + `IFormatOptionsSchema` geometry knobs |

**optimize vs. shrink:** *optimize* searches the parameter space (e.g. pick the
cluster size that wastes the least slack) and re-tunes the layout; *shrink* holds
the layout parameters fixed and squeezes the bytes (recompress / drop trailing
slack / step to a smaller standard disc size). Run optimize to choose *how* the
container is shaped; run shrink to make *that* shape as small as it goes.

**purge vs. wipe:** *purge* removes the **live** data (you end up with an empty
container); *wipe* removes only the **dead** data (you keep every live file, but
no recoverable remnants survive in the gaps).

### compact — the one-click composite

**compact** chains the three size-affecting verbs so the user gets "make this as
small as possible while keeping the contents" in a single action:

1. **defrag** — consolidate live data so it is contiguous;
2. **optimize** — re-encode the payload with the best methods (where the format
   is re-encodable: ZIP, gzip/zlib, compound tar, the CVF family, …);
3. **shrink** — truncate the freed tail / step down to the smallest canonical
   size that still fits.

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
new interface — it composes the existing capability interfaces, so any format
that implements at least one of defrag/optimize/shrink gets a compact action.

### Naming note / current divergences (to be reconciled)

The canonical verb set is **optimize · shrink · defrag · purge · wipe**, and
`IWipeEmpty` backs **wipe** (this is the established name across the code and UI —
the "clean" alias is retired). Two items still diverge:

- A dedicated **purge (empty-all)** verb has no interface yet; it is realised by
  `IArchiveModifiable.Remove` over all entries (or a fresh empty `Create`). A
  future `IArchivePurgeable` could formalise it.
- `IArchiveShrinkable`'s current implementation focuses on the *container-size*
  step-down; the *re-encode-payload-with-better-methods* half of **shrink** is
  presently driven by the compression optimizer + `IFormatOptionsSchema`.

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

The Defragment/Optimize window draws a **block map** of the real on-disk layout so
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
| `IArchiveShrinkable`       | **shrink** (smallest canonical size / tight-pack) |
| `ILayoutOptimizable`       | **optimize** (parameter retune, in-place or streaming) |
| `IWipeEmpty`               | **wipe** (zero unused/slack/deleted) |
| `IFormatOptionsSchema`     | per-format Method/Level/… choices in the create + optimize/shrink dialogs |
| `IFilesystemExtentMap` / `IArchiveLayoutMap` | the block-map preview in the Defrag/Optimize window |
| `IStreamFormatOperations`  | single-stream (de)compression with level/dictionary options |

Coverage counts per verb live in `docs/OPERATION_COVERAGE.md`; per-format R/O/WORM/R-W
state lives in the README tables and is audited against the actual interface
implementations, not advertised intent.
