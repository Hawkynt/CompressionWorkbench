#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.RomFs;

public sealed class RomFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover {
  public string Id => "RomFs";
  public string DisplayName => "ROMFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".romfs";
  public IReadOnlyList<string> Extensions => [".romfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("-rom1fs-"u8.ToArray(), Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("romfs", "ROMFS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Linux ROM filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new RomFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size,
      "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new RomFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new RomFsWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing RomFs image. Uses
  /// <see cref="RomFsModifier"/> for in-place append. Falls back to rebuild
  /// if the in-place path fails.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    try {
      foreach (var (name, data) in FormatHelpers.FilesOnly(inputs)) {
        RomFsModifier.RemoveFile(archive, name);
        RomFsModifier.AddFile(archive, name, data);
      }
    } catch {
      archive.Position = 0;
      ModifyRebuilder.Add(archive, inputs,
        readEntries: stream => {
          var r = new RomFsReader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
        },
        buildImage: BuildImage);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing RomFs image. ROMFS entries
  /// are inline with headers + data, so unlinking the first entry requires
  /// rebuilding. We use rebuild for Remove to handle all edge cases reliably.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        var r = new RomFsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new RomFsBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new RomFsBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Defragments a RomFs image. Falls back to rebuild since ROMFS entries
  /// are tightly packed with inline data — in-place reordering is complex.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new RomFsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage);

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    using var ms = new MemoryStream();
    using var w = new RomFsWriter(ms, leaveOpen: true);
    foreach (var (n, d) in files) w.AddFile(n, d);
    w.Finish();
    return ms.ToArray();
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    var r = new RomFsReader(image);
    // Superblock: magic (8) + fullSize (4) + checksum (4) + padded volume name
    yield return new DefragBlockInfo(0, 32, DefragBlockKind.MetadataReserved, "superblock");
    // Emit directory entries as Used with trailing "/" — the planner recognises
    // the marker as directory metadata. Without this dir blocks would be
    // counted as Free and could be clobbered by a relocation pass.
    foreach (var e in r.Entries) {
      if (e.Size <= 0) continue;
      var emitName = e.IsDirectory ? e.Name + "/" : e.Name;
      yield return new DefragBlockInfo(e.DataOffset, e.Size, DefragBlockKind.Used, emitName);
    }
  }
}
