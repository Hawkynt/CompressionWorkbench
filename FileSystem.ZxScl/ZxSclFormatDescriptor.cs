#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ZxScl;

public sealed class ZxSclFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {

  // Upper bound: max payload (40 tracks x 16 sectors x 256 bytes x 4 layers) + magic/headers/CRC.
  public long? MaxTotalArchiveSize => ZxSclReader.MaxPayloadSize;
  public string AcceptedInputsDescription =>
    "ZX Spectrum TR-DOS file (up to 655 360 bytes total; 8-char names).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>
  /// SCL is variable-size — there's no fixed canonical byte count. We declare the hard
  /// payload ceiling so <see cref="IArchiveShrinkable"/>-style consumers still have a target.
  /// </summary>
  public IReadOnlyList<long> CanonicalSizes => [];

  public string Id => "ZxScl";
  public string DisplayName => "SCL (ZX Spectrum)";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  public string DefaultExtension => ".scl";
  public IReadOnlyList<string> Extensions => [".scl"];
  public IReadOnlyList<string> CompoundExtensions => [];

  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(ZxSclReader.Magic, Offset: 0, Confidence: 0.95)];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ZX Spectrum SCL archive (TR-DOS compact form)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new ZxSclReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new ZxSclReader(stream);
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
        $"SCL: combined input size {total} bytes exceeds TR-DOS payload ceiling ({cap} bytes).");

    var w = new ZxSclWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }
}
