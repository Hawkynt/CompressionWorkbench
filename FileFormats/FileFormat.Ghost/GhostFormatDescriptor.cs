#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ghost;

/// <summary>
/// Symantec / Norton Ghost backup-image descriptor — R/W for the
/// Ghost 11.x / 12.x record container, version-gated R/O fallback for
/// the legacy DOS-era Ghost 4-7 framing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> The Ghost 11.x / 12.x on-disk format is reverse-engineered
/// from Norton Ghost 11.5.1 binaries (ported from the MIT-licensed
/// <c>nyarime/gho</c> Go implementation). Round trip is verified by
/// self-write-then-read for stored, Fast LZ (Z1), and zlib levels 3-9 —
/// with and without password-based encryption.
/// </para>
/// <para>
/// <b>Legacy generations.</b> Ghost 4-7 uses a different chunk framing and
/// is <em>not</em> read by this descriptor — recognition is by first-byte
/// hint only, and any extraction request surfaces the raw container plus
/// a diagnostic metadata file pointing users at Symantec Ghost Explorer.
/// </para>
/// <para>
/// <b>Detection.</b> Magic <c>FE EF</c> at offset 0 with confidence 0.65
/// — the same magic is shared by other formats (e.g. Crusader 4-byte
/// headers) so we keep the confidence modest and rely on the registry's
/// extension hint (<c>.gho</c> / <c>.ghs</c>) to disambiguate.
/// </para>
/// </remarks>
public sealed class GhostFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  public string Id => "Ghost";
  public string DisplayName => "Symantec / Norton Ghost";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gho";
  public IReadOnlyList<string> Extensions => [".gho", ".ghs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0xFE, 0xEF], Offset: 0, Confidence: 0.65)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("fastlz", "Fast LZ (Z1)"),
    new("zlib-3", "High zlib (Z3)"),
    new("zlib-9", "High zlib (Z9)")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  public string Description =>
    "Symantec / Norton Ghost — R/W for the Ghost 11.x / 12.x record container " +
    "(FE EF + 0x012F18D8 record framing, Fast LZ Z1 + zlib Z3-Z9 compression, CRC-16 " +
    "stream cipher encryption, .ghs spanning). Format reverse-engineered from " +
    "Norton Ghost 11.5.1 binaries (ported from MIT-licensed nyarime/gho). Legacy " +
    "DOS-era Ghost 4-7 images are detected but version-gated — recovery requires " +
    "Symantec Ghost Explorer (ghostexp.exe) or Ghost32.exe. Self-round-trip is " +
    "test-covered for all supported compression modes including encryption.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new GhostReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new GhostReader(stream, password: password);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new GhostReader(archive, password: password);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"Ghost entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }

  // ── IArchiveCreatable ──────────────────────────────────────────────

  /// <summary>
  /// Produces a fresh Ghost 11.x / 12.x record container.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Inputs are mapped to Ghost records by leaf-name convention:
  /// </para>
  /// <list type="bullet">
  ///   <item><description><c>track0.bin</c> — written as the MBR / Track 0 record (sector count defaults to 63).</description></item>
  ///   <item><description><c>partition*.bin</c> (any name containing "partition") — written as compressed partition records.</description></item>
  ///   <item><description>any other entry — written as a partition (fallback so callers always get all bytes into the image).</description></item>
  /// </list>
  /// <para>
  /// The <see cref="FormatCreateOptions.MethodName"/> selects the compression
  /// mode: <c>stored</c>, <c>fastlz</c> (default), or <c>zlib-N</c> for N
  /// in 3..9. Passing a <see cref="FormatCreateOptions.Password"/>
  /// enables the CRC-16 stream cipher (the encryption flag at header
  /// byte 12, bit 1 is set).
  /// </para>
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var compression = MapMethodName(options.MethodName);
    using var w = new GhostWriter(output, compression, password: options.Password, leaveOpen: true);

    byte[]? track0 = null;
    var partitions = new List<byte[]>();

    foreach (var (name, data) in FlatFiles(inputs)) {
      if (name.Equals("track0.bin", StringComparison.OrdinalIgnoreCase) && track0 == null)
        track0 = data;
      else
        partitions.Add(data);
    }

    if (track0 != null)
      w.WriteTrack0(track0, sectors: 63);

    foreach (var p in partitions)
      w.WritePartition(p);

    w.WriteEnd();
  }

  private static byte MapMethodName(string? name) => name?.ToLowerInvariant() switch {
    null or "" or "fastlz" or "fast" or "z1" => GhostConstants.CompressionFast,
    "stored" or "none" or "z0" => GhostConstants.CompressionNone,
    "zlib-3" or "z3" or "high-3" => GhostConstants.CompressionHigh3,
    "zlib-4" or "z4" or "high-4" => GhostConstants.CompressionHigh4,
    "zlib-5" or "z5" or "high-5" => GhostConstants.CompressionHigh5,
    "zlib-6" or "z6" or "high-6" => GhostConstants.CompressionHigh6,
    "zlib-7" or "z7" or "high-7" => GhostConstants.CompressionHigh7,
    "zlib-8" or "z8" or "high-8" => GhostConstants.CompressionHigh8,
    "zlib-9" or "z9" or "high-9" or "high" => GhostConstants.CompressionHigh9,
    _ => throw new InvalidDataException($"Ghost: unknown compression method '{name}'.")
  };
}
