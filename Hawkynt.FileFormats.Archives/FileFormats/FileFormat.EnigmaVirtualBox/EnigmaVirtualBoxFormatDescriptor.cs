#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.EnigmaVirtualBox;

/// <summary>
/// Enigma Virtual Box: the files and folders it boxes into an executable, stored or
/// aPLib-compressed.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/mos9527/evbunpack</c> — evbunpack, the unpacker this reader was converted from</description></item>
///   <item><description><c>https://enigmaprotector.com/en/aboutvb.html</c> — Enigma Virtual Box</description></item>
/// </list>
/// </summary>
public sealed class EnigmaVirtualBoxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "EnigmaVirtualBox";
  public string DisplayName => "Enigma Virtual Box";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".exe";
  // Detected by its ".enigma1" section (FormatDetector.DetectInstaller), never by ".exe".
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("aplib", "aPLib"), new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Enigma Virtual Box: the virtual filesystem boxed into an executable";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    EnigmaVirtualBoxReader reader;
    try {
      reader = new EnigmaVirtualBoxReader(stream);
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException) {
      var note = Encoding.UTF8.GetBytes($"format=enigma-virtual-box\nparse_status=partial\nerror={ex.Message.Replace('\n', ' ')}\n");
      return [new ArchiveEntryInfo(0, "metadata.ini", note.Length, note.Length, "stored", false, false, null, Kind: "metadata")];
    }
    return reader.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.FileName, e.Size, e.StoredSize, e.IsCompressed ? "aplib" : "stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = new EnigmaVirtualBoxReader(stream);
    foreach (var e in reader.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files))
        continue;
      if (e.IsDirectory)
        Directory.CreateDirectory(Path.Combine(outputDir, e.FileName));
      else
        WriteFile(outputDir, e.FileName, reader.Extract(e));
    }
  }
}
