#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Atari8;

public sealed class Atari8FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {

  // Writer emits SS/SD (92 176 bytes). Declared ceiling matches Atari8Writer.ImageSize.
  public long? MaxTotalArchiveSize => Atari8Writer.ImageSize;
  public string AcceptedInputsDescription =>
    "Atari 8-bit AtariDOS 2.x disk (SS/SD 92 176, SS/ED 133 136, or DS/DD 183 936 bytes).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>Canonical ATR sizes: SS/SD (92 176) is the one this WORM writer emits.</summary>
  public IReadOnlyList<long> CanonicalSizes => [Atari8Writer.ImageSize];

  public string Id => "Atari8";
  public string DisplayName => "ATR (Atari 8-bit)";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  public string DefaultExtension => ".atr";
  public IReadOnlyList<string> Extensions => [".atr"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // ATR magic 0x0296, stored little-endian as 96 02 at offset 0.
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x96, 0x02], Offset: 0, Confidence: 0.90)];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Atari 8-bit AtariDOS 2.x floppy disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new Atari8Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new Atari8Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var total = 0L;
    foreach (var i in inputs) if (!i.IsDirectory) total += new FileInfo(i.FullPath).Length;
    if (this.MaxTotalArchiveSize is long cap && total > cap)
      throw new InvalidOperationException(
        $"AtariDOS: combined input size {total} bytes exceeds SS/SD capacity ({cap} bytes).");

    var w = new Atari8Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }
}
