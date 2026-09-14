#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tfs;

/// <summary>
/// Conservative read-only descriptor for the format historically registered as
/// BBN Trans-FS (TFS) in CompressionWorkbench.
/// </summary>
/// <remarks>
/// <para>
/// No normative public on-disk specification, independently verifiable reference
/// implementation or genuine sample image has been located. The repository used
/// <c>54 46 53 01</c> as a detector from its initial TFS stub, but that value was
/// introduced without a source and therefore no longer participates in automatic
/// format detection. The <c>.tfs</c> extension is the only routing hint retained.
/// </para>
/// <para>
/// Until format identity, allocation, namespace, transaction-publication and
/// empty-volume semantics are independently established, the input is exposed as
/// one opaque image. No write or maintenance capability is advertised: claiming
/// a wipe that can identify no free bytes, or a rebuild that merely replaces the
/// complete opaque image, would be mechanically callable but semantically false.
/// </para>
/// </remarks>
public sealed class TfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private const string FullImageName = "FULL.tfs";
  private const string MetadataName = "metadata.ini";
  private const int RepositoryHeuristicLength = 4;

  public string Id => "Tfs";
  public string DisplayName => "TFS (BBN Trans-FS)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".tfs";
  public IReadOnlyList<string> Extensions => [".tfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "TFS (historically labelled BBN Trans-FS) — opaque extension-routed surface; format identity and on-disk layout remain unverified.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead)
      throw new ArgumentException("TFS listing requires a readable stream.", nameof(stream));

    var length = MeasureLength(stream);
    return [
      new ArchiveEntryInfo(0, FullImageName, length, length, "stored", false, false, null),
      new ArchiveEntryInfo(1, MetadataName, 0, 0, "stored", false, false, null, Kind: "opaque"),
    ];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
    if (!stream.CanRead)
      throw new ArgumentException("TFS extraction requires a readable stream.", nameof(stream));

    if (stream.CanSeek)
      stream.Position = 0;

    Span<byte> prefix = stackalloc byte[RepositoryHeuristicLength];
    var prefixLength = ReadPrefix(stream, prefix);
    var matchesRepositoryHeuristic = MatchesRepositoryHeuristic(prefix[..prefixLength]);

    if (Wants(FullImageName, files)) {
      Directory.CreateDirectory(outputDir);
      using var target = File.Create(Path.Combine(outputDir, FullImageName));
      target.Write(prefix[..prefixLength]);
      stream.CopyTo(target);
    }

    if (!Wants(MetadataName, files))
      return;

    var metadata = new StringBuilder()
      .Append("parse_status=opaque\n")
      .Append("format_identity=unverified\n")
      .Append("legacy_magic_hex=0x54465301\n")
      .Append("legacy_magic_match=").Append(matchesRepositoryHeuristic ? "true" : "false").Append('\n')
      .Append("signature_status=unverified_repository_heuristic\n")
      .Append("layout_status=opaque\n")
      .Append("note=The historical BBN Trans-FS label and legacy magic are not backed by a located normative source; allocation and transaction metadata are not guessed.\n")
      .ToString();
    WriteFile(outputDir, MetadataName, Encoding.UTF8.GetBytes(metadata));
  }

  private static long MeasureLength(Stream stream) {
    if (stream.CanSeek) {
      stream.Position = 0;
      var length = stream.Length;
      stream.Position = 0;
      return length;
    }

    long total = 0;
    var buffer = new byte[81920];
    int read;
    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
      total += read;

    return total;
  }

  private static int ReadPrefix(Stream stream, Span<byte> destination) {
    var total = 0;
    while (total < destination.Length) {
      var read = stream.Read(destination[total..]);
      if (read == 0)
        break;
      total += read;
    }
    return total;
  }

  private static bool MatchesRepositoryHeuristic(ReadOnlySpan<byte> prefix)
    => prefix.Length >= RepositoryHeuristicLength
      && prefix[0] == 0x54
      && prefix[1] == 0x46
      && prefix[2] == 0x53
      && prefix[3] == 0x01;

  private static bool Wants(string name, string[]? filter)
    => filter is not { Length: > 0 } || MatchesFilter(name, filter);
}
