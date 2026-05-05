#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Awb;

/// <summary>
/// CRI Audio Wave Bank (AFS2) — used by Capcom (Resident Evil, Monster Hunter), Sega
/// (Yakuza, Persona 5), and other CRI Middleware titles. Contains raw codec payloads
/// (HCA, ADX, etc.) which are surfaced verbatim — we do not decode the inner audio.
/// </summary>
public sealed class AwbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  public string Id => "Awb";
  public string DisplayName => "CRI Audio Wave Bank";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".awb";
  public IReadOnlyList<string> Extensions => [".awb", ".acb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("AFS2"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("afs2", "AFS2")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "CRI Middleware Audio Wave Bank (Capcom / Sega games)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new AwbReader(stream, leaveOpen: true);
    var meta = r.BuildMetadataIni();
    var list = new List<ArchiveEntryInfo>(r.Entries.Count + 1);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      list.Add(new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, "Stored", false, false, null));
    }
    list.Add(new ArchiveEntryInfo(r.Entries.Count, "metadata.ini", meta.Length, meta.Length, "Stored", false, false, null));
    return list;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new AwbReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
    if (files == null || files.Length == 0 || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", r.BuildMetadataIni());
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new AwbWriter(output, leaveOpen: true);
    foreach (var (_, data) in FlatFiles(inputs))
      w.AddEntry(data);
  }
}
