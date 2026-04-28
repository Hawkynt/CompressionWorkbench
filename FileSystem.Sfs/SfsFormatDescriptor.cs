#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Sfs;

/// <summary>
/// Read-only descriptor for Amiga Smart Filesystem (SFS) volume images. SFS is
/// the OFS/FFS replacement used by AmigaOS 4 and AROS, with the complete spec
/// at http://www.xs4all.nl/~hjohn/SFS/ (Amiga SFS spec). Surfaces the parsed root block as a
/// structured metadata bundle; walking the B-tree to enumerate files is a
/// follow-up.
/// </summary>
public sealed class SfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Sfs";
  public string DisplayName => "Amiga SFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".sfs";
  public IReadOnlyList<string> Extensions => [".sfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "SFS\0" at offset 0 of the root block — unique enough that 0.95 confidence
    // is honest. We probe offsets 0 / 512 / 1024 in the parser to be lenient on
    // partitioned dumps but the canonical detection point is 0.
    new([0x53, 0x46, 0x53, 0x00], Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga Smart Filesystem — root block surface only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.sfs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    SfsRootBlock root;
    try {
      root = SfsRootBlock.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.sfs", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.sfs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (root.Valid)
      entries.Add(new ArchiveEntryInfo(2, "root_block.bin", root.RawBytes.LongLength, root.RawBytes.LongLength, "stored", false, false, null));
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

    SfsRootBlock root;
    try {
      root = SfsRootBlock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.sfs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.sfs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(root), files);
    if (root.Valid)
      WriteIfMatch(outputDir, "root_block.bin", root.RawBytes, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(SfsRootBlock root) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(root.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"root_block_offset={root.RootBlockOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"checksum=0x{root.Checksum:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"own_block={root.OwnBlock}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version={root.Version}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sequence_number={root.SequenceNumber}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"date_created={root.DateCreated}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"total_blocks={root.TotalBlocks}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={root.BlockSize}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  // Bounded — SFS root block is at offset 0 with magic "SFS\0"; we only need the
  // first few KB for header surfacing.
  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
