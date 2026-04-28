#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.LittleFs;

/// <summary>
/// Read-only descriptor for LittleFS images (Arduino / RTOS / IoT embedded-flash FS).
/// Surfaces the superblock and parsed geometry. Walking the tag-based metadata
/// pair commit log with CRC validation is intentionally out of scope — that's a
/// full reference-implementation port. Detection + structural surfacing is the win.
/// </summary>
public sealed class LittleFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "LittleFs";
  public string DisplayName => "LittleFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".littlefs";
  public IReadOnlyList<string> Extensions => [".littlefs", ".lfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Single canonical-offset registration. The reader's IndexOf scan inside
    // TryParse handles non-canonical placements (revision-byte width varies);
    // we only need ONE magic registration to surface the descriptor for
    // explicit-extension dispatch + the FilesystemCarver. Three duplicated
    // registrations triggered O(N) signature-scan candidate explosion in
    // FilesystemCarverTests.FatInsideRawDump_Detected on a 10 MB host buffer.
    new([0x6C, 0x69, 0x74, 0x74, 0x6C, 0x65, 0x66, 0x73], Offset: 16, Confidence: 0.6),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "LittleFS embedded-flash FS — superblock surface only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.littlefs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    LittleFsSuperblock sb;
    try {
      sb = LittleFsSuperblock.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.littlefs", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.littlefs", image.LongLength, image.LongLength, "stored", false, false, null));
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

    LittleFsSuperblock sb;
    try {
      sb = LittleFsSuperblock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.littlefs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.littlefs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "superblock.bin", sb.RawBytes, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(LittleFsSuperblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"superblock_offset={sb.SuperblockOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_major={sb.VersionMajor}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_minor={sb.VersionMinor}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={sb.BlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_count={sb.BlockCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"total_blocks={sb.BlockCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"name_max={sb.NameMax}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"file_max={sb.FileMax}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"attr_max={sb.AttrMax}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"revision={sb.Revision}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

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
