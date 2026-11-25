#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dcs;

/// <summary>
/// Amiga DCS (Device Compression System, ParCon Software) archive (".dcs"): an 880 KiB floppy packed
/// cylinder by cylinder with XPK. Read-only: <c>disk.adf</c> is the floppy image.
///
/// References:
/// <list type="bullet">
///   <item><description><c>docs/DCS-ON-DISK.md</c> — layout measured on DCS 0.10 output</description></item>
///   <item><description><c>https://github.com/MacPaw/XADMaster</c> — libxad's DCS client (<c>libxad/clients/DCS.c</c>)</description></item>
/// </list>
/// </summary>
public sealed class DcsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private const string ImageName = "disk.adf";

  public string Id => "Dcs";
  public string DisplayName => "DCS (Amiga Disk Archiver)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".dcs";
  public IReadOnlyList<string> Extensions => [".dcs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("DCS\0\0\0TR"u8.ToArray(), Confidence: 0.90, Mask: [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0xFF]),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("xpk", "XPK per cylinder")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga DCS floppy disk archive (read-only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new DcsReader(stream);
      return [new(0, ImageName, DcsReader.Cylinders * DcsReader.CylinderSize, stream.CanSeek ? stream.Length : 0,
        "XPK " + string.Join("/", r.Packers), false, r.EncryptionPasses != 0, null)];
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.dcs", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DcsReader(stream);
    if (files is not { Length: > 0 } || MatchesFilter(ImageName, files))
      WriteFile(outputDir, ImageName, r.ExtractDisk());
  }
}
