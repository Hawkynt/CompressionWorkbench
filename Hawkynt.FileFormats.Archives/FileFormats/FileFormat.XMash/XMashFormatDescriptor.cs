#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.XMash;

/// <summary>
/// Amiga X-Mash disk archive (".xms", magic "MSH"): an 880 KiB floppy packed track by track with XPK.
/// Read-only: <c>disk.adf</c> is the floppy image; <c>banner.txt</c> and <c>file_id.diz</c> are the
/// attached texts when present. ".xms" is shared with unrelated formats, so detection is by magic.
///
/// References:
/// <list type="bullet">
///   <item><description>X-Mash 1.2 (Hydra/LSD, Aminet <c>util/pack/lsdxms12.lha</c>) — <c>File_Format.DOC</c></description></item>
///   <item><description><c>https://github.com/MacPaw/XADMaster</c> — libxad's xMash client (<c>libxad/clients/xMash.c</c>)</description></item>
/// </list>
/// </summary>
public sealed class XMashFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private const string ImageName = "disk.adf";
  private const string BannerName = "banner.txt";
  private const string DescriptionName = "file_id.diz";

  public string Id => "XMash";
  public string DisplayName => "X-Mash (Amiga)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".xms";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MSHD"u8.ToArray(), Confidence: 0.80),
    new("MSHB"u8.ToArray(), Confidence: 0.80),
    new("MSHF"u8.ToArray(), Confidence: 0.80),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("xpk", "XPK per track group")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga X-Mash floppy disk archive (read-only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new XMashReader(stream);
      var entries = new List<ArchiveEntryInfo> {
        new(0, ImageName, XMashReader.Tracks * XMashReader.TrackSize, stream.CanSeek ? stream.Length : 0,
          "XPK " + string.Join("/", r.Packers), false, false, null),
      };
      if (Join(r.Banners) is { } banner)
        entries.Add(new(entries.Count, BannerName, banner.Length, banner.Length, "XPK", false, false, null));
      if (Join(r.Descriptions) is { } description)
        entries.Add(new(entries.Count, DescriptionName, description.Length, description.Length, "XPK", false, false, null));
      return entries;
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.xms", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new XMashReader(stream);
    if (files is not { Length: > 0 } || MatchesFilter(ImageName, files))
      WriteFile(outputDir, ImageName, r.ExtractDisk());
    if (Join(r.Banners) is { } banner && (files is not { Length: > 0 } || MatchesFilter(BannerName, files)))
      WriteFile(outputDir, BannerName, banner);
    if (Join(r.Descriptions) is { } description && (files is not { Length: > 0 } || MatchesFilter(DescriptionName, files)))
      WriteFile(outputDir, DescriptionName, description);
  }

  // Several texts of one kind (an archive appended to) are concatenated in file order.
  private static byte[]? Join(IReadOnlyList<byte[]> texts) => texts.Count == 0 ? null : texts.SelectMany(t => t).ToArray();
}
