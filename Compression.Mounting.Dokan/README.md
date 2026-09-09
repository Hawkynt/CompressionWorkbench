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

## Current qualification

- read-only mounting, implemented over the stable-node filesystem contract;
- `MountAsync` refuses anything but `MountAccessMode.ReadOnly`, an unsupported
  plan, an unavailable runtime, or a non-Windows host — each with its own
  exception rather than a silent downgrade;
- OS-integration smoke coverage mounts a synthetic namespace, enumerates and
  reads it through Win32, keeps a file open during teardown, and verifies clean
  unmount whenever a Dokany 2 runtime/driver is installed;
- teardown uses Dokan's remove/wait lifecycle; `DokanInstance.Dispose()` is the
  final safety net and, per the upstream binding, closes the native handle only
  after Dokan reports the filesystem dismounted.

`SupportsReadWrite` stays false, and the profile says why: writable Dokan
mounting waits on Windows sharing and delete-pending semantics and on mutation
conformance tests. A format advertising archive-level `CanModify` does not move
that gate; the reasoning is in
[`docs/FILESYSTEM-DRIVER-ARCHITECTURE.md`](../docs/FILESYSTEM-DRIVER-ARCHITECTURE.md).
