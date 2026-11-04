#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.WixBurn;

/// <summary>
/// WiX Burn bundle (the setup.exe WiX's light.exe / wix build makes from a &lt;Bundle&gt;): the
/// bootstrapper application, the Burn manifest and the attached packages with their payloads.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://wixtoolset.org/docs/tools/burn/</c> — Burn</description></item>
///   <item><description><c>docs/WIX-BURN-ON-DISK.md</c> — the layout as measured on bundles light.exe built</description></item>
/// </list>
/// </summary>
public sealed class WixBurnFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "WixBurn";
  public string DisplayName => "WiX Burn bundle";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".exe";
  // Detected by its ".wixburn" PE section (FormatDetector.DetectInstaller), never by ".exe".
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cab", "Cabinet")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "WiX Burn bundle: bootstrapper application, manifest and attached packages, hash-checked";

  /// <summary>True when the PE carries a ".wixburn" section with the Burn magic.</summary>
  public static bool IsWixBurn(Stream stream)
    => WixBurnReader.FindSection(stream) is { Length: >= 4 } s && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s) == WixBurnReader.SectionMagic;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    WixBurnReader reader;
    try {
      reader = new WixBurnReader(stream);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException or System.Xml.XmlException) {
      var note = Encoding.UTF8.GetBytes($"format=wix-burn\nparse_status=partial\nerror={ex.Message.Replace('\n', ' ')}\n");
      return [new ArchiveEntryInfo(0, "metadata.ini", note.Length, note.Length, "stored", false, false, null, Kind: "metadata")];
    }
    return reader.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.Size, -1, "cab", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = new WixBurnReader(stream);
    foreach (var e in reader.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files))
        continue;
      WriteFile(outputDir, e.FileName, reader.Extract(e));
    }
  }
}
