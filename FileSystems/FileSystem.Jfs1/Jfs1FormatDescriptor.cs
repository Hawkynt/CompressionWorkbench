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
    IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable,
    IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema {

  public string Id => "Jfs1";
  public string DisplayName => "JFS1 (OS/2 original IBM JFS)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
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
    var bs = options.GetOptionInt("BlockSize", 4096);
    if (bs is 1024 or 2048 or 4096) w.SetBlockSize(bs);
    var abs = options.GetOptionInt("AggregateBlockSize", 4096);
    if (abs is 1024 or 2048 or 4096) w.SetAggregateBlockSize(abs);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) => Jfs1ExtentMap.Enumerate(image);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new Jfs1Reader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new Jfs1Writer();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });

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
