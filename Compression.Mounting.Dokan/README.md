# Compression.Mounting.Dokan

Windows Dokany 2 adapter for the mount-neutral `Compression.Mounting` contracts.

The first slice is intentionally limited to a real runtime probe and a truthful backend profile. It checks the application directory and Windows system directory for `dokan2.dll`, resolves `DokanVersion` and `DokanDriverVersion`, and only reports the runtime as available when both the user-mode library and kernel driver answer successfully.

No filesystem callback is advertised yet. `SupportsReadOnly` and `SupportsReadWrite` remain false until the stable-node-ID callback bridge and its conformance tests exist. A present DLL is not treated as evidence that read or write mounting works.
