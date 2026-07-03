#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vib;

/// <summary>
/// VMware vSphere Installation Bundle (<c>.vib</c>) — a Unix <c>ar</c> archive of
/// <c>descriptor.xml</c>, <c>sig.pkcs7</c> and a compressed payload (usually a
/// <c>.vgz</c> = gzip'd tar, sometimes xz'd or a bare tar). Listing/extraction
/// delegate to the <c>ar</c> reader, surface <c>descriptor.xml</c> + the raw
/// signature, and unpack the decompressed payload tree under <c>payload/</c>.
/// Read-only: re-signing a VIB requires VMware's private keys, so creation is not
/// offered.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://blogs.vmware.com/vsphere/2011/09/whats-in-a-vib.html</c> — VMware's "What's in a VIB?" (file archive + XML descriptor + signature)</description></item>
///   <item><description>VMware vSphere / ESXi documentation on VIBs, acceptance levels and <c>esxcli software vib</c> — the vendor reference for producers and consumers</description></item>
/// </list>
/// </summary>
public sealed class VibFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Vib";
  public string DisplayName => "vSphere Installation Bundle";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".vib";
  public IReadOnlyList<string> Extensions => [".vib"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // VIB shares the ar global magic "!<arch>\n"; extension + descriptor.xml member
  // distinguish it. Lower confidence than plain ar so ar/deb still win by extension.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("vib", "VIB")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "VMware vSphere Installation Bundle (ar + descriptor.xml + sig.pkcs7 + vgz payload)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new VibReader(stream);
    var entries = new List<ArchiveEntryInfo>();
    var idx = 0;

    if (r.DescriptorXml is { } xml)
      entries.Add(new ArchiveEntryInfo(idx++, VibConstants.DescriptorName, xml.Length, xml.Length,
        "stored", false, false, null, Kind: "Tag"));

    if (r.Signature is { } sig)
      entries.Add(new ArchiveEntryInfo(idx++, VibConstants.SignatureName, sig.Length, sig.Length,
        "stored", false, false, null, Kind: "Tag"));

    foreach (var e in r.ReadPayloadEntries())
      entries.Add(new ArchiveEntryInfo(idx++, PayloadPath(e.Path), e.Data.Length, e.Data.Length,
        "vgz", e.IsDirectory, false, null));

    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new VibReader(stream);

    if (r.DescriptorXml is { } xml && Wants(files, VibConstants.DescriptorName))
      WriteFile(outputDir, VibConstants.DescriptorName, xml);

    if (r.Signature is { } sig && Wants(files, VibConstants.SignatureName))
      WriteFile(outputDir, VibConstants.SignatureName, sig);

    foreach (var e in r.ReadPayloadEntries()) {
      if (e.IsDirectory)
        continue;
      var path = PayloadPath(e.Path);
      if (Wants(files, path))
        WriteFile(outputDir, path, e.Data);
    }
  }

  private static string PayloadPath(string tarPath) {
    var normalized = tarPath.Replace('\\', '/').TrimStart('.', '/');
    return "payload/" + normalized;
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);
}
