# ReFS driver readiness checklist

The goal of `FileSystem.Refs` is a reusable filesystem core, not merely an image extractor or defragmenter. This checklist defines what "100%" means before claiming that the same core is suitable for a mounted read/write driver.

## Implemented foundation

- [x] VBR/SUPB/CHKP bootstrap and active-root selection
- [x] ReFS 3.x container translation (VLCN → PLCN and inverse for mapped physical targets)
- [x] live MSB+ B+ traversal
- [x] namespace walk, resident data and ordinary extent-backed streams
- [x] metadata CRC32-C / ReFS CRC64 / SHA-256 primitives
- [x] parent-reference graph and Merkle checksum propagation
- [x] exact Medium/Container/Small allocator row decoding and bitmap/compact-row mutation
- [x] allocator-tier-aware physically contiguous target selection
- [x] resident-to-extent promotion for offline placement
- [x] whole-file scattered extent relink
- [x] Block Refcount lookup plus detach/decrement semantics for relocated shared extents
- [x] live MSB+ metadata-page relocation in virtual and real root address spaces
- [x] CHKP relocation through all fixed SUPB copies
- [x] explicit fixed VBR/SUPB anchors
- [x] filesystem-specific metadata/data placement manager
- [x] tier-aware metadata-zone placement for Medium/Container/Small-owned structures
- [x] self-describing Schema Table catalog and failover validation
- [x] explicit offline-vs-native mutation transaction boundary
- [x] alternate-CHKP prepare/publish primitive with clock/self-checksum validation
- [x] MLog LogCore/control/data framing parser and inner redo-record codec

## Required for complete native R/W

- [ ] native CoW page allocator and immutable-page mutation path
- [ ] wire alternate-checkpoint publication into NativeCow transactions after CoW + MLog durability
- [ ] MLog entry XOR-fold generation/verification and circular-log writer/control-page advancement
- [ ] redo payload codecs for every opcode emitted by supported ReFS versions
- [ ] redo replay/restarter path and dirty-volume recovery
- [ ] B+ insert/update/delete with split, merge and root height changes
- [ ] implement every Schema Table key-rule selector needed by writable tables (unknown selectors must remain fail-closed)
- [ ] complete Medium Allocator allocation-zone policy rather than row-local free-run selection
- [ ] complete Container Allocator allocation-zone policy
- [ ] complete Small Allocator allocation-zone policy
- [ ] Container Table + Container Index coordinated container create/delete/move
- [ ] Block Refcount row creation/removal, increment/clone semantics and all snapshot/dedup ownership cases
- [ ] hard-link create/remove and link-count/back-reference semantics
- [ ] sparse allocation mutation
- [ ] integrity-stream data-checksum generation/update
- [ ] stream snapshot create/delete/write-CoW semantics
- [ ] named-data/ADS mutation
- [ ] container compression/dedup mutation and integrity metadata
- [ ] security descriptor allocation/update
- [ ] reparse-point mutation
- [ ] USN/journal update semantics where required by the format
- [ ] create, mkdir, unlink, rmdir, rename, link, truncate, read, write and flush operations
- [ ] timestamps/attributes/security updates
- [ ] volume format/create path for each supported ReFS version/profile
- [ ] online locking/concurrency/cache coherency layer suitable for a driver frontend
- [ ] fault-injection tests at every transaction phase
- [ ] Windows mount/chkdsk/fsutil conformance corpus for ReFS 3.4, 3.7, 3.9, 3.10, 3.14 and Insider variants

## Definition of done

A feature is not counted as supported because a parser recognizes its bytes. It is supported only when the implementation can read it, mutate it without dropping semantics, preserve crash-consistency rules appropriate to the selected mutation backend, and round-trip it through independent ReFS tooling or a validated corpus.

Offline maintenance may use `RefsMutationMode.OfflineQuiescent` on an unmounted image. A mounted driver must use `RefsMutationMode.NativeCow`; that mode remains fail-closed until native CoW/MLog/checkpoint transaction semantics are implemented.
