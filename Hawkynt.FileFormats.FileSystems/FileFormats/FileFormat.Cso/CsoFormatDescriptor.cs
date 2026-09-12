#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cso;

/// <summary>
/// PSP CSO v1/v2 and ZSO compressed ISO image.
/// </summary>
/// <remarks>
/// The synthetic block entries expose logical, decompressed block contents. Replacing one therefore
/// round-trips through the same bytes a consumer of the ISO sees, while container maintenance can
/// canonicalize physical compression, index alignment and stale tail bytes independently.
/// </remarks>
public sealed class CsoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable,
    IArchiveLayoutMap, ILayoutOptimizable, IArchivePurgeable, ISyntheticEntryNames {

  private static readonly IReadOnlySet<string> SyntheticNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "FULL.cso", "FULL.ziso", "metadata.ini", "index.bin",
  };

  public string Id => "Cso";
  public string DisplayName => "PSP CSO/ZSO";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cso";
  public IReadOnlyList<string> Extensions => [".cso", ".ziso", ".zso"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("CISO"u8.ToArray(), Confidence: 0.90),
    new("ZISO"u8.ToArray(), Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("deflate", "Deflate / CSO v1"),
    new("lz4", "LZ4 / ZSO"),
    new("cso2", "CSO v2 (DEFLATE/LZ4)"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PSP CSO v1/v2 (DEFLATE/LZ4) and ZSO (LZ4) compressed ISO image.";
  public IReadOnlySet<string> SyntheticEntryNames => SyntheticNames;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var layout = CsoImage.ReadLayout(stream);
    var entries = new List<ArchiveEntryInfo>(4 + layout.BlockCount);
    var ext = layout.Variant == CsoVariant.Zso ? "ziso" : "cso";
    entries.Add(new ArchiveEntryInfo(0, $"FULL.{ext}", layout.FullSize, layout.FullSize, "Stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "Stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(2, "index.bin", layout.IndexRaw.Length * 4L, layout.IndexRaw.Length * 4L, "Stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(3, "blocks", 0, 0, "Stored", true, false, null));

    for (var i = 0; i < layout.BlockCount; ++i) {
      var (_, physicalLength, encoding) = CsoImage.GetBlockSpan(layout, i);
      entries.Add(new ArchiveEntryInfo(
        Index: 4 + i,
        Name: $"blocks/block_{i:D5}.bin",
        OriginalSize: CsoImage.LogicalBlockLength(layout, i),
        CompressedSize: physicalLength,
        Method: MethodName(encoding),
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: null,
        Kind: encoding == CsoBlockEncoding.Stored ? "stored" : "compressed"));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var layout = CsoImage.ReadLayout(stream);
    var ext = layout.Variant == CsoVariant.Zso ? "ziso" : "cso";
    var fullName = $"FULL.{ext}";

    if (Wants(files, fullName)) {
      if (layout.FullSize > int.MaxValue)
        throw new InvalidDataException("CSO/ZSO whole-image synthetic entry is too large for buffered extraction.");
      stream.Position = 0;
      var bytes = new byte[(int)layout.FullSize];
      CsoImage.ReadExact(stream, bytes);
      WriteFile(outputDir, fullName, bytes);
    }

    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(layout)));

    if (Wants(files, "index.bin")) {
      var indexBytes = new byte[layout.IndexRaw.Length * sizeof(uint)];
      for (var i = 0; i < layout.IndexRaw.Length; ++i)
        BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(i * sizeof(uint), sizeof(uint)), layout.IndexRaw[i]);
      WriteFile(outputDir, "index.bin", indexBytes);
    }

    for (var i = 0; i < layout.BlockCount; ++i) {
      var name = $"blocks/block_{i:D5}.bin";
      if (!Wants(files, name))
        continue;
      var decoded = CsoImage.DecodeBlock(stream, layout, i);
      var logicalLength = CsoImage.LogicalBlockLength(layout, i);
      WriteFile(outputDir, name, decoded.AsSpan(0, logicalLength).ToArray());
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var variant = VariantFromCreateOptions(options);
    var blockSize = options.GetOptionInt("BlockSize", CsoWriter.DefaultBlockSize);
    if (blockSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(options), "CSO/ZSO BlockSize must be positive.");

    using var payload = CsoInPlaceModifier.CreateScratchStream();
    ulong size = 0;
    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      var content = input.ReadContent();
      payload.Write(content);
      size = checked(size + (ulong)content.Length);
    }
    payload.Position = 0;
    CsoWriter.Write(output, payload, size, blockSize, variant);
  }

  /// <summary>Replaces logical block_NNNNN.bin entries transactionally.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var layout = CsoImage.ReadLayout(archive);
    var replacements = new Dictionary<int, byte[]>();
    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      var index = ParseBlockIndex(input.ArchiveName.Replace('\\', '/'));
      if (index < 0)
        throw BlockNamespaceOnly(input.ArchiveName, "added");
      if (index >= layout.BlockCount)
        throw new ArgumentOutOfRangeException(nameof(inputs), $"CSO/ZSO block index {index} outside [0, {layout.BlockCount}).");
      var content = input.ReadContent();
      if (content.Length != layout.BlockSize)
        throw new ArgumentException(
          $"Replacement block {index} must be exactly block_size ({layout.BlockSize}) bytes; got {content.Length}.",
          nameof(inputs));
      replacements[index] = content;
    }
    if (replacements.Count != 0)
      CsoInPlaceModifier.WriteBlocks(archive, replacements);
  }

  /// <summary>Clears logical blocks to zero; it does not edit the ISO 9660 directory tree inside them.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var layout = CsoImage.ReadLayout(archive);
    var zero = new byte[checked((int)layout.BlockSize)];
    var replacements = new Dictionary<int, byte[]>();
    foreach (var name in entryNames) {
      var index = ParseBlockIndex(name.Replace('\\', '/'));
      if (index < 0)
        throw BlockNamespaceOnly(name, "removed");
      if (index >= layout.BlockCount)
        throw new ArgumentOutOfRangeException(nameof(entryNames), $"CSO/ZSO block index {index} outside [0, {layout.BlockCount}).");
      replacements[index] = zero;
    }
    if (replacements.Count != 0)
      CsoInPlaceModifier.WriteBlocks(archive, replacements);
  }

  // ── Maintenance ──────────────────────────────────────────────────────

  /// <summary>Canonical repack: ordered blocks, align=0, no stale/orphaned body bytes.</summary>
  public void Defragment(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("CSO/ZSO defragmentation requires a readable, writable, seekable stream.", nameof(archive));
    var layout = CsoImage.ReadLayout(archive);
    using var staged = CsoInPlaceModifier.CreateScratchStream();
    Repack(archive, staged, layout, checked((int)layout.BlockSize));
    Commit(staged, archive, truncate: true);
  }

  /// <summary>
  /// Rebuilds canonically and uses it only when it is smaller; otherwise copies the source through.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    using var staged = CsoInPlaceModifier.CreateScratchStream();
    var useStaged = false;
    try {
      var layout = CsoImage.ReadLayout(input);
      Repack(input, staged, layout, checked((int)layout.BlockSize));
      useStaged = staged.Length < input.Length;
    } catch {
      useStaged = false;
    }

    output.Position = 0;
    output.SetLength(0);
    var chosen = useStaged ? staged : input;
    chosen.Position = 0;
    chosen.CopyTo(output);
  }

  /// <summary>
  /// Wipes dead bytes by canonicalizing into the beginning of the same-sized stream and zeroing the
  /// now-unindexed tail. If the local codecs cannot reproduce the image no larger than the source,
  /// no byte is touched.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("CSO/ZSO wipe requires a readable, writable, seekable stream.", nameof(image));

    var originalLength = image.Length;
    var layout = CsoImage.ReadLayout(image);
    using var staged = CsoInPlaceModifier.CreateScratchStream();
    Repack(image, staged, layout, checked((int)layout.BlockSize));
    if (staged.Length >= originalLength)
      return 0;

    var reclaimed = originalLength - staged.Length;
    staged.Position = 0;
    image.Position = 0;
    staged.CopyTo(image);
    WriteZeros(image, reclaimed);
    image.Flush();
    return reclaimed;
  }

  /// <summary>Describes the real container byte layout; only bytes after the final index are free.</summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    var layout = CsoImage.ReadLayout(archive);
    if (layout.DataStart > 0)
      yield return new DefragBlockInfo(0, layout.DataStart, DefragBlockKind.MetadataReserved, "CSO/ZSO header + index");

    for (var i = 0; i < layout.BlockCount; ++i) {
      var (offset, length, encoding) = CsoImage.GetBlockSpan(layout, i);
      if (length <= 0)
        continue;
      yield return new DefragBlockInfo(offset, length, DefragBlockKind.Used,
        $"blocks/block_{i:D5}.bin", encoding switch {
          CsoBlockEncoding.Stored => DefragBlockClass.Frozen,
          CsoBlockEncoding.Lz4 => DefragBlockClass.Cold,
          _ => DefragBlockClass.Normal,
        });
    }

    if (layout.DataEnd < layout.FullSize)
      yield return new DefragBlockInfo(layout.DataEnd, layout.FullSize - layout.DataEnd, DefragBlockKind.Free, "unindexed tail");
  }

  public LayoutAnalysis AnalyzeLayout(Stream image) {
    var layout = CsoImage.ReadLayout(image);
    var trailing = Math.Max(0, layout.FullSize - layout.DataEnd);
    var notes = new List<string> {
      $"{VariantName(layout.Variant)} with {layout.BlockCount:N0} independently compressed blocks.",
      "Canonical rebuilds emit align=0 and remove index-alignment padding/orphaned data without changing logical ISO bytes.",
    };
    if (layout.Align != 0)
      notes.Add($"Current index_shift={layout.Align}; a rebuild can remove that alignment padding.");
    return new LayoutAnalysis {
      ImageSize = layout.FullSize,
      CurrentUnitSize = checked((int)layout.BlockSize),
      CurrentSlackBytes = trailing,
      OptimalUnitSize = checked((int)layout.BlockSize),
      OptimalSlackBytes = 0,
      RequiresRebuild = ["BlockSize", "IndexShift"],
      Notes = notes,
    };
  }

  /// <summary>Reblocks and recompresses while preserving the source CSO/ZSO variant and logical ISO bytes.</summary>
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);
    var layout = CsoImage.ReadLayout(source);
    var targetBlockSize = options.UnitSize > 0 ? options.UnitSize : checked((int)layout.BlockSize);
    if (targetBlockSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(options), "CSO/ZSO target BlockSize must be positive.");
    Repack(source, target, layout, targetBlockSize);
  }

  /// <summary>Leaves a valid empty container of the same CSO/ZSO variant and block geometry.</summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("CSO/ZSO purge requires a readable, writable, seekable stream.", nameof(archive));
    var layout = CsoImage.ReadLayout(archive);
    using var emptyInput = new MemoryStream([], writable: false);
    using var staged = CsoInPlaceModifier.CreateScratchStream();
    CsoWriter.Write(staged, emptyInput, 0, checked((int)layout.BlockSize), layout.Variant);
    Commit(staged, archive, truncate: true);
  }

  private static void Repack(Stream source, Stream target, CsoLayout layout, int targetBlockSize) {
    source.Position = 0;
    using var logical = CsoImage.OpenLogicalStream(source, layout);
    target.Position = 0;
    target.SetLength(0);
    CsoWriter.Write(target, logical, layout.UncompressedSize, targetBlockSize, layout.Variant);
  }

  private static void Commit(Stream staged, Stream target, bool truncate) {
    staged.Position = 0;
    target.Position = 0;
    if (truncate)
      target.SetLength(0);
    staged.CopyTo(target);
    target.Flush();
  }

  private static void WriteZeros(Stream stream, long count) {
    Span<byte> zeros = stackalloc byte[4096];
    while (count > 0) {
      var length = (int)Math.Min(count, zeros.Length);
      stream.Write(zeros[..length]);
      count -= length;
    }
  }

  private static CsoVariant VariantFromCreateOptions(FormatCreateOptions options) {
    var requested = options.GetString("Variant") ?? options.MethodName;
    if (string.IsNullOrWhiteSpace(requested))
      return CsoVariant.CsoV1;
    return requested.Trim().ToLowerInvariant() switch {
      "cso" or "cso1" or "deflate" or "stored" => CsoVariant.CsoV1,
      "zso" or "lz4" => CsoVariant.Zso,
      "cso2" or "v2" => CsoVariant.CsoV2,
      _ => throw new NotSupportedException($"Unsupported CSO/ZSO creation variant '{requested}'."),
    };
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static string BuildMetadataIni(CsoLayout layout) {
    var sb = new StringBuilder();
    sb.Append("[Cso]\n");
    sb.Append(CultureInfo.InvariantCulture, $"magic={layout.Magic}\n");
    sb.Append(CultureInfo.InvariantCulture, $"variant={VariantName(layout.Variant)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"header_size={layout.HeaderSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"uncompressed_size={layout.UncompressedSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"block_size={layout.BlockSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"version={layout.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"align={layout.Align}\n");
    sb.Append(CultureInfo.InvariantCulture, $"block_count={layout.BlockCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_size={layout.FullSize}\n");
    return sb.ToString();
  }

  private static string VariantName(CsoVariant variant) => variant switch {
    CsoVariant.CsoV1 => "cso1",
    CsoVariant.CsoV2 => "cso2",
    CsoVariant.Zso => "zso",
    _ => "unknown",
  };

  private static string MethodName(CsoBlockEncoding encoding) => encoding switch {
    CsoBlockEncoding.Stored => "Stored",
    CsoBlockEncoding.Deflate => "Deflate",
    CsoBlockEncoding.Lz4 => "LZ4",
    _ => "Unknown",
  };

  private static NotSupportedException BlockNamespaceOnly(string name, string verb)
    => new(
      $"CSO/ZSO: '{name}' cannot be {verb}. A compressed ISO container is edited at logical " +
      "block_NNNNN.bin granularity; editing the ISO 9660 filesystem inside it is a separate operation.");

  private static int ParseBlockIndex(string name) {
    var fileName = Path.GetFileNameWithoutExtension(name);
    const string prefix = "block_";
    if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
      return -1;
    return int.TryParse(fileName[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0
      ? index
      : -1;
  }
}
