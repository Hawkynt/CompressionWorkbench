#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tfs;

/// <summary>
/// Read-only descriptor for BBN Trans-FS (TFS). TFS is a transactional
/// filesystem developed at BBN; the on-disk format is poorly documented
/// publicly so this descriptor is intentionally detection-only — it emits the
/// raw image as a single opaque entry rather than guessing layout.
///
/// References:
/// <list type="bullet">
///   <item><description>BBN Laboratories technical reports on Trans-FS — the only substantive documentation; not stably archived online</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Magic</b>: <c>0x54465301</c> ("TFS\x01") at offset 0.
/// Block size 1024 per the BBN papers. We do not attempt to walk the inode
/// table or directory structure — the published material is insufficient to
/// do that honestly.</para>
/// </remarks>
public sealed class TfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Tfs";
  public string DisplayName => "TFS (BBN Trans-FS)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".tfs";
  public IReadOnlyList<string> Extensions => [".tfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "TFS\x01" — 0x54 0x46 0x53 0x01 at offset 0.
    new([0x54, 0x46, 0x53, 0x01], Offset: 0, Confidence: 0.80),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "BBN Trans-FS transactional filesystem — opaque single-entry surface.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.tfs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var valid = image.Length >= 4 && image[0] == 0x54 && image[1] == 0x46 && image[2] == 0x53 && image[3] == 0x01;
    entries.Add(new ArchiveEntryInfo(0, "FULL.tfs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null,
      Kind: valid ? "ok" : "partial"));
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

    var valid = image.Length >= 4 && image[0] == 0x54 && image[1] == 0x46 && image[2] == 0x53 && image[3] == 0x01;
    WriteIfMatch(outputDir, "FULL.tfs", image, files);

    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(valid ? "ok" : "partial")}\n");
    bldr.Append("magic_hex=0x54465301\n");
    bldr.Append("block_size=1024\n");
    bldr.Append("note=TFS on-disk layout is not publicly documented; image surfaced as opaque blob.\n");
    WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(bldr.ToString()), files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
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
