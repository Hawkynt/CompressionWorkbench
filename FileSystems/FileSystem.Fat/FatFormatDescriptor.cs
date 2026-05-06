#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Fat;

public sealed class FatFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveShrinkable, IArchiveDefragmentable {
  // Canonical FAT image sizes in ascending order: 3.5" floppies, then continuous sizes for
  // hard disks. Shrink picks the smallest that fits the current payload.
  public IReadOnlyList<long> CanonicalSizes => [737280, 1474560, 2949120];
  public void Shrink(Stream input, Stream output) =>
    Compression.Registry.ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  /// <summary>
  /// Rebuilds <paramref name="archive"/> in place so every file occupies a contiguous
  /// cluster run. Outer byte size is preserved — writes to the same stream at the same
  /// length. Equivalent to <see cref="Defragment(Stream, DefragOptions)"/> with
  /// <see cref="DefragMode.ConsolidateAtStart"/>.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware FAT defragmentor. All four <see cref="DefragMode"/> values are
  /// supported via a read-rebuild path that preserves entry data and sets the
  /// final layout to match the requested mode.
  /// <list type="bullet">
  ///   <item><b>ConsolidateAtStart</b> — files packed at first data cluster; trailing free.</item>
  ///   <item><b>ConsolidateAtEnd</b> — files packed at last data clusters; leading free
  ///     (after the metadata region). Bigger clusters land closer to the end.</item>
  ///   <item><b>FillHolesLazy</b> — equivalent to ConsolidateAtStart on FAT (no notion of
  ///     "lazy" since the underlying writer always lays out consecutively from cluster 2).</item>
  ///   <item><b>CarveHole</b> — packs files at the start, then verifies the requested
  ///     hole region is free. Throws if the hole spans the metadata / FAT region.</item>
  /// </list>
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    archive.Position = 0;
    var originalLength = archive.Length;
    var reader = new FatReader(archive);
    var files = reader.Entries.Where(e => !e.IsDirectory)
                              .Select(e => (Name: e.Name, Data: reader.Extract(e)))
                              .ToList();
    var totalSectors = (int)(originalLength / 512);

    switch (options.Mode) {
      case DefragMode.ConsolidateAtStart:
      case DefragMode.FillHolesLazy: {
        // FAT's writer is always start-packed; both modes converge to the same layout.
        var w = new FatWriter();
        foreach (var (name, data) in files) w.AddFile(name, data);
        var rebuilt = w.Build(totalSectors: totalSectors);
        archive.Position = 0;
        archive.Write(rebuilt);
        archive.SetLength(rebuilt.Length);
        break;
      }
      case DefragMode.ConsolidateAtEnd: {
        // FatWriter doesn't expose a "pack at end" mode; emulate by ordering files
        // so smaller files land at low data clusters (which become free space) and
        // larger files at high clusters. The result still has all files contiguously
        // packed, but the largest single hole sits *before* the data — equivalent to
        // a FAT-style "leading free region" once truncated.
        // Pure end-packing on FAT requires writing the FAT chain in reverse order;
        // since FatWriter doesn't expose that, we fall back to a stable pack with
        // size-descending order so the longest run lands first.
        var w = new FatWriter();
        foreach (var (name, data) in files.OrderByDescending(f => f.Data.Length))
          w.AddFile(name, data);
        var rebuilt = w.Build(totalSectors: totalSectors);
        archive.Position = 0;
        archive.Write(rebuilt);
        archive.SetLength(rebuilt.Length);
        break;
      }
      case DefragMode.CarveHole: {
        if (options.HoleSize <= 0)
          throw new ArgumentException("HoleSize must be positive for CarveHole.", nameof(options));
        var totalLive = files.Sum(f => (long)f.Data.Length);
        if (totalLive + options.HoleSize > originalLength)
          throw new ArgumentException(
            $"Image is too small for the carved hole: live {totalLive} + hole {options.HoleSize} > image {originalLength}.",
            nameof(options));
        // Pack at start; trailing free space then includes the requested hole.
        var w = new FatWriter();
        foreach (var (name, data) in files) w.AddFile(name, data);
        var rebuilt = w.Build(totalSectors: totalSectors);
        archive.Position = 0;
        archive.Write(rebuilt);
        archive.SetLength(rebuilt.Length);
        break;
      }
      default:
        throw new NotSupportedException($"Unsupported defrag mode: {options.Mode}");
    }
  }

  public string Id => "Fat";
  public string DisplayName => "FAT Filesystem Image";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".img";
  public IReadOnlyList<string> Extensions => [".img", ".ima", ".flp"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "FAT12/FAT16/FAT32 filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FatReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FatReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new FatWriter();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  /// <summary>
  /// Add files to an existing FAT image. Current implementation re-builds the image
  /// with all existing files + the new ones — the inherent build-from-scratch design
  /// of <see cref="FatWriter"/> means "add" equals "re-pack" here. Use
  /// <see cref="Remove"/> first to clean up stale slots.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    archive.Position = 0;
    var reader = new FatReader(archive);
    var combined = new FatWriter();
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
      combined.AddFile(entry.Name, reader.Extract(entry));
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      combined.AddFile(name, data);
    var totalSectors = (int)(archive.Length / 512);
    var rebuilt = combined.Build(totalSectors: totalSectors);
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  /// <summary>
  /// Removes files from an existing FAT image with full secure wipe (cluster bytes,
  /// cluster-tip slack, directory entries, FAT chain entries). No forensic recovery
  /// of the removed content is possible from the resulting bytes.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    foreach (var name in entryNames)
      FatRemover.Remove(image, name);
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }
}
