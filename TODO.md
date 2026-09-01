# CompressionWorkbench mounting roadmap

## Goal

Expose supported archives, filesystem images, and nested disk/container images as operating-system mounts. The UI and CLI must offer read-only or read-write mounting only when the selected image, its backing source, and the active mount backend can actually satisfy the requested semantics.

A writable offline rebuild is **not** automatically a writable mount. `FormatCapabilities.CanModify` answers whether an existing container can be modified through the archive/image API; mountability is determined by the mount-grade filesystem/provider contracts and their per-image probe result.

### Non-negotiable mount parsing invariant

**If CompressionWorkbench mounts it, CompressionWorkbench parses it.** Every source layer must be decoded by CompressionWorkbench before an OS-facing backend receives the namespace. The only valid boundary is:

`source bytes -> CompressionWorkbench container/archive/partition/filesystem parsers -> IFilesystemSession -> FUSE/Dokan`

Never delegate source interpretation to the host's filesystem or mount stack. This applies even when the current host happens to support the filesystem natively: ext4, NTFS, FAT, XFS, APFS, Btrfs, ZFS, and every other mounted format still go through CompressionWorkbench's own layers. Host support is irrelevant because the same image must resolve on an OS that does not understand that format. FUSE/Dokan are transport adapters for an already-parsed namespace, not alternate filesystem decoders.

## Non-goals

- Do not advertise writable mounting merely because a format can be recreated or rewritten offline.
- Do not emulate missing mutation primitives by silently replacing the entire image for every filesystem call.
- Do not put Dokan/FUSE-specific concepts into format implementations.
- Do not require a native kernel-mode IFS driver before the userspace filesystem contract is proven.
- Do not weaken filesystem-driver-safety or round-trip tests to gain mount checkmarks.

## Architecture

### 1. Mount-neutral host

- [ ] Add `Compression.Mounting` targeting `net10.0`.
- [ ] Define `MountAccessMode` (`ReadOnly`, `ReadWrite`).
- [ ] Define `MountBackendCapabilities`, `MountRequest`, `MountPlan`, `IMountBackend`, and `IMountSession`.
- [ ] Add a capability resolver that combines:
  - backend availability and supported operations;
  - source stream/file writability;
  - `IFilesystemDriverProvider.ProbeFilesystem`;
  - `FilesystemDriverProfile.CanMount` / `CanMountWritable`;
  - the exact `FilesystemDriverCapabilities` needed by the backend.
- [ ] Fail closed: unknown or partially supported semantics produce read-only or unsupported plans, never optimistic read-write.
- [ ] Keep mount lifecycle explicit: start, active session, flush, unmount, dispose.
- [ ] Normalize error mapping in the neutral layer so backends only translate neutral errors to NTSTATUS/errno.

### 2. Virtual namespace adapters

- [ ] Filesystem adapter: map `IFilesystemSession` directly to the neutral mount namespace using stable `FilesystemNodeId` handles.
- [ ] Archive adapter: expose archive entries as a synthetic namespace for read-only mounts.
- [ ] Writable archive adapter: only enable after add/replace/remove/rename/truncate semantics are explicitly modeled and round-trip tested; do not equate `IArchiveModifiable` with random-write file handles.
- [ ] Disk/container adapter: resolve partition/container layers to an `IFilesystemDriverProvider` without copying the complete image when a random-access block device is available.
- [ ] Nested mounts: preserve the chain of outer container -> block device/partition -> filesystem and propagate writability from every layer.

### 3. Windows Dokan backend

- [ ] Add `Compression.Mounting.Dokan` targeting `net10.0-windows`.
- [ ] Use DokanNet 2.x through a narrow backend adapter; keep the rest of the repository independent of Dokan types.
- [ ] Implement mount/unmount and dependency probing (Dokany installed/available).
- [ ] Map open/create/read/write/truncate/flush/enumerate/stat/rename/delete/directory operations to the neutral namespace.
- [ ] Track open handles by stable node ID, not pathname, so rename/unlink cannot invalidate an existing handle.
- [ ] Implement Windows sharing/delete-pending semantics deliberately instead of approximating them with path locks.
- [ ] Map timestamps and attributes without inventing unsupported metadata writes.
- [ ] Add read-only integration tests first; enable read-write integration tests only for driver profiles that advertise the complete required primitive set.

