#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.SquashFs;

public sealed class SquashFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty {
  public string Id => "SquashFs";
  public string DisplayName => "SquashFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".sqfs";
  public IReadOnlyList<string> Extensions => [".sqfs", ".squashfs", ".snap", ".appimage"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'h', (byte)'s', (byte)'q', (byte)'s'], Confidence: 0.95),
    new([(byte)'s', (byte)'q', (byte)'s', (byte)'h'], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("squashfs", "SquashFS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Linux compressed read-only filesystem";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SquashFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size, -1,
      "squashfs", e.IsDirectory, false, e.ModifiedTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SquashFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new SquashFsWriter(output, leaveOpen: true);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddDirectory(input.ArchiveName.TrimEnd('/'));
      } else {
        var data = File.ReadAllBytes(input.FullPath);
        w.AddFile(input.ArchiveName, data);
      }
    }
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs,
      readEntries: stream => {
        var r = new SquashFsReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory && !e.IsSymlink).Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new SquashFsWriter(ms, leaveOpen: true))
          foreach (var (n, d) in files) w.AddFile(n, d);
        return ms.ToArray();
      });

  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        var r = new SquashFsReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory && !e.IsSymlink).Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new SquashFsWriter(ms, leaveOpen: true))
          foreach (var (n, d) in files) w.AddFile(n, d);
        return ms.ToArray();
      });

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new SquashFsReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory && !e.IsSymlink).Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new SquashFsWriter(ms, leaveOpen: true))
          foreach (var (n, d) in files) w.AddFile(n, d);
        return ms.ToArray();
      });

  /// <summary>
  /// SquashFS is a compressed, read-only image: superblock, compressed data
  /// blocks, fragment table, inode/directory tables and the export/id/lookup
  /// tables are packed back-to-back with no free regions and no cluster tips
  /// (file data is stored at the compressed-block level, so there is no
  /// allocation slack to wipe).
  ///
  /// <para>Note: <see cref="EnumerateExtents"/> reports Used runs at synthetic,
  /// uncompressed-size offsets for the defrag preview — those offsets do
  /// <em>not</em> map to real on-disk positions, so this method deliberately
  /// does not drive the generic wiper from them (doing so would zero live
  /// compressed bytes). Cluster tips are not applicable; this returns 0.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    // Fully packed, read-only image — no free regions or cluster tips exist.
    return 0;
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    yield return new DefragBlockInfo(0, SquashFsConstants.SuperblockSize, DefragBlockKind.MetadataReserved, "superblock");
    var r = new SquashFsReader(image, leaveOpen: true);
    long offset = SquashFsConstants.SuperblockSize;
    foreach (var e in r.Entries) {
      if (e.IsDirectory || e.IsSymlink) continue;
      if (e.Size > 0) {
        yield return new DefragBlockInfo(offset, e.Size, DefragBlockKind.Used, e.FullPath);
        offset += e.Size;
      }
    }
    if (offset < image.Length)
      yield return new DefragBlockInfo(offset, image.Length - offset, DefragBlockKind.MetadataReserved, "metadata-tables");
  }
}
