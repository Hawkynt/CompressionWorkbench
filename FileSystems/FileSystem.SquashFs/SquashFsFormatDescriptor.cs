#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.SquashFs;

public sealed class SquashFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The only writer-honoured knob is the data block size: it is split into the
  /// superblock's <c>block_size</c> / <c>block_log</c> fields and drives how each
  /// file's payload is chunked into compressed data blocks. SquashFS stores no
  /// volume label, and this writer always compresses with gzip (zlib), so no label
  /// or compression-method knob is published.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.PowerOfTwoSize(
      key: "BlockSize", displayName: "Data block size",
      min: 4096, max: 1048576, defaultLabel: "128 KB",
      description: "Compressed data block size. SquashFS allows powers of two from 4 KB to 1 MB; larger blocks compress better but waste more on small files."),
  ];

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

  /// <summary>
  /// Opens a single filesystem entry as a bounded read-only stream. The
  /// reader produces the decoded file bytes by walking the entry's extent
  /// or block chain; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length so cluster/extent slack past the entry's
  /// end is physically unreachable through this view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new SquashFsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.FullPath, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var blockSize = ResolveBlockSize(options);
    using var w = new SquashFsWriter(output, leaveOpen: true, blockSize: blockSize);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddDirectory(input.ArchiveName.TrimEnd('/'));
      } else {
        var data = input.ReadContent();
        w.AddFile(input.ArchiveName, data);
      }
    }
  }

  /// <summary>
  /// Resolves the writer's data block size from the schema. "Auto"/absent keeps
  /// the <see cref="SquashFsWriter.DefaultBlockSize"/>; a pinned power-of-two size
  /// label is parsed back to bytes.
  /// </summary>
  private static uint ResolveBlockSize(FormatCreateOptions? options) {
    var parsed = FilesystemSchemaPresets.ParseSize(options?.GetOption("BlockSize", "Auto"));
    return parsed > 0 ? (uint)parsed : SquashFsWriter.DefaultBlockSize;
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