### 4. Linux FUSE backend

- [ ] Add `Compression.Mounting.Fuse` targeting `net10.0`.
- [ ] Prefer a FUSE3 low-level/inode-oriented binding so stable `FilesystemNodeId` maps naturally to FUSE inode/handle semantics.
- [ ] Keep the binding behind `IMountBackend`; if no maintained .NET binding satisfies the requirements, use a small source-owned native interop layer rather than leaking an abandoned package throughout the codebase.
- [ ] Probe `libfuse3`/`fusermount3` availability and report actionable backend limitations.
- [ ] Implement errno mapping, inode lifetime, lookup/reference counts, file handles, readdir offsets, flush/fsync, rename and unlink semantics.
- [ ] Add Linux CI integration tests where `/dev/fuse` is available; keep pure adapter contract tests runnable everywhere.

### 5. macOS / FSKit path

- [ ] Keep the neutral mount contract compatible with a future macFUSE/FUSE3 FSKit backend.
- [ ] Do not make macOS support depend on deprecated kernel extensions.
- [ ] Re-evaluate a native FSKit backend once the neutral namespace and FUSE backend are stable.

### 6. Native Windows IFS path

- [ ] Treat a native Windows IFS/minifilter-style implementation as a separate, later backend/bridge project.
- [ ] Reuse the same userspace protocol and conformance suite rather than reimplementing format logic in kernel mode.
- [ ] Define a narrow IPC protocol before any kernel code: requests, stable handles, cancellation, timeouts, buffer ownership, crash recovery, and forced unmount.
- [ ] Only pursue the kernel driver if Dokan cannot meet a measured requirement (performance, boot-time mounting, paging I/O, security model, etc.).

## Capability truth table

### Read-only mount

A filesystem image may advertise read-only mounting only when all are true:

- [ ] the descriptor provides `IFilesystemDriverProvider`;
- [ ] `ProbeFilesystem` returns `CanMount = true` for this image/profile;
- [ ] the profile provides directory enumeration, data reads, random access, and stable node IDs required by the backend;
- [ ] the selected backend is available on the host;
- [ ] all outer container/partition layers can provide safe random reads.

### Read-write mount

Read-write additionally requires all of the following for the selected profile and mount policy:

- [ ] `CanMountWritable = true`;
- [ ] mutation model is not `None` or `WholeImageRebuild` for ordinary mounted writes;
- [ ] backing source and every outer layer are writable;
- [ ] required data operations (`WriteData`, `Truncate`, `Flush`) are present;
- [ ] required namespace operations (`CreateFile`, `DeleteFile`, `CreateDirectory`, `RemoveDirectory`, `Rename`) are present;
- [ ] backend-specific metadata semantics are either supported or explicitly exposed as unsupported without corrupting data;
- [ ] writable driver safety and round-trip/conformance tests pass for that profile.

Optional primitives such as hard links, symlinks, sparse files, metadata writes and transactions are advertised independently and mapped only when present.

## UI

- [ ] Add a Mount action for a currently opened archive/image/filesystem when at least one backend returns a supported plan.
- [ ] Default to read-only.
- [ ] Offer read-write only when the resolver returns an allowed read-write plan for the concrete image.
- [ ] Show the reason when read-write is unavailable (format profile, backing file, outer container, backend, missing primitive, or dependency).
- [ ] Let the user choose a Windows drive letter or Unix mountpoint as appropriate.
- [ ] Show active mounts and provide explicit unmount/flush actions.
- [ ] Use the same resolver as the CLI; no duplicated UI capability rules.

## CLI

