#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Coherent;

/// <summary>
/// Read-only descriptor for Mark Williams Coherent OS file system. Magic
/// 0xFD18 at file offset 1024+504. Distinguished from Xenix by extension /
/// 32-bit magic absence — Coherent uses a 16-bit magic only.
/// </summary>
public sealed class CoherentFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Coherent";
  public string DisplayName => "Coherent FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".coh";
  public IReadOnlyList<string> Extensions => [".coh", ".coherent"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0xFD18 at file offset 1024+504 = 1528 — confidence kept low because
    // Xenix and other s5-derivatives share the same magic; extension
    // disambiguates.
    new([0x18, 0xFD], Offset: 1528, Confidence: 0.55),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Mark Williams Coherent OS filesystem image — true in-place R/W via V7-style inode + zone mutation. Add scans the inode table for free slots and the data area for unreferenced zones (direct + single-indirect + double-indirect tiers, grows past s_fsize when exhausted). Replace rewrites payload bytes at the same on-disk block offsets when the new size fits the inode's existing zones. Remove zeroes data + indirect pointer blocks + dirent + inode slot. Subdirectory mutation deferred (root-level only).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CoherentReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CoherentReader(stream);
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
    var r = new CoherentReader(archive);
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

  /// <summary>
  /// WORM emission: builds a fresh Coherent filesystem image from the
  /// supplied inputs. Directories are flattened (Coherent dirents only
  /// support a single-component 14-byte name) and the resulting image
  /// self-round-trips via <see cref="CoherentReader"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var writer = new CoherentWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs))
      writer.AddFile(name, data);
    writer.Finish();
  }

  /// <summary>
  /// Adds (or replaces by leaf name) files inside an existing Coherent image
  /// via true in-place V7-style inode + zone mutation. Routes through
  /// <see cref="CoherentInPlaceModifier"/> — no rebuild fall-back: if the
  /// inode table is exhausted (the WORM writer sizes it tight to the
  /// originally-committed files) the operation surfaces <see cref="IOException"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    CoherentInPlaceModifier.Add(archive, inputs);
  }

  /// <summary>
  /// Removes the named entries from an existing Coherent image. Wipes all
  /// data zones AND indirect pointer blocks, then clears the inode slot and
  /// the dirent — no forensic recovery of the removed content is possible.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      CoherentInPlaceModifier.Remove(archive, name);
  }
}
