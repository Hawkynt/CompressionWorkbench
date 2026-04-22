# Multi-Payload Containers Are Archives

## The Principle

> **Any format that packages N discrete, separately-addressable payloads is an archive.**

A format earns archive treatment whenever the binary layout contains:

- A directory or index of named/indexed entries, and
- Each entry can be extracted as an independent blob, and
- A consumer might plausibly want one entry without the others.

This is true regardless of whether the entries are files, images, pages, frames, tracks, layers, tables, fonts, strings, or other domain objects. The `IArchiveFormatOperations` contract — `List` / `Extract` / optional `Create` — describes exactly the interaction model users want for these containers.

The *contents* may remain domain-specific (a TIFF page is still a TIFF; an RT_ICON entry is still an icon), but that's a property of the extracted blobs, not of the container. The workbench just has to learn how to slice the container along its natural payload boundaries.

## Why This Matters Here

CompressionWorkbench already ships ~180 format descriptors. The insight adds a second yield per binary format the workbench touches: every container becomes discoverable, listable, and extractable through the same CLI, UI, and analysis surfaces as ZIP or TAR.

A concrete demonstration: our existing `ResourceDll` writer produces a PE32+ DLL with `RT_RCDATA` resources. The reader today is intentionally narrow — it returns only resources the writer itself placed there. Relaxing the `RT_RCDATA`-only filter turns the same reader into a general PE resource browser: drop `shell32.dll` or a Microangelo-produced icon library on it, and get back one entry per `RT_GROUP_ICON`, `RT_BITMAP`, `RT_MANIFEST`, `RT_STRING`, etc. — a ResourceHacker-style read-only archive.

The same transformation applies to many other common binary containers that are *already* archives-by-structure but have never been presented that way in ordinary file managers.

## Catalog

Candidate formats, roughly ordered by ubiquity × ease of implementation.

### Tier 1 — cheap and high-impact

| Format                                      | Entries become                                                                                                                                                                             | Read                                     | Write                                         |
| ------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------------- | --------------------------------------------- |
| **PE resources** (.dll/.exe/.ocx/.cpl/.sys) | one entry per resource: `RT_GROUP_ICON` reassembled into `.ico`, `RT_BITMAP` → `.bmp`, `RT_MANIFEST` → `.xml`, `RT_STRING` tables → `.txt`, `RT_VERSION` → `.rcv`, raw `RT_RCDATA` by name | relax current `ResourceDllReader` filter | not planned (use resource compilers)          |
| **ICO / CUR**                               | one entry per `ICONDIRENTRY` → `.png` or `.bmp` (named by size/depth)                                                                                                                      | header + directory table                 | reverse (already well-specified)              |
| **Multi-page TIFF**                         | one single-page `.tif` per IFD; rewrite header pointing at one IFD only                                                                                                                    | walk IFD chain                           | rewrite IFD pointers                          |
| **Multi-frame GIF**                         | one `.gif` per frame (single-frame GIF preserving LZW data)                                                                                                                                | parse GIF blocks                         | rebuild header + logical screen for one frame |
| **Animated PNG**                            | one `.png` per frame; apply `dispose` / `blend` against previous frames                                                                                                                    | walk `fcTL`/`fdAT` chunk pairs           | rebuild IHDR+IDAT from a frame                |
| **Font collection (.ttc / .otc)**           | one `.ttf` / `.otf` per member font                                                                                                                                                        | TTC header + offset table                | copy tables, rebuild offset table             |

### Tier 2 — valuable but more work

| Format                       | Entries become                   | Notes                                                         |
| ---------------------------- | -------------------------------- | ------------------------------------------------------------- |
| **MO / PO (gettext)**        | one `.txt` per msgid/msgstr pair | MO is binary, PO is text; both trivial to slice               |
| **ID3 container inside MP3** | album art, URLs, comments        | ID3v2 frames are already addressable                          |
| **PSD layers**               | one `.png` per layer             | needs layer-mask + RLE handling; encoder for PNG              |
| **Keynote/Pages iWork**      | one file per archive part        | iWork `.iwa` uses Protobuf + ZIP + snappy; mostly ZIP already |

### Tier 3 — large surface but possible

