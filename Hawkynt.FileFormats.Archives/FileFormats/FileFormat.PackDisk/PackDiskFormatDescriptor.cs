#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PackDisk;

/// <summary>
/// Amiga PackDisk archive (magic "DISKPACK"): an 880 KiB floppy packed cylinder by cylinder with XPK.
/// Read-only: <c>disk.adf</c> is the floppy image. PackDisk gives its archives no extension, so
/// detection is by magic.
///
/// Reference: <c>https://github.com/MacPaw/XADMaster</c> — libxad's PackDisk client
/// (<c>libxad/clients/PackDisk.c</c>).
/// </summary>
public sealed class PackDiskFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private const string ImageName = "disk.adf";

  public string Id => "PackDisk";
  public string DisplayName => "PackDisk (Amiga)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".pdsk";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("DISKPACK\0\u0001"u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("xpk", "XPK per cylinder")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga PackDisk floppy disk archive (read-only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new PackDiskReader(stream);
      return [new(0, ImageName, PackDiskReader.Cylinders * PackDiskReader.CylinderSize, stream.CanSeek ? stream.Length : 0,
        "XPK " + string.Join("/", r.Packers), false, false, null)];
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.pdsk", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PackDiskReader(stream);
    if (files is not { Length: > 0 } || MatchesFilter(ImageName, files))
      WriteFile(outputDir, ImageName, r.ExtractDisk());
  }
}
