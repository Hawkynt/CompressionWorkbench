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
/// No normative public on-disk specification or independently verifiable
/// implementation has been located. The existing <c>54 46 53 01</c> detector is
/// therefore retained only as a legacy repository heuristic; it is not treated
/// as proof of a documented superblock layout.
/// </para>
/// <para>
/// Until allocation, namespace, transaction-publication and empty-volume
/// semantics are known, the filesystem is exposed as one opaque image. No write
/// or maintenance capability is advertised: claiming a wipe that can identify
/// no free bytes, or a rebuild that merely replaces the complete opaque image,
/// would be mechanically callable but semantically false.
/// </para>
/// </remarks>
public sealed class TfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private const string FullImageName = "FULL.tfs";
  private const string MetadataName = "metadata.ini";
  private const int LegacyMagicLength = 4;

  public string Id => "Tfs";
  public string DisplayName => "TFS (BBN Trans-FS)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".tfs";
  public IReadOnlyList<string> Extensions => [".tfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Legacy CompressionWorkbench heuristic. No normative public format source
    // has been found that establishes this as a TFS superblock signature.
    new([0x54, 0x46, 0x53, 0x01], Offset: 0, Confidence: 0.80),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "BBN Trans-FS — conservative opaque read-only surface; allocation and transaction layout remain undocumented.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead)
      throw new ArgumentException("TFS listing requires a readable stream.", nameof(stream));

    var (length, hasLegacyMagic) = Inspect(stream);
    return [
      new ArchiveEntryInfo(0, FullImageName, length, length, "stored", false, false, null),
      new ArchiveEntryInfo(1, MetadataName, 0, 0, "stored", false, false, null,
        Kind: hasLegacyMagic ? "ok" : "partial"),
    ];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
    if (!stream.CanRead)
      throw new ArgumentException("TFS extraction requires a readable stream.", nameof(stream));

    if (stream.CanSeek)
      stream.Position = 0;

    Span<byte> prefix = stackalloc byte[LegacyMagicLength];
    var prefixLength = ReadPrefix(stream, prefix);
    var hasLegacyMagic = HasLegacyMagic(prefix[..prefixLength]);

    if (Wants(FullImageName, files)) {
      Directory.CreateDirectory(outputDir);
      using var target = File.Create(Path.Combine(outputDir, FullImageName));
      target.Write(prefix[..prefixLength]);
      stream.CopyTo(target);
    }

    if (!Wants(MetadataName, files))
      return;

    var metadata = new StringBuilder()
      .Append("parse_status=").Append(hasLegacyMagic ? "ok" : "partial").Append('\n')
      .Append("magic_hex=0x54465301\n")
      .Append("signature_status=legacy_heuristic\n")
      .Append("layout_status=opaque\n")
      .Append("note=No normative public on-disk layout is known; allocation and transaction metadata are not guessed.\n")
      .ToString();
    WriteFile(outputDir, MetadataName, Encoding.UTF8.GetBytes(metadata));
  }

  private static (long Length, bool HasLegacyMagic) Inspect(Stream stream) {
    if (stream.CanSeek) {
      stream.Position = 0;
      Span<byte> prefix = stackalloc byte[LegacyMagicLength];
      var prefixLength = ReadPrefix(stream, prefix);
      var length = stream.Length;
      stream.Position = 0;
      return (length, HasLegacyMagic(prefix[..prefixLength]));
    }

    Span<byte> nonSeekablePrefix = stackalloc byte[LegacyMagicLength];
    var nonSeekablePrefixLength = ReadPrefix(stream, nonSeekablePrefix);
    long total = nonSeekablePrefixLength;
    var buffer = new byte[81920];
    int read;
    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
      total += read;

    return (total, HasLegacyMagic(nonSeekablePrefix[..nonSeekablePrefixLength]));
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

  private static bool HasLegacyMagic(ReadOnlySpan<byte> prefix)
    => prefix.Length >= LegacyMagicLength
      && prefix[0] == 0x54
      && prefix[1] == 0x46
      && prefix[2] == 0x53
      && prefix[3] == 0x01;

  private static bool Wants(string name, string[]? filter)
    => filter is not { Length: > 0 } || MatchesFilter(name, filter);
}