| Format                   | Entries become                                 | Notes                                      |
| ------------------------ | ---------------------------------------------- | ------------------------------------------ |
| **MP4 / MOV / MKV**      | one demuxed track per audio/video/subtitle     | Real demuxer needed; large implementation  |
| **Matroska attachments** | as-is binary attachments inside MKV            | straightforward once the box parser exists |
| **SWF embedded assets**  | DefineBits* / DefineSprite blocks → images/swf |                                            |

## Implementation Pattern

All members follow the same skeleton — the `IArchiveFormatOperations` / `IFormatDescriptor` pair we already use for ZIP/TAR/etc., with one convention refinement:

1. **Entry naming** mirrors the natural addressing of the source format. TIFF entries are `page_000.tif`, `page_001.tif`, …. ICO entries are `icon_256x256_32bpp.png`, `cursor_32x32_hotspot_16_16.cur`. Resource-DLL entries are `GROUP_ICON/<name>.ico`, `BITMAP/<id>.bmp`, `MANIFEST/<id>.xml`.
2. **Extension on extract** is the extension of the *extracted payload*, not of the source container. Extracting a TIFF page still gives you a `.tif`; extracting an RT_GROUP_ICON gives you a `.ico`.
3. **Read-only containers** declare only `CanList | CanExtract | CanTest`; attempting `Create` throws `NotSupportedException`. This is the honest shape for most of Tier 2 / Tier 3 — we can slice them but encoding the inner payload back is a separate (usually not worth it) project.
4. **Capability flags** stay the same as other archives. The existing `FormatCapabilities` enum already covers WORM vs. R/W vs. Read-only, and the existing capability-aware CLI + UI paths automatically pick this up.

## Current State

| Status    | Format                                                                                        | Descriptor file                                                                   |
| --------- | --------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------- |
| ✓ shipped | `ResourceDll` (writer-narrow, RT_RCDATA only)                                                 | `FileFormat.ResourceDll/ResourceDllFormatDescriptor.cs`                           |
| ✓ shipped | `PeResources` (read-only, any RT_*; reassembles RT_GROUP_ICON → .ico, wraps RT_BITMAP → .bmp) | `FileFormat.PeResources/PeResourcesFormatDescriptor.cs`                           |
| ✓ shipped | `Ico` (multi-image .ico)                                                                      | `FileFormat.PngCrushAdapters/IcoFormatDescriptor.cs`                              |
| ✓ shipped | `Cur` (multi-image .cur)                                                                      | `FileFormat.PngCrushAdapters/CurFormatDescriptor.cs`                              |
| ✓ shipped | `Ani` (animated cursor)                                                                       | `FileFormat.PngCrushAdapters/AniFormatDescriptor.cs`                              |
| ✓ shipped | `Apng` (animated PNG)                                                                         | `FileFormat.PngCrushAdapters/ApngFormatDescriptor.cs`                             |
| ✓ shipped | `Tiff` (multi-page)                                                                           | `FileFormat.PngCrushAdapters/TiffFormatDescriptor.cs`                             |
| ✓ shipped | `BigTiff` (multi-page > 4 GB)                                                                 | `FileFormat.PngCrushAdapters/BigTiffFormatDescriptor.cs`                          |
| ✓ shipped | `Mng` (multi-image PNG)                                                                       | `FileFormat.PngCrushAdapters/MngFormatDescriptor.cs`                              |
| ✓ shipped | `Fli` (FLI/FLC animation)                                                                     | `FileFormat.PngCrushAdapters/FliFormatDescriptor.cs`                              |
| ✓ shipped | `Dcx` (multi-image PCX)                                                                       | `FileFormat.PngCrushAdapters/DcxFormatDescriptor.cs`                              |
| ✓ shipped | `Icns` (Apple icon)                                                                           | `FileFormat.PngCrushAdapters/IcnsFormatDescriptor.cs`                             |
| ✓ shipped | `Mpo` (stereoscopic JPEG)                                                                     | `FileFormat.PngCrushAdapters/MpoFormatDescriptor.cs`                              |
| ✓ shipped | `Gif` (multi-frame)                                                                           | `FileFormat.Gif/GifFormatDescriptor.cs`                                           |
| ✓ shipped | `Ttc` / `Otc` (font collections)                                                              | `FileFormat.FontCollection/`                                                      |
| ✓ shipped | `Mo` / `Po` (gettext catalogs)                                                                | `FileFormat.Gettext/`                                                             |
| ✓ shipped | `Ttf` / `Otf` glyph-as-archive (cmap + glyf slicing; CFF/OpenType passes through)             | `FileFormat.FontCollection/TtfFormatDescriptor.cs`                                |
| ✓ shipped | `Wav` — full + per-channel WAV + RIFF metadata                                                | `FileFormat.Wav/WavFormatDescriptor.cs`                                           |
| ✓ shipped | `FlacArchive` — full + decoded per-channel WAV                                                | `FileFormat.Flac/FlacArchiveDescriptor.cs`                                        |
| ✓ shipped | `Mp3` — full + ID3v2 text/URL frames + APIC cover art                                         | `FileFormat.Mp3/Mp3FormatDescriptor.cs`                                           |
| ✓ shipped | `Ogg` — full + per-logical-stream packets + Vorbis/Opus comments                              | `FileFormat.Ogg/OggFormatDescriptor.cs`                                           |
| ✓ shipped | `Mp4` / MOV — demuxed tracks (H.264 → Annex-B, audio concatenated)                            | `FileFormat.Mp4/Mp4FormatDescriptor.cs`                                           |
| ✓ shipped | `Mkv` / WebM — demuxed tracks + attachments + chapters                                        | `FileFormat.Matroska/MkvFormatDescriptor.cs`                                      |
| planned   | MP3 Layer III audio decode (per-channel PCM)                                                  | out of scope for current iteration                                                |
| planned   | Vorbis/Opus audio decode (per-channel PCM)                                                    | out of scope for current iteration                                                |
| planned   | Image plane splitting integrated into JPEG/PNG descriptors                                    | `Compression.Core/Image/PlaneSplitter.cs` helper ships; descriptor wiring pending |
| planned   | PSD layers                                                                                    | Tier 2                                                                            |