- [ ] Add `mount <image> <target> [--read-only|--read-write] [--backend ...]`.
- [ ] Add `mount capabilities <image>` to print the resolved per-image/backend plan and limitations.
- [ ] Add `unmount <target>` where the backend supports external unmount.
- [ ] Make `--read-write` fail with a precise reason instead of silently falling back to read-only.

## Tests

### Pure contract tests

- [ ] Resolver never offers read-write from `FormatCapabilities.CanModify` alone.
- [ ] Resolver rejects writable mount for `WholeImageRebuild` profiles.
- [ ] Resolver rejects writable mount when the backing stream is read-only.
- [ ] Resolver rejects a profile missing any required core mutation primitive.
- [ ] Resolver preserves optional capabilities independently.
- [ ] Open handles remain usable across rename in the fake filesystem session.
- [ ] Delete/open-handle lifetime behavior is deterministic and backend-independent.
- [ ] Concurrent positional reads do not share a mutable stream cursor.
- [ ] Flush/unmount calls are ordered and idempotent where required.

### Backend conformance

- [ ] Shared backend contract suite using an in-memory fake filesystem.
- [ ] Dokan read-only smoke mount on Windows CI when Dokany is available.
- [ ] Dokan read-write CRUD/rename/truncate/flush suite.
- [ ] FUSE read-only smoke mount on Linux CI when `/dev/fuse` is available.
- [ ] FUSE read-write CRUD/rename/truncate/fsync suite.
- [ ] Cancellation, forced-unmount and backend-crash cleanup tests.

### Format qualification

For every format/profile promoted to mountable:

- [ ] probe is non-destructive and restores stream position;
- [ ] read-only namespace walk matches existing extraction/listing results;
- [ ] random reads match extracted content;
- [ ] writable CRUD operations survive close/reopen;
- [ ] rename/delete semantics preserve unrelated data;
- [ ] allocator/free-space metadata remains structurally valid;
- [ ] journal/checkpoint/recovery semantics are tested when applicable;
- [ ] capability advertising matches the tested profile, not the family name.

## Delivery order

### Phase A — foundation

- [ ] Add `Compression.Mounting` and the capability resolver.
- [ ] Add pure resolver/contract tests.
- [ ] Integrate the project into the solution.

### Phase B — first usable Windows mount

- [ ] Add `Compression.Mounting.Dokan`.
- [ ] Implement read-only filesystem mounting over `IFilesystemSession`.
- [ ] Wire UI Mount action and read-only/read-write selector to the resolver.
- [ ] Add a CLI `mount` command using the same resolver/backend registry.

### Phase C — honest writable mounting

- [ ] Select the simplest already-qualified filesystem driver profiles with native granular mutation support.
- [ ] Complete missing mount-grade primitives where justified.
- [ ] Enable Dokan read-write only after conformance passes.
- [ ] Never promote EROFS/SquashFS/CramFS-style whole-image rebuild support to writable mount capability.

### Phase D — FUSE

- [ ] Implement Linux FUSE3 backend against the same neutral namespace.
- [ ] Share the conformance suite with Dokan.
- [ ] Wire CLI backend selection and mountpoint handling.

### Phase E — archives and nested containers

- [ ] Read-only archive namespace adapter.
- [ ] Explicit writable archive filesystem semantics where safe.
- [ ] Partition/container chaining using `IRandomAccessBlockDevice`.
- [ ] Propagate access mode and limitations through nested layers.

### Phase F — breadth

- [ ] Qualify retro/exotic filesystems with simple allocation/namespace structures first.
- [ ] Expand to journaled/CoW filesystems only after allocator, transaction, crash-consistency and recovery semantics are real.
- [ ] Audit docs/UI/CLI capability tables from the same mechanical source used by runtime resolution.

## Immediate implementation checkpoint

The first code checkpoint is complete when the repository has a buildable mount-neutral project whose resolver can answer, for a concrete `FilesystemDriverProfile` and backend, whether read-only and read-write mounting are allowed and exactly why not. That checkpoint deliberately precedes Dokan/FUSE callbacks so backend code cannot accidentally invent its own capability policy.
