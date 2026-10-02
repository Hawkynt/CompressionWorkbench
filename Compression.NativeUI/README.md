# Compression.NativeUI

The desktop shell for CompressionWorkbench: one NativeForms application over the Win32 and GTK
backends, so Windows and Linux run the same code rather than two frontends drifting apart.

## Screens

- **Ribbon** — the commands live in an Office-style ribbon instead of a menu bar: a Quick Access
  Toolbar (Back, Forward, Up, Refresh), File, Home (clipboard, organize, open, selection), View
  (navigation and preview panes, Details / Thumbnails), Tools (analysis, maintenance, partitions,
  mounting, benchmark), an Archive Tools tab that appears only inside an archive (extract, add, test,
  reconfigure), and a Disk Tools tab, Defragment, that appears while there is something to maintain.
  Every shortcut works form-wide from the ribbon.
- **Archive browser** — the main window, laid out like a file manager. A folder tree on the left
  shows the drives (or `/`) and home, with the open archive grafted in as a folder beside the file it
  lives in; it follows every navigation, however it happened. The breadcrumb bar is also an address
  bar: click its empty space to type or paste a path — one that runs into an archive included —
  with folder completion, and each crumb's chevron lists its subfolders. Back and Forward
  (Alt+Left / Alt+Right) retrace host folders and archive folders alike. File list with name, size,
  compressed size, ratio, method and modified columns; open, extract, create, test; `..` navigation
  that exits an archive into the host filesystem; auto-descent into nested formats; drag in and
  drag out. Rename in place (F2, or Rename in the menus) works on disk, inside ZIP archives and inside
  filesystem images whose driver renames in place (FAT, ext, Minix, the Commodore disk images,
  RomFS). It is lossless there — a ZIP is copied with only its names changed, an image has one
  directory entry changed — and it is not offered for formats that could only rename by
  re-creating themselves. Names Windows or POSIX would refuse are caught before anything is touched. Cut, copy and paste (Ctrl+X / C / V in the list or the tree) move files and
  folders between host folders and archive folders in any direction; nothing is overwritten — a
  taken name gets a number — and a cut removes its sources only after every copy has landed.
  Whatever arrives in a folder is written beside its destination under a staging name and renamed
  into place when complete, so extraction never detours through a temp folder on another volume.
  New Folder (Ctrl+Shift+N) works in folders on disk and opens the new name for editing.
  Rows drag onto a folder in the tree or a folder row in the list: within one disk volume or one
  archive that moves them, anywhere else it copies; files dropped from the desktop copy the same way.
  Rows dragged out of the window arrive in Explorer, Nautilus or Finder as ordinary files. Archive
  entries are not extracted up front: they are decoded only when the target asks and written straight
  into the destination — on Linux and macOS under a temporary name in that folder, renamed when
  complete; on Windows Explorer pulls the stream into the destination itself. Attachments dragged
  from mail clients and other applications that have no file on disk are received the same way,
  written beside the destination and renamed into place, or added to the open archive.
- **Thumbnails** — View → Thumbnails (Ctrl+Shift+2; Details is Ctrl+Shift+6) shows large icons, and
  every entry any image decoder can read gets its picture shrunk into its icon, decoded in the
  background — on disk and inside archives alike. Entries over 16 MB and beyond the first 500 of a
  folder keep their icon.
- **Preview pane** — beside the list (Alt+P toggles it): the selected entry as a picture when any
  of the image decoders reads it, as the start of its text when it reads as text, otherwise its name.
  Entries larger than 32 MB are not read for a glance.
- **Preview** — the selected entry as a picture, as text, or as a hex dump, with an optional
  statistics side panel. Multi-frame images get playback controls.
- **Properties** — sizes, ratio, method and dates, plus byte statistics for a file or a child count
  for a directory.
- **Binary analysis** — magic scan, algorithm fingerprints, entropy map, heatmap, trial
  decompression, chain reconstruction, statistics, strings and a struct-template interpreter.
- **Defragment tab** — maintenance is a contextual ribbon tab rather than a window. It appears while
  the open archive or image, or an archive file selected in a folder, supports any maintenance
  operation (Tools → Maintenance or Ctrl+Shift+D selects it; the Maintenance entries of the list's
  context menu open it with their operation picked). Selecting it hands the client area below the
  breadcrumb bar to the block map; leaving it shows the browser again exactly as it was.

  | Group | Items |
  | --- | --- |
  | Target | The open volume (default), or a selected file whose content — never its name — proves it a container: "Selected: inner.img (Fat)". Changing the selection never retargets; only picking here does |
  | Operation | Defragment, Optimize, Shrink, Compact, Clear (wipe free space), Purge, Scramble as a radio group; Start; Stop while extents are being moved |
  | Defrag Mode | Consolidate (Pack at End as its variant), Defrag (fill holes), Re-order (ascending blocks), Carve Hole with its size, placement and offset fields — all extent moves — and Sort Entries, which sorts every directory by name in place and moves no data |
  | Options | Block interleave (spinner, 1–256), metadata placement, layout profile and Edit Profiles for extent moves; the seed for Scramble and the method for Optimize (Compress, Repack or Canonicalize, as the format offers) appear while their operation is picked |
  | View | Blocks, Circle, 3D Stack; Files panel; Legend; Analyze (read the layout again) |

  Every item asks one adapter (`Maintenance/TargetCapabilities.cs`) whether the target supports it,
  and is disabled with the reason in its tooltip when not. The adapter is a view of the registry's
  `MaintenanceCapabilities.Describe` profile — operations, honoured defrag features — so the tab, the
  CLI and the support matrices cannot disagree; a test holds it to the profile for every registered
  format. Sort Entries is offered for FAT and exFAT. Operations run off the UI thread through
  `MaintenancePresenter`, driving the map live; a refusal (`NotSupportedException`) is reported and
  leaves the image byte-identical, and after a change the archive is re-listed in place. A nested
  archive is maintained as a temporary copy and written back into its host after each change.
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
one window (`maintenance` is the shell on the Defragment tab over a scrambled FAT floppy). It does not write an image: NativeForms has no way to render a window to a bitmap, so CI
takes the picture from outside with an X11 capture tool. Everything that makes a capture
reproducible lives here — fixed payloads, fixed timestamps, a fixed scramble seed, and an analysis
window that leaves its elapsed time out of the status line.

## Run

```text
dotnet run --project Compression.NativeUI/Compression.NativeUI.csproj
```

The GTK backend needs GTK 3 present (`libgtk-3-0` on Debian and Ubuntu).

NativeForms is taken from source when a working copy sits next to this repository
(`../NativeForms`), and from nuget.org otherwise — NativeForms main is usually ahead of its last
stable package. Point elsewhere with `-p:NativeFormsSource=<path>`, or force the packages with
`-p:NativeFormsFromSource=false`. CI clones the commit pinned in `build/NativeForms.ref` into that
sibling position; bumping NativeForms is a one-line change to that file.
