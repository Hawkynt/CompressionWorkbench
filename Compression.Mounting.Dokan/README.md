# Compression.Mounting.Dokan

Windows Dokany 2 adapter for the mount-neutral `Compression.Mounting` contracts.

The backend does **not** parse filesystem images. CompressionWorkbench opens and
parses every source layer before this project sees the namespace:

`source bytes -> CompressionWorkbench parsers -> IFilesystemSession -> Dokany 2`

## Runtime probe

`DokanRuntimeProbe` checks the application directory and the Windows system
directory for `dokan2.dll` and resolves `DokanVersion` and
`DokanDriverVersion`. The runtime is reported available only when both the
user-mode library and the kernel driver answer successfully; a present DLL on
its own is not treated as evidence.

## Read/write semantics

Read-only and read-write mounts use the same stable-node
`IFilesystemSession` contract. A read-write plan is accepted only when the
resolver has qualified the concrete filesystem profile, backing source, and
Dokan backend for the complete core mutation surface.

The Dokan bridge deliberately implements Windows handle semantics rather than
treating callbacks as independent path operations:

- open handles retain the stable `FilesystemNodeId`, requested access mask,
  share mode, and positional filesystem handle;
- share-mode checks are bidirectional: a new open must be allowed by every
  existing handle and must itself share every access category already granted;
- read/write/truncate operations through a context enforce the access granted to
  that exact handle and never reopen the pathname to bypass it;
- delete callbacks validate only. The namespace entry is removed from
  `Cleanup` when Dokan reports `DeletePending`, while the stable node/file
  handle remains usable until `CloseFile`;
- a pending delete rejects new opens with `STATUS_DELETE_PENDING` and can be
  cancelled when Dokan calls the delete callback again with
  `DeletePending == false`;
- append writes honor both Dokan's write-to-end flag and the Win32
  `offset == -1` append convention;
- paging writes are clipped to the current logical EOF and cannot extend it;
- `SetEndOfFile` changes logical length. Growing `SetAllocationSize` is an
  allocation hint and therefore does not invent an EOF extension when the
  neutral filesystem contract has no preallocation primitive; shrinking it
  truncates as Windows requires;
- timestamp writes are forwarded only when the filesystem profile advertises
  `SetMetadata`.

Dokany handles byte-range locks in the kernel/user-mode library by default.
The backend therefore does not enable `DOKAN_OPTION_FILELOCK_USER_MODE`; the
`LockFile`/`UnlockFile` callbacks intentionally remain unused.

## Qualification

Pure adapter conformance tests exercise D64 persistence plus Windows
share/delete/access/append/paging/allocation semantics without requiring an
installed Dokan driver. Windows OS-integration tests additionally exercise real
Win32 create/write/flush/truncate/rename/delete operations through a mounted
ROMFS image whenever Dokany 2 is available, then reopen the source image and
verify persistence.

Operations for which the neutral filesystem contract has no truthful model are
left unsupported instead of being fabricated. At present that includes Win32
security descriptors, alternate data streams, arbitrary Win32 attribute
mapping, and exact volume free-space reporting.

## References and licensing

The callback behavior is derived from the public Dokany/Dokan.NET contracts:

- Dokan.NET `IDokanOperations` documentation for create/open, cleanup/close,
  delete-pending, write, truncate, and flush behavior;
- Dokany's public `dokan.h` contract for `DOKAN_FILE_INFO`,
  `DeletePending`, paging I/O, write-to-end-of-file, and user-mode file locks;
- Win32 file sharing and append semantics exposed through those contracts.

Dokan.NET is MIT-licensed and the repository is LGPL-3.0-or-later. No Dokan
implementation code is copied or translated; the adapter is original code
against the public API behavior.
