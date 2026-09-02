# Compression.NativeUI

Cross-platform NativeForms shell for CompressionWorkbench's mounting workflow.

The existing WPF `Compression.UI` remains the full Windows workstation while this frontend grows screen by screen. Mounting starts here because it benefits immediately from one UI contract across the Win32 and GTK NativeForms backends instead of duplicating policy in WPF and a future Linux frontend.

## Current scope

- select a filesystem image;
- detect and probe the concrete filesystem profile through `FormatRegistry`;
- display per-image mountability, mutation model, driver capabilities, and limitations;
- choose read-only or read-write access;
- choose an injected mount backend and resolve support through `Compression.Mounting`;
- provide a mount target and own mount/unmount lifecycle once an `IMountLauncher` is composed.

No filesystem mount backend or launcher is registered by this project yet. That is deliberate: the UI does not invent Dokan/FUSE availability. The Dokan/FUSE composition layer will inject real `IFilesystemMountBackend` implementations and an image/session opener after their dependency probes and callback bridges exist.

Archive files are also rejected for now rather than being mislabeled as filesystem images. They will become mountable through the synthetic archive namespace adapter described in the repository `TODO.md`.

## Run

```text
dotnet run --project Compression.NativeUI/Compression.NativeUI.csproj
```

NativeForms currently provides working Win32 and GTK backends. Its Cocoa backend remains a placeholder, so this frontend intentionally registers Windows and GTK only.
