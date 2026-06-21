#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.AmigaPfs;

/// <summary>
/// R/W descriptor for the Amiga Professional File System (PFS3 / PFS3aio).
/// Signature "PFS\x02"/"PFS\x03"/"PFSa" at offset 0 of the boot block.
///
/// Stage 1 caveat: only direct-block file references are extractable; multi-
/// block files requiring full anode-tree traversal will report a partial
/// extraction. The reader robustly lists all dirblock entries regardless.
/// Stage 1 writer emits boot + root + linear dirblock chain + contiguous
/// per-file data extents (anode-as-direct-block convention) — self-round-trip
/// clean with the matching reader. Stage 1 R/W (this descriptor) adds in-place
/// Add/Remove against the same shape via <see cref="AmigaPfsModifier"/>; image
/// is still <em>not</em> FS-UAE/WinUAE mountable (full PFS3aio anode-table /
/// bitmap / rootinfo emission deferred to a future Stage 2 promotion).
/// </summary>
public sealed class AmigaPfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable {
  public string Id => "AmigaPfs";
  public string DisplayName => "Amiga Professional FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".pfs";
  public IReadOnlyList<string> Extensions => [".pfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'P', (byte)'F', (byte)'S', 0x02], Offset: 0, Confidence: 0.95),
    new([(byte)'P', (byte)'F', (byte)'S', 0x03], Offset: 0, Confidence: 0.95),
    new([(byte)'P', (byte)'F', (byte)'S', (byte)'a'], Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga Professional File System (PFS3/PFS3aio) image — Stage 1 R/W " +
    "(boot block + root + linear dirblock chain + contiguous file extents; anode-as-direct-block " +
    "convention; in-place Add/Remove against the same shape; full anode-table/bitmap emission " +
    "deferred — not yet FS-UAE/WinUAE mountable).";

  /// <summary>
  /// Appends or replaces files inside an existing Stage 1 PFS3 image. Each
  /// <paramref name="inputs"/> entry is removed by name first (so callers
  /// get replace-by-name semantics) and then written through
  /// <see cref="AmigaPfsModifier"/> — touching only the affected dirblock,
  /// any newly chained dirblock, and the file's contiguous data extent.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        AmigaPfsModifier.AddDirectory(archive, input.ArchiveName);
        continue;
      }
      AmigaPfsModifier.AddFile(archive, input.ArchiveName, input.ReadContent());
    }
  }

  /// <summary>
  /// Removes the named entries from an existing Stage 1 PFS3 image. The
  /// dirblock entry bytes and the file's data extent are zeroed; the freed
  /// blocks are not currently re-used by subsequent <see cref="Add"/> calls
  /// (Stage 1 has no free-list bookkeeping — extents grow past the
  /// high-water mark).
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      AmigaPfsModifier.RemoveFile(archive, name, wipeData: true);
  }

  /// <summary>
  /// Creates a fresh PFS3 image from <paramref name="inputs"/>. Directories
  /// surface as PFS dirblock entries with the directory type bit set; nested
  /// paths flatten into the root dirblock for parity with the Stage 1 reader.
  /// Image grows past the conventional 880 KB DD floppy when content requires.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new AmigaPfsWriter();
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddDirectory(input.ArchiveName);
        continue;
      }
      w.AddFile(input.ArchiveName, input.ReadContent());
    }
    var label = options?.GetOption("VolumeLabel", "DISK") ?? "DISK";
    if (string.IsNullOrEmpty(label)) label = "DISK";
    var image = w.Build(label);
    output.Write(image, 0, image.Length);
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new AmigaPfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new AmigaPfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new AmigaPfsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }
}
