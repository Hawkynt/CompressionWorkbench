#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Adfs;

/// <summary>
/// Descriptor for Acorn Advanced Disc Filing System (ADFS) images. Read works
/// for both old-map (S/M/L, 256-byte sectors) and new-map (D/E/F, 1024-byte
/// sectors). Create (WORM) emits ADFS-L (640 KiB, old-map) only.
/// Detected by the "Hugo" or "Nick" directory marker at sector 2 — root dir
/// magic at file offset 0x200 (old map) or 0x400 (new map).
/// </summary>
public sealed class AdfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Adfs";
  public string DisplayName => "Acorn ADFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".adl";
  public IReadOnlyList<string> Extensions => [".adl", ".adf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "Hugo" at 0x200 (old map S/M/L) — confidence kept moderate because
    // .adf collides with Amiga ADF (which begins with "DOS" at offset 0).
    new([(byte)'H', (byte)'u', (byte)'g', (byte)'o'], Offset: 0x200, Confidence: 0.75),
    new([(byte)'N', (byte)'i', (byte)'c', (byte)'k'], Offset: 0x200, Confidence: 0.75),
    // New map (D/E/F): root dir at 0x400.
    new([(byte)'H', (byte)'u', (byte)'g', (byte)'o'], Offset: 0x400, Confidence: 0.70),
    new([(byte)'N', (byte)'i', (byte)'c', (byte)'k'], Offset: 0x400, Confidence: 0.70),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Acorn ADFS (BBC Micro / Archimedes / RISC OS) filesystem — read + R/W (ADFS-L variant; in-place Add/Remove against the old-map FSM and Hugo-bracketed root directory).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new AdfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new AdfsReader(stream);
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
    var r = new AdfsReader(archive);
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

  // ── IArchiveCreatable (WORM) ─────────────────────────────────────────────

  /// <summary>
  /// Emits a fresh ADFS-L disc image (640 KiB, old-map, 256-byte sectors)
  /// containing the supplied inputs at the root directory. Capacity is
  /// validated up-front against the 2 553 usable data sectors (total 2 560
  /// minus 2 for the FSM and 5 for the root directory).
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var writer = new AdfsWriter();
    var title = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(title)) writer.DiscTitle = title;

    foreach (var (name, data) in FlatFiles(inputs))
      writer.AddFile(name, data);

    output.Write(writer.Build());
  }

  // ── IArchiveModifiable (R/W) ────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) files inside an existing ADFS-L image. Uses
  /// <see cref="AdfsModifier"/> for in-place mutation against the old-map
  /// FSM and Hugo-bracketed root directory — only the FSM sectors, the root
  /// directory, and the file's freshly-allocated data sectors are touched.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FlatFiles(inputs)) {
      AdfsModifier.RemoveFile(archive, name);
      AdfsModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing ADFS-L image. Each entry's
  /// data sectors are wiped and returned to the FSM with adjacent-region
  /// merging, and the root directory's entry slot is compacted so the
  /// trailing zero sentinel re-engages.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      AdfsModifier.RemoveFile(archive, name);
  }
}