## Cross-repo reuse

The shipped image-container formats (Ico, Cur, Ani, Apng, Tiff, BigTiff, Mng, Fli, Dcx, Icns, Mpo) are thin adapters over readers from the sibling [`PNGCrushCS`](https://github.com/Hawkynt/PNGCrushCS) repository, linked via `<ProjectReference Include="..\..\PNGCrushCS\…">`. Each adapter is ~30-40 lines:

```csharp
public sealed class IcoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  // … standard descriptor metadata …
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    MultiImageArchiveHelper.List(stream, "icon", ReadAll);
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    MultiImageArchiveHelper.Extract(stream, outputDir, files, "icon", ReadAll);
  private static IReadOnlyList<RawImage> ReadAll(Stream s) =>
    MultiImageArchiveHelper.ToRawImages<IcoFile>(IcoReader.FromStream(s));
}
```

`MultiImageArchiveHelper` handles the common steps: enumerating images, filtering by name pattern, converting incompatible pixel formats to `Rgba32`, encoding via `PngFile.FromRawImage` + `PngWriter.ToBytes`, writing files. Adding a new container is one new descriptor + one entry in `FileFormat.PngCrushAdapters.csproj`'s `<ProjectReference>` list — no parser/decoder code is duplicated across repos.

Build prerequisite: PNGCrushCS must exist as a sibling directory at `..\..\PNGCrushCS` relative to this repo.

## FAQ

### Why not just make the existing `ResourceDll` universal?

The existing descriptor serves two distinct audiences — the workbench *as publisher* (writer emits only what we understand, with stable round-trip guarantees) and the workbench *as reader of unknown input* (read whatever is there). Separating the descriptors keeps the first contract auditable without foreclosing the second.

### Won't this cause collisions — same extension, multiple descriptors?

Yes, and that's fine. The format registry resolves by `(extension, magic)` pair with confidence scores. `.dll` matches both `ResourceDll` (compound `.resource.dll`) and `PeResources` (any `.dll`), but `.resource.dll` wins the compound lookup first. The existing collision-resolution logic applies unchanged.

### What about write support for the read-only ones?

A PE resource editor, a TIFF assembler, an APNG encoder — each is its own substantial project. Read support already delivers most of the user value (browsing, extraction, triage) at a fraction of the code. Write support can be added on demand, one format at a time. The `FormatCapabilities.CanCreate` flag advertises exactly this distinction.

### Do we lose the "honest failure" for formats that aren't really archives?

No. The `IArchiveFormatOperations.List` contract is free to return a single "whole payload" entry (e.g., our stream formats already do this on the `ArchiveOperations.List` side). But formats that can't produce *multiple* addressable entries shouldn't advertise themselves as archives — they should stay in `FormatCategory.Stream`.
