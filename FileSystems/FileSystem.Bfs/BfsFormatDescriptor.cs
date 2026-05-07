#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Bfs;

/// <summary>
/// Read-only descriptor for BeOS / Haiku BFS filesystem images. Surfaces the
/// superblock as a structured metadata bundle. Walking the root directory B+tree
/// to enumerate files is explicitly out of scope — we emit the raw superblock
/// bytes and the whole disk for downstream tools.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why R-only.</b> A complete BeFS R/W stack is multi-week work. The format
/// is built on several non-trivial structures that all interlock:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>Allocation groups + block bitmap chain.</b> Free-space tracking is split
///   across N allocation groups, each with its own bitmap. Allocations must
///   honor AG locality and the on-disk <c>used_blocks</c> counter in the
///   superblock.
///   </description></item>
///   <item><description>
///   <b>Inodes (typically 2 KiB) with small_data / file_cookie areas.</b> Each
///   inode embeds attribute key/value pairs inline; large attributes spill to
///   <c>data_stream</c> blocks. The reader/writer must round-trip both layouts.
///   </description></item>
///   <item><description>
///   <b>data_stream with direct + indirect + double-indirect extent lists.</b>
///   File data is referenced through 12 direct extents, an indirect extent, and
///   a double-indirect extent — each extent is a <c>(allocation_group,
///   start, length)</c> triple. Truncate/grow paths must rebalance these.
///   </description></item>
///   <item><description>
///   <b>Directory contents stored as a B+ tree.</b> Each directory is an inode
///   whose data stream is a real B+ tree (separate header + interior nodes +
///   leaf duplicates). Insert/delete must split/merge nodes and rewrite parent
///   chains. There is also a per-volume index B+ tree (<c>indices_dir_ino</c>)
///   used for live queries that must stay in sync.
///   </description></item>
///   <item><description>
///   <b>Journal.</b> BeFS is journaled — any structural change must be wrapped
///   in a transaction header in the on-disk log area, otherwise <c>fs_check</c>
///   from Haiku will mark the volume dirty.
///   </description></item>
/// </list>
/// <para>
/// Per the project rule "never advertise <c>CanCreate</c> without real spec
/// compliance" we keep BFS at <see cref="FormatCapabilities.CanList"/> +
/// <see cref="FormatCapabilities.CanExtract"/> + <see cref="FormatCapabilities.CanTest"/>
/// only. A guard test (<c>BfsTests.Descriptor_IsHonestlyReadOnly</c>) blocks
/// drive-by capability upgrades that would not be validated by Haiku's
/// <c>fs_check</c>.
/// </para>
/// </remarks>
public sealed class BfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Bfs";
  public string DisplayName => "BFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".bfs";
  public IReadOnlyList<string> Extensions => [".bfs", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // '1SFB' at offset 544 (offset 32 into the superblock at sector 1)
    new([0x31, 0x53, 0x46, 0x42], Offset: 544, Confidence: 0.35),
    // '1SFB' at offset 32 (no-MBR rewrap)
    new([0x31, 0x53, 0x46, 0x42], Offset: 32, Confidence: 0.30),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "BeOS / Haiku filesystem image — superblock surface only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.bfs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var sb = BfsSuperblock.TryParse(image);
    entries.Add(new ArchiveEntryInfo(0, "FULL.bfs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(2, "superblock.bin", sb.RawBytes.LongLength, sb.RawBytes.LongLength, "stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    BfsSuperblock sb;
    try {
      sb = BfsSuperblock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.bfs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.bfs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "superblock.bin", sb.RawBytes, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(BfsSuperblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"superblock_offset={sb.SuperblockOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volume_name={sb.Name}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={sb.BlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"num_blocks={sb.NumBlocks}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"used_blocks={sb.UsedBlocks}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"num_ags={sb.NumAgs}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"root_dir_ino={sb.RootDirIno}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"indices_dir_ino={sb.IndicesDirIno}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic1_ok={(sb.Magic1Value == BfsSuperblock.Magic1)}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic2_ok={(sb.Magic2Value == BfsSuperblock.Magic2)}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic3_ok={(sb.Magic3Value == BfsSuperblock.Magic3)}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
