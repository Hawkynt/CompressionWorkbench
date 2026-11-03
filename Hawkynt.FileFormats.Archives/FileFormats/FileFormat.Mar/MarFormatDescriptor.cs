#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mar;

/// <summary>
/// Mozilla ARchive (MAR) — the container of Firefox / Thunderbird update packages.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://wiki.mozilla.org/Software_Update:MAR</c> — Mozilla's format description</description></item>
///   <item><description><c>https://github.com/mozilla-releng/build-mar</c> — mardor, Mozilla release engineering's MAR reader/writer (the oracle)</description></item>
/// </list>
/// </summary>
public sealed class MarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  public string Id => "Mar";
  public string DisplayName => "MAR";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: unsigned archives are rebuilt with their product-information / additional sections and
  // member permission bits intact; signed archives refuse edits (the signatures would no longer verify).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".mar";
  public IReadOnlyList<string> Extensions => [".mar"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("MAR1"u8.ToArray(), Confidence: 0.9)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("store", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Mozilla update archive. Members are listed and extracted exactly as stored (update packages hold them " +
    "bzip2- or xz-compressed; the Method column names which). Create options: channel + productversion " +
    "write a product-information block.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MarReader(stream);
    var list = new List<ArchiveEntryInfo>(r.Entries.Count);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      list.Add(new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, MarReader.DescribeStoredData(Head(stream, e)), false, false, null));
    }
    return list;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MarReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var channel = options.GetString("channel");
    var version = options.GetString("productversion");
    if ((channel == null) != (version == null))
      throw new ArgumentException("MAR: channel and productversion must be given together.");
    IReadOnlyList<MarAdditionalSection>? sections = channel != null ? [MarWriter.ProductInformation(channel, version!)] : null;
    var files = FilesOnly(inputs).Select(f => (f.Name, f.Data, MarWriter.DefaultFlags)).ToList();
    MarWriter.Write(output, files, sections);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Rewrite(archive, files => {
      foreach (var (name, data) in FilesOnly(inputs)) {
        var existing = files.FindIndex(f => string.Equals(f.Name, name, StringComparison.Ordinal));
        if (existing >= 0) files[existing] = (name, data, files[existing].Flags);
        else files.Add((name, data, MarWriter.DefaultFlags));
      }
    });
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var drop = new HashSet<string>(entryNames, StringComparer.Ordinal);
    Rewrite(archive, files => files.RemoveAll(f => drop.Contains(f.Name)));
  }

  private static void Rewrite(Stream archive, Action<List<(string Name, byte[] Data, uint Flags)>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;
    var r = new MarReader(archive);
    if (r.Signatures.Count > 0)
      throw new NotSupportedException("MAR: the archive is signed; editing it would invalidate its signatures.");
    var files = r.Entries.Select(e => (e.Name, r.Extract(e), e.Flags)).ToList();
    edit(files);
    using var rebuilt = new MemoryStream();
    MarWriter.Write(rebuilt, files, r.HasSignatureBlock ? r.AdditionalSections : null);
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
  }

  private static byte[] Head(Stream stream, MarEntry e) {
    var n = (int)Math.Min(6u, e.Size);
    var buf = new byte[n];
    stream.Position = e.Offset;
    stream.ReadExactly(buf);
    return buf;
  }
}
