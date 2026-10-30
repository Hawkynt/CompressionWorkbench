#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Lzh;

namespace FileFormat.C64Sfx;

/// <summary>
/// Commodore 64/128 self-extracting LHA archive (Chris Smeets' SFX): a BASIC/machine-code
/// extractor of 3711 or 3721 bytes followed by an ordinary LHA archive (-lh1- on the C64). Reading
/// skips the stub, up to two bytes before the first "-lh?-" marker, and reads the rest as LHA.
/// Writing would mean redistributing Smeets' extractor, so the format is read-only.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://unusedino.de/ec64/technical/formats/sfx.html</c> — Peter Schepers' SFX.TXT (rev 1.3)</description></item>
///   <item><description><c>http://fileformats.archiveteam.org/wiki/SFX</c> — format overview and samples</description></item>
/// </list>
/// </summary>
public sealed class C64SfxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private const string Signature = "C64/C128 SELF EXTRACTING LHARCHIVE";
  private const int MaxStubLength = 0x2000;

  public string Id => "C64Sfx";
  public string DisplayName => "C64/C128 SFX (self-extracting LHA)";
  public FormatCategory Category => FormatCategory.Archive;
  // RO: the payload is plain LHA; creating one would mean shipping Smeets' extractor stub.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".sfx";
  public IReadOnlyList<string> Extensions => [".sfx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // The copyright banner sits at 0x0D2B in the 3721-byte stub and ten bytes earlier in the 3711-byte one.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(System.Text.Encoding.ASCII.GetBytes(Signature), Offset: 0x0D2B, Confidence: 0.95),
    new(System.Text.Encoding.ASCII.GetBytes(Signature), Offset: 0x0D21, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lh1", "LHA -lh1-")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore 64/128 self-extracting LHA archive (Chris Smeets); read-only";

  /// <summary>Returns the embedded LHA archive (everything from two bytes before the first -lh?- marker).</summary>
  /// <exception cref="InvalidDataException">No LHA marker follows a plausible stub.</exception>
  public static MemoryStream Payload(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var data = ms.ToArray();
    var window = data.AsSpan(0, Math.Min(data.Length, MaxStubLength));
    for (var i = 2; i + 5 <= window.Length; ++i)
      if (window[i] == '-' && window[i + 1] == 'l' && window[i + 2] == 'h' && window[i + 4] == '-')
        return new MemoryStream(data, i - 2, data.Length - i + 2, writable: false);
    throw new InvalidDataException("No LHA archive follows the SFX extractor.");
  }

  // C64 LHA stores "name\0T", T being the CBM file type (P, S, U, R); the name ends at the NUL.
  private static (string Name, string? Type) Split(string stored) {
    var nul = stored.IndexOf('\0');
    return nul < 0 ? (stored, null) : (stored[..nul], stored[(nul + 1)..]);
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new LhaReader(Payload(stream));
    return r.Entries.Select((e, i) => {
      var (name, type) = Split(e.FileName);
      return new ArchiveEntryInfo(i, name, e.OriginalSize, e.CompressedSize,
        type is { Length: > 0 } ? $"{e.Method.Trim('-')}/{type}" : e.Method.Trim('-'), false, false, e.LastModified);
    }).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new LhaReader(Payload(stream));
    foreach (var e in r.Entries) {
      var (name, _) = Split(e.FileName);
      if (files != null && !FormatHelpers.MatchesFilter(name, files)) continue;
      FormatHelpers.WriteFile(outputDir, name, r.ExtractEntry(e));
    }
  }
}
