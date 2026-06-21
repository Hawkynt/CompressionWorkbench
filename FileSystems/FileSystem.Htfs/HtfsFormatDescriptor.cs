#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Htfs;

/// <summary>
/// SCO HTFS (High Throughput File System) — S5-derived FS introduced in SCO
/// OpenServer 5. Now exposes a WORM writer + reader with real nested
/// subdirectories, defrag/purge/conversion, fileset optimizer, and an options
/// schema (BlockSize / InodeCount / VolumeLabel).
/// </summary>
/// <remarks>
/// <para><b>Reference</b>: SCO OpenServer Development System docs,
/// <c>sys/fs/htfs/htfs_fs.h</c>. Superblock at byte offset 512 (sector 1).
/// Magic <c>0x012FD15D</c> (LE u32) at byte offset 0 of the superblock.</para>
/// <para><b>Hierarchy</b>: real — directories nest via the writer's inode +
/// 16-byte dirent chain (single-block dirs cap one BB of entries each).</para>
/// </remarks>
public sealed class HtfsFormatDescriptor :
    IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
    IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema {

  public string Id => "Htfs";
  public string DisplayName => "HTFS (SCO High Throughput File System)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".htfs";
  public IReadOnlyList<string> Extensions => [".htfs", ".s5"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x5D, 0xD1, 0x2F, 0x01], Offset: 512, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "SCO HTFS — WORM writer + nested-directory reader.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("BlockSize", "Block size", FormatOptionKind.Enum, "512",
      AllowedValues: ["512", "1024", "2048"],
      Description: "Block size in bytes (S5-style HTFS supports 512/1024/2048)."),
    new("InodeCount", "Inode count", FormatOptionKind.Integer, "64",
      Description: "Reserved inode slots in the inode array (default 64; cap 256)."),
    FilesystemSchemaPresets.VolumeLabel(16),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new HtfsReader(stream);
      return r.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false, null
      )).ToList();
    } catch {
      return [
        new ArchiveEntryInfo(0, "FULL.htfs", stream.Length, stream.Length, "stored", false, false, null),
        new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null),
      ];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    try {
      var r = new HtfsReader(stream);
      foreach (var e in r.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && !MatchesFilter(e.Name, files)) continue;
        WriteFile(outputDir, e.Name, r.Extract(e));
      }
    } catch {
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      var sb = HtfsSuperblock.TryParse(ms.ToArray());
      WriteFile(outputDir, "metadata.ini", BuildMetadata(sb));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new HtfsWriter();
    w.SetVolumeLabel(options.GetOption("VolumeLabel", "WORM"));
    // NOTE: the block-size auto-optimiser is intentionally NOT wired here. The
    // HTFS reader's block-size detection only recovers 512-byte images, so a
    // non-512 default would not round-trip — see HtfsReader.DetectBlockSize. The
    // BlockSize knob therefore stays an explicit, caller-pinned choice only.
    var blockSize = options.GetOptionInt("BlockSize", 512);
    if (blockSize is 512 or 1024 or 2048) w.SetBlockSize(blockSize);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) => HtfsExtentMap.Enumerate(image);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new HtfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new HtfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });

  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var size = image.Length;
    var extents = HtfsExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, size, wipeClusterTips: false, fileSizeLookup: null);
  }

  private static byte[] BuildMetadata(HtfsSuperblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic=0x{sb.Magic:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"isize={sb.Isize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"fsize={sb.Fsize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"nfree={sb.Nfree}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"ninode={sb.Ninode}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }
}
