#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Jfs1;

/// <summary>
/// OS/2 original IBM JFS1 format descriptor — distinct from
/// <c>FileSystem.Jfs</c> which targets the Linux JFS2 derivative. WORM
/// writer + reader with real nested subdirectories, defrag/purge/conversion,
/// fileset optimizer, and an options schema (BlockSize / AggregateBlockSize /
/// VolumeLabel).
///
/// References:
/// <list type="bullet">
///   <item><description>IBM "JFS for OS/2 Warp Server for e-business" documentation (1999-2000) — the original vendor documentation of the pre-Linux JFS1 (no stable public URL)</description></item>
///   <item><description><c>https://jfs.sourceforge.net/</c> — the open-sourced JFS project, useful for contrasting the later JFS2-derived layout</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/JFS_(file_system)</c> — Wikipedia overview of the JFS family</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Reference</b>: IBM JFS for OS/2 Warp Server documentation
/// (1999-2000), pre-Linux-port. Magic <c>"JFS1"</c> ASCII at byte offset 0
/// with <c>s_version = 1</c>. The descriptor refuses any image where
/// <c>s_version &gt;= 2</c> so it cannot steal Linux-JFS detection.</para>
/// <para><b>Hierarchy</b>: real — directories nest via writer-emitted dirent
/// chains (4-byte LE inode + 1-byte nlen + name) anchored from inode 2.</para>
/// </remarks>
public sealed class Jfs1FormatDescriptor :
    IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
    IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  public string Id => "Jfs1";
  public string DisplayName => "JFS1 (OS/2 original IBM JFS)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".jfs1";
  public IReadOnlyList<string> Extensions => [".jfs1"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("JFS1"u8.ToArray(), Offset: 0, Confidence: 0.70),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "IBM JFS1 (OS/2 original) — WORM writer + nested-directory reader.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("BlockSize", "Block size", FormatOptionKind.Enum, "4096",
      AllowedValues: ["1024", "2048", "4096"],
      Description: "JFS1 block size in bytes (IBM OS/2 spec allows 1024/2048/4096)."),
    new("AggregateBlockSize", "Aggregate block size", FormatOptionKind.Enum, "4096",
      AllowedValues: ["1024", "2048", "4096"],
      Description: "Aggregate block size for the dmap chain (usually equals BlockSize)."),
    FilesystemSchemaPresets.VolumeLabel(16),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new Jfs1Reader(stream);
      return r.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false, null)).ToList();
    } catch {
      return [
        new ArchiveEntryInfo(0, "FULL.jfs1", stream.Length, stream.Length, "stored", false, false, null),
        new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null),
      ];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    try {
      var r = new Jfs1Reader(stream);
      foreach (var e in r.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && !MatchesFilter(e.Name, files)) continue;
        WriteFile(outputDir, e.Name, r.Extract(e));
      }
    } catch {
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      var sb = Jfs1Superblock.TryParse(ms.ToArray());
      WriteFile(outputDir, "metadata.ini", BuildMetadata(sb));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new Jfs1Writer();
    w.SetVolumeLabel(options.GetOption("VolumeLabel", "WORM"));

    var fileSizes = new List<long>();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the volume out; reading a large input
      // into a byte[] would cap the volume at what an array can hold.
      if (info.InMemoryContent is { } bytes) {
        w.AddFile(info.ArchiveName, bytes);
        fileSizes.Add(bytes.LongLength);
      } else {
        var size = new FileInfo(info.FullPath).Length;
        w.AddStreamingFile(info.ArchiveName, size, () => File.OpenRead(info.FullPath));
        fileSizes.Add(size);
      }
    }

    // Block size: a pinned value wins verbatim; when unset, the shared layout
    // optimiser picks the legal 1024/2048/4096 size that minimises slack +
    // metadata overhead instead of defaulting to 4096. Routed through the shared
    // adapter (rather than the bespoke Jfs1Optimizer) to prove consolidation; the
    // aggregate block size tracks the data block size.
    var bs = options.HasOption("BlockSize")
      ? options.GetOptionInt("BlockSize", 4096)
      : Compression.Core.Layout.LayoutOptimizerAdapter.SelectAllocationUnit(
          [1024, 2048, 4096],
          fileSizes,
          fixedOverhead: blk => 3L * blk); // sb + inode block + root dir
    if (bs is 1024 or 2048 or 4096) w.SetBlockSize(bs);
    var abs = options.HasOption("AggregateBlockSize")
      ? options.GetOptionInt("AggregateBlockSize", 4096)
      : bs;
    if (abs is 1024 or 2048 or 4096) w.SetAggregateBlockSize(abs);

    w.WriteTo(output);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing JFS1 image via
  /// <see cref="Jfs1InPlaceModifier"/> — TRUE in-place O(touched bytes) I/O
  /// (claim a free dinode slot, append a contiguous data extent at EOF, write
  /// the root dirent). Falls back to a whole-image rebuild only for nested paths
  /// or inode-table exhaustion.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    try {
      foreach (var (name, data) in FilesOnly(inputs)) {
        Jfs1InPlaceModifier.RemoveFile(archive, name, wipeData: true);
        Jfs1InPlaceModifier.AddFile(archive, name, data);
      }
    } catch (IOException) {
      archive.Position = 0;
      ModifyRebuilder.Add(archive, inputs,
        readEntries: stream => {
          var r = new Jfs1Reader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
        },
        buildImage: BuildImage, largeVolumeCreator: this);
    }
  }

  /// <summary>Removes the named entries in-place via <see cref="Jfs1InPlaceModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var leftover = new List<string>();
    foreach (var name in entryNames) {
      var leaf = name.Replace('\\', '/').TrimStart('/');
      if (leaf.Contains('/') || !Jfs1InPlaceModifier.RemoveFile(archive, leaf, wipeData: true))
        leftover.Add(name);
    }
    if (leftover.Count == 0) return;
    archive.Position = 0;
    ModifyRebuilder.Remove(archive, leftover.ToArray(),
      readEntries: stream => {
        var r = new Jfs1Reader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage, largeVolumeCreator: this);
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new Jfs1Writer();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) => Jfs1ExtentMap.Enumerate(image);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // A volume too large to materialise goes through the streaming rebuilder;
    // the buffered path's buildImage returns a byte[] of the whole image, which
    // the writer refuses to produce once it passes the array limit.
    // Every mode streams above the cap: end-pack and carve-hole order their
    // entries from scratch inside the rebuilder, so none of them falls back
    // to a buffered rebuild the volume is too large for.
    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      Jfs1Writer? streamWriter = null;
      Stream? target = null;
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => {
          var r = new Jfs1Reader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
        },
        beginWrite: s2 => { streamWriter = new Jfs1Writer(); target = s2; },
        // As a stream factory, not inline: an inline payload is materialised
        // inside the image buffer, which is what a large volume cannot afford.
        writeEntry: (name, data) => streamWriter!.AddStreamingFile(
          name, data.LongLength, () => new MemoryStream(data, writable: false)),
        finishWrite: () => streamWriter!.WriteTo(target!));
      return;
    }

    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new Jfs1Reader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new Jfs1Writer();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  /// <summary>Largest volume a defrag will rebuild through a byte[].</summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;


  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var size = image.Length;
    var extents = Jfs1ExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, size, wipeClusterTips: false, fileSizeLookup: null);
  }

  private static byte[] BuildMetadata(Jfs1Superblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic={sb.MagicString}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"s_version={sb.Version}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"s_size={sb.Size}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"s_bsize={sb.BlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"s_l2bsize={sb.Log2BlockSize}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }
}
