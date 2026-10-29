#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Cab;
using FileFormat.Wim;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Msu;

/// <summary>
/// Windows Update standalone package (.msu): a Microsoft Cabinet holding the update's own
/// cabinet(s), its applicability XML, a properties text and the WSUS scan cabinet; recent
/// packages may be WIM containers instead. The container is recognised by its magic and the
/// listing is the container's own, read by our Cab or WIM reader.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/troubleshoot/windows-client/installing-updates-features-roles/description-of-windows-update-standalone-installer</c> — the package and its parts</description></item>
/// </list>
/// </summary>
public sealed class MsuFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Msu";
  public string DisplayName => "Windows Update package (MSU)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".msu";
  public IReadOnlyList<string> Extensions => [".msu"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Extension-only: the CAB and WIM descriptors own the container magics.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cab", "Cabinet"), new("wim", "WIM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Windows Update standalone package: the cabinet (or WIM) with the update parts";

  private static IArchiveFormatOperations Container(Stream stream) {
    Span<byte> magic = stackalloc byte[5];
    stream.Position = 0;
    var n = stream.ReadAtLeast(magic, 5, throwOnEndOfStream: false);
    stream.Position = 0;
    if (n >= 4 && magic[..4].SequenceEqual("MSCF"u8))
      return new CabFormatDescriptor();
    if (n >= 5 && magic.SequenceEqual("MSWIM"u8))
      return new WimFormatDescriptor();
    throw new InvalidDataException("Not an MSU package: neither a cabinet (MSCF) nor a WIM (MSWIM).");
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      return Container(stream).List(stream, password);
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or NotSupportedException) {
      var note = Encoding.UTF8.GetBytes($"format=msu\nparse_status=partial\nerror={ex.Message.Replace('\n', ' ')}\n");
      return [new ArchiveEntryInfo(0, "metadata.ini", note.Length, note.Length, "stored", false, false, null, Kind: "metadata")];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    IArchiveFormatOperations container;
    try {
      container = Container(stream);
    } catch (InvalidDataException ex) {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes($"format=msu\nparse_status=partial\nerror={ex.Message}\n"));
      return;
    }
    container.Extract(stream, outputDir, password, files);
  }
}
