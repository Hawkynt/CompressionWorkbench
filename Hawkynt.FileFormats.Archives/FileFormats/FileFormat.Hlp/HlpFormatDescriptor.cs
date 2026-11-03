#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Hlp;

/// <summary>
/// Windows Help (<c>.hlp</c>), read-only: the internal files of the help file system (<c>|SYSTEM</c>,
/// <c>|TOPIC</c>, <c>|FONT</c>, <c>|CONTEXT</c>, embedded bitmaps, baggage) exactly as stored. The
/// leading <c>|</c> that marks WinHelp's own files cannot appear in a file name, so it is shown as
/// <c>#</c> (<c>#SYSTEM</c>); topic text is not decompressed into RTF.
///
/// References:
/// <list type="bullet">
///   <item><description>Manfred Winterhoff, "Windows Help File Format" (helpdeco's <c>helpfile.txt</c>) — the format description</description></item>
///   <item><description><c>https://github.com/pmachapman/helpdeco</c> — helpdeco, the oracle (<c>-d</c> directory listing, internal-file dumps)</description></item>
/// </list>
/// </summary>
public sealed class HlpFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Hlp";
  public string DisplayName => "Windows Help (HLP)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".hlp";
  public IReadOnlyList<string> Extensions => [".hlp"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x3F, 0x5F, 0x03, 0x00], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("hlp", "WinHelp internal files")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Windows Help file: internal files as stored (|SYSTEM shown as #SYSTEM), read-only";

  /// <summary>The entry name of an internal file: WinHelp's '|' marker becomes '#'.</summary>
  public static string EntryName(string internalName) => internalName.Replace('|', '#');

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new HlpReader(ReadAll(stream));
    return [.. r.Files.Select((f, i) => new ArchiveEntryInfo(i, EntryName(f.Name), f.UsedSpace, f.UsedSpace, "stored", false, false, null))];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new HlpReader(ReadAll(stream));
    foreach (var f in r.Files) {
      var name = EntryName(f.Name);
      if (files == null || MatchesFilter(name, files))
        WriteFile(outputDir, name, r.Extract(f));
    }
  }

  private static byte[] ReadAll(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
