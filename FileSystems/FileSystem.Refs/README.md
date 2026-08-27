# FileSystem.Refs

Pure-managed ReFS 3.x reader and offline mutation core.

The implementation is intentionally not structured as a one-off defragmenter. Parsing, namespace resolution, allocation, metadata graph maintenance, checksums, transactions and placement are separate layers so the same filesystem core can later sit behind a mounted filesystem driver.

## Current architecture

- `RefsVolumeHeader` — VBR/FSRS geometry and checksum.
- `RefsMetadataReader` — SUPB/CHKP bootstrap, root tables, container translation and B+ walking.
- `RefsNamespaceReader` — directory/file resolution, resident streams and extent-backed data.
- `RefsAllocatorMap` / `RefsAllocatorWriter` — allocation visibility plus exact Medium/Container/Small row mutation and tier-local free-run selection.
- `RefsBlockRefcount` — Block Refcount lookup and shared-block detach/decrement semantics.
- `RefsChecksum` — page-reference and SUPB/CHKP checksum algorithms.
- `RefsMetadataGraph` — live MSB+ reachability, parent references, Merkle propagation and page repointing.
- `RefsBootstrapState` / `RefsCheckpointCommitter` — fixed VBR/SUPB anchors, movable checkpoint slots and alternate-checkpoint publication primitive.
- `RefsSchemaCatalog` — self-describing Schema Table definitions and failover validation for schema-aware mutation.
- `RefsMLogCodec` — MLog framing/redo parsing and inner redo-record serialization.
- `RefsPageEditor` / `RefsStreamLayoutEditor` — leaf-row and stream-allocation rewriting.
- `RefsBlockMover` — file-data relocation, scattered relink and shared-block lifetime preservation.
- `RefsMetadataMover` — live MSB+ and CHKP relocation with allocation-safe ordering.
- `RefsMetadataPlacementPlanner` — allocator-tier-aware placement of filesystem structures.
- `RefsPlacementManager` — ReFS-specific multi-pass placement policy (metadata zones, defrag and interleave).
- `RefsMutationModel` — explicit `OfflineQuiescent` versus future `NativeCow` transaction boundary.

## Placement model

ReFS has more than one address space and more than one allocation-ownership domain, so metadata cannot be treated as anonymous raw extents.

Most file and metadata references contain VLCNs. They are translated through the Container Table to physical LCNs. Container-Table roots and the Small Allocator are bootstrap exceptions whose page references contain real LCNs. The MSB+ page header self-LCN tuple uses the same address space as the reference to that page.

Allocation ownership is likewise tiered. Ordinary metadata is Medium-owned, Container-Table metadata is Container-owned, and Small/bootstrap metadata is Small-owned. A physically attractive target in another tier is not a valid destination. `RefsMetadataPlacementPlanner` therefore selects a free run through the owning allocator instead of asking the generic planner to guess and relying on the mover to reject cross-tier placements later.

A metadata move follows this order:

1. Select a destination that is free and addressable in the structure's actual allocator tier.
2. Claim the destination in that allocator before copying. This is required when the page being moved is itself part of an allocator tree.
3. Copy the complete metadata page without destroying the old copy.
4. Stamp the page self-address in the correct virtual/real address space.
5. Repoint every live parent reference and recompute its child digest.
6. Propagate changed checksums to CHKP and refresh the CHKP self-checksum.
7. For CHKP relocation, rewrite every valid fixed SUPB copy and refresh each SUPB self-checksum.
8. Re-open the committed graph and release old allocation only when no final live range occupies it.

VBR and the three SUPB locations are format-fixed anchors and are therefore modelled as fixed structures rather than pretending that every byte on a filesystem is relocatable.

## Shared data

ReFS block cloning and snapshots mean an allocated data cluster can have more than one logical owner. Relocating one stream must therefore not blindly clear the old allocator bit. The current offline mover repoints the stream first, then decrements the affected Block Refcount entries while preserving the dedup ownership flags, recomputes each refcount-row total and Merkle ancestry, and releases only clusters whose final ownership count permits it.

This closes the dangerous case where defragmenting one owner could otherwise free storage still referenced by another stream. Full clone/snapshot creation still requires Block Refcount row creation/increment semantics and remains part of the driver-readiness work.

## Driver-grade target

Offline in-place mutation and a mounted filesystem driver are not the same transaction model. ReFS native writes use copy-on-write metadata, redo-only MLog records and alternating checkpoint commits. The current offline backend can deliberately update a quiescent image directly, but the reusable parsers/editors must not depend on that shortcut.

Some native-transaction pieces are already decoded independently: alternate CHKP preparation/publication and MLog framing/inner redo records. They are deliberately not exposed as a fake mounted write path until immutable replacement-page CoW, log integrity/circular management and recovery are connected into one durability protocol.

The remaining work before this core can honestly be called a complete mounted R/W implementation is tracked in `DRIVER_READINESS.md`. The largest pieces are:

- native CoW metadata transactions that allocate replacement pages rather than overwriting live pages;
- MLog XOR-fold integrity, circular-log management, complete emitted redo payloads and replay/restart;
- the Schema Table key-rule implementations needed by writable tables;
- general B+ insertion/deletion including cascading split, merge, root growth and root shrink;
- complete allocation-zone policy for Medium/Container/Small plus Block Refcount row lifecycle and clone semantics;
- sparse, integrity-stream, snapshot/CoW-version, ADS, security, reparse and journal mutation;
- container create/delete/move, compression/dedup mutation and associated integrity metadata;
- complete create/mkdir/unlink/rmdir/rename/link/truncate/write/flush namespace operations;
- dirty-volume recovery, transaction fault injection and conformance tests against Windows-created ReFS 3.4/3.7/3.9/3.10/3.14 and Insider images.

Until those invariants are decoded and tested, unsupported variants are pinned or refused. Silent approximation is not considered support.
