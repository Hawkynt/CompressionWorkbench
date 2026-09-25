# Compression.Sfx.Shared

Source shared by both self-extracting stubs (`Compression.Sfx.Cli`, `Compression.Sfx.Ui`).

This is a folder of linked sources rather than a project on purpose: a stub is prepended to every
archive built with it, so it must not carry an assembly boundary or a reference it does not need.
The files here are compiled directly into each stub.

`GeneratedRegistrationHost.cs` is compiled only into the universal tier — the carved tier names its
one descriptor directly and never populates a registry.
