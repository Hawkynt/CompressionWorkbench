# Compression.NativeUI

The desktop shell for CompressionWorkbench: one NativeForms application over the Win32 and GTK
backends, so Windows and Linux run the same code rather than two frontends drifting apart.

## Screens

- **Archive browser** — the main window, laid out like a file manager. A folder tree on the left
  shows the drives (or `/`) and home, with the open archive grafted in as a folder beside the file it
  lives in; it follows every navigation, however it happened. The breadcrumb bar is also an address
  bar: click its empty space to type or paste a path — one that runs into an archive included —
  with folder completion, and each crumb's chevron lists its subfolders. Back and Forward
  (Alt+Left / Alt+Right) retrace host folders and archive folders alike. File list with name, size,
  compressed size, ratio, method and modified columns; open, extract, create, test; `..` navigation
  that exits an archive into the host filesystem; auto-descent into nested formats; drag in and
  drag out. Rename in place (F2, or Rename in the menus) works on disk and inside any archive or
  image whose format can be modified; names Windows or POSIX would refuse are caught before
  anything is touched. Cut, copy and paste (Ctrl+X / C / V in the list or the tree) move files and
  folders between host folders and archive folders in any direction; nothing is overwritten — a
  taken name gets a number — and a cut removes its sources only after every copy has landed.
- **Preview pane** — beside the list (Alt+P toggles it): the selected entry as a picture when any
  of the image decoders reads it, as the start of its text when it reads as text, otherwise its name.
  Entries larger than 32 MB are not read for a glance.
- **Preview** — the selected entry as a picture, as text, or as a hex dump, with an optional
  statistics side panel. Multi-frame images get playback controls.
- **Properties** — sizes, ratio, method and dates, plus byte statistics for a file or a child count
  for a directory.
- **Binary analysis** — magic scan, algorithm fingerprints, entropy map, heatmap, trial
  decompression, chain reconstruction, statistics, strings and a struct-template interpreter.
- **Maintenance** — defragment, optimize, shrink, purge, wipe-empty, compact and scramble, over a
  live block map that can also be projected onto a platter or a stack of platters.
- **Partition editor** — MBR and GPT tables: add, delete, purge, convert, format, verify.
- **Benchmark** — every building block against seven synthetic data patterns.
- **Reverse engineer** — discovers an unknown format by probing a tool or by locating known content
  inside sample archives.
- **Mount** — mounts a filesystem image through Dokan or FUSE.
- **File associations** — Windows only; the window lists what the build recognises everywhere, but
  the registrations are registry keys and the actions are disabled off Windows.

## Backend composition

`Program.cs` is the composition root. It registers the Win32 and GTK NativeForms backends,
initialises the format registry, and then offers exactly the mount backend the host can actually
provide: `DokanFilesystemMountBackend` on Windows, `FuseFilesystemMountBackend` on Linux — and each
only if its own runtime probe reports the dependency present. A backend whose probe fails is not
listed, so the UI never invents Dokan or FUSE availability.

Dokan and FUSE both advertise read-write mounting; the capability resolver still admits that mode
only for an exact filesystem profile whose backing source is writable and whose driver provides the
required mount-grade file mutation primitives. D64/CBM DOS is the first qualified end-to-end
writable profile; its flat namespace intentionally returns unsupported for directory
creation/removal rather than disabling file writes for the whole mount.

NativeForms currently provides working Win32 and GTK backends. Its Cocoa backend remains a
placeholder, so this frontend registers Windows and GTK only.

## Icons

The icon artwork is described once, as vector shapes in `Theming/IconSet.cs`, and rasterized at
whatever size a control asks for. Shipping pre-baked bitmaps instead would blur the moment the shell
ran at a scale factor other than 1.

## Screenshots

`--screenshot=<archive-browser|analysis|maintenance>` builds a deterministic fixture and shows that
one window. It does not write an image: NativeForms has no way to render a window to a bitmap, so CI
takes the picture from outside with an X11 capture tool. Everything that makes a capture
reproducible lives here — fixed payloads, fixed timestamps, a fixed scramble seed, and an analysis
window that leaves its elapsed time out of the status line.

## Run

```text
dotnet run --project Compression.NativeUI/Compression.NativeUI.csproj
```

The GTK backend needs GTK 3 present (`libgtk-3-0` on Debian and Ubuntu).
