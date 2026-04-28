#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ext1;

/// <summary>
/// Descriptor for ext1 filesystem images — the 1992 predecessor of ext2 by Rémy Card.
/// ext1's on-disk superblock layout is identical to the GOOD_OLD-revision ext2 superblock
/// with one crucial difference: the s_magic field at offset 56 of the superblock
/// (file-relative offset 1080) reads <c>0xEF51</c> instead of ext2's <c>0xEF53</c>.
/// ext1 has no journal, no extents, and no FEATURE_INCOMPAT_FILETYPE — directory entries
/// are 8-byte fixed-header (with a 16-bit <c>name_len</c>) + name only. Detection,
/// structural surfacing and round-trip read+write of small WORM images are supported;
/// vintage pre-1993 Linux disk images and forensic tooling for early Linux installs
/// are the consumers.
/// </summary>
public sealed class Ext1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {
  public string Id => "Ext1";
  public string DisplayName => "ext1";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".ext1";
  public IReadOnlyList<string> Extensions => [".ext1"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    // ext1 magic 0xEF51 (LE u16) at file offset 1080 = superblock_offset (1024)
    // + s_magic field offset (56). Same slot as ext2's 0xEF53.
    [new([0x51, 0xEF], Offset: 1080, Confidence: 0.9f)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ext1 (1992) Linux filesystem image — round-trip WORM, no Linux mkfs.";

  // ext1 superblock geometry — identical layout to GOOD_OLD ext2.
  private const int SuperblockOffset = 1024;
  private const int SuperblockMinSize = 264;
  private const ushort Ext1Magic = 0xEF51;

  // --- IArchiveWriteConstraints ---
  // Default writer geometry: 1024-byte blocks × 4096 blocks = 4 MiB image.
  // 12 direct block pointers × 1024 = 12 KiB max per file; 117 user inodes
  // (128 - 11 reserved) bound the file count. The advertised cap reflects the
  // total image size — well under the WORM ceiling but matches what writer
  // produces.
  public long? MaxTotalArchiveSize => 4L * 1024 * 1024;
  public long? MinTotalArchiveSize => 4L * 1024 * 1024;
  public string AcceptedInputsDescription =>
    "ext1 WORM: ≤117 files, each ≤12 KiB (single-group rev-0 layout, direct blocks only).";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) {
      reason = "ext1 writer is flat WORM — directory entries are not preserved.";
      return false;
    }
    try {
      var size = new FileInfo(input.FullPath).Length;
      if (size > 12 * 1024) {
        reason = $"file '{input.ArchiveName}' exceeds the 12 KiB direct-block limit.";
        return false;
      }
    } catch { /* size unknown — accept and let writer decide */ }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.ext1", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    Ext1Superblock sb;
    try {
      sb = Ext1Superblock.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.ext1", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.ext1", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(2, "superblock.bin", sb.RawBytes.LongLength, sb.RawBytes.LongLength, "stored", false, false, null));

    // Surface user files when the image is structurally walkable. This is best-effort
    // — synthetic test images or partial superblocks fall through silently.
    if (sb.Valid) {
      try {
        using var ms = new MemoryStream(image);
        using var reader = new Ext1Reader(ms);
        var idx = entries.Count;
        foreach (var e in reader.Entries) {
          if (e.IsDirectory) continue;
          entries.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "stored", false, false, e.LastModified));
        }
      } catch { /* best-effort; reader failure leaves only surface entries */ }
    }

    return entries;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new Ext1Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    Ext1Superblock sb;
    try {
      sb = Ext1Superblock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.ext1", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.ext1", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "superblock.bin", sb.RawBytes, files);

    // Best-effort: extract user files when the image is walkable.
    if (sb.Valid) {
      try {
        using var ms = new MemoryStream(image);
        using var reader = new Ext1Reader(ms);
        foreach (var e in reader.Entries) {
          if (e.IsDirectory) continue;
          if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
          WriteFile(outputDir, e.Name, reader.Extract(e));
        }
      } catch { /* synthetic / unwalkable image — surface entries are enough */ }
    }
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(Ext1Superblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic=0x{sb.Magic:X4}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"inodes_count={sb.InodesCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"blocks_count={sb.BlocksCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"r_blocks_count={sb.ReservedBlocksCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"free_blocks_count={sb.FreeBlocksCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"free_inodes_count={sb.FreeInodesCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"first_data_block={sb.FirstDataBlock}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"log_block_size={sb.LogBlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={1024U << (int)sb.LogBlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"blocks_per_group={sb.BlocksPerGroup}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"inodes_per_group={sb.InodesPerGroup}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  // Bounded read — superblock parsing only needs the first ~1.3 KiB, but we
  // give ourselves headroom while keeping carver scans capped. For round-trip
  // reads we want the full image — bumped to cover the 4 MiB writer default.
  private const int HeaderReadCap = 8 * 1024 * 1024;

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }

  /// <summary>
  /// Inlined ext1 superblock parser. Layout matches the GOOD_OLD ext2/3/4
  /// header byte-for-byte; only the magic differs (0xEF51 vs 0xEF53). We parse
  /// the fixed-header fields up through s_inodes_per_group at offset 40.
  /// </summary>
  internal sealed class Ext1Superblock {
    public bool Valid { get; init; }
    public ushort Magic { get; init; }
    public uint InodesCount { get; init; }
    public uint BlocksCount { get; init; }
    public uint ReservedBlocksCount { get; init; }
    public uint FreeBlocksCount { get; init; }
    public uint FreeInodesCount { get; init; }
    public uint FirstDataBlock { get; init; }
    public uint LogBlockSize { get; init; }
    public uint BlocksPerGroup { get; init; }
    public uint InodesPerGroup { get; init; }
    public byte[] RawBytes { get; init; } = [];

    public static Ext1Superblock TryParse(ReadOnlySpan<byte> image) {
      if (image.Length < SuperblockOffset + SuperblockMinSize) return new Ext1Superblock();
      var sb = image.Slice(SuperblockOffset);
      var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(56, 2));
      if (magic != Ext1Magic) return new Ext1Superblock();

      // Snapshot the canonical 1024-byte superblock for the surface entry.
      var rawLen = Math.Min(1024, sb.Length);
      var raw = sb.Slice(0, rawLen).ToArray();

      var inodesCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0, 4));
      var blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(4, 4));
      var reserved = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(8, 4));
      var freeBlocks = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(12, 4));
      var freeInodes = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(16, 4));
      var firstData = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(20, 4));
      var logBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(24, 4));
      var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(32, 4));
      var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(40, 4));

      return new Ext1Superblock {
        Valid = true,
        Magic = magic,
        InodesCount = inodesCount,
        BlocksCount = blocksCount,
        ReservedBlocksCount = reserved,
        FreeBlocksCount = freeBlocks,
        FreeInodesCount = freeInodes,
        FirstDataBlock = firstData,
        LogBlockSize = logBlock,
        BlocksPerGroup = blocksPerGroup,
        InodesPerGroup = inodesPerGroup,
        RawBytes = raw,
      };
    }
  }
}
