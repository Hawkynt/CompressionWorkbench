using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pea;

/// <summary>PEA archive (.pea), PeaZip's native format.</summary>
public sealed class PeaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema {
  /// <inheritdoc />
  public string Id => "Pea";
  /// <inheritdoc />
  public string DisplayName => "PEA (PeaZip)";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <inheritdoc />
  public string DefaultExtension => ".pea";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".pea"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  // 0xEA, version 1, revision 0-6: three bytes so a stray 0xEA byte does not claim a file.
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [.. Enumerable.Range(0, 7).Select(r => new MagicSignature([0xEA, 0x01, (byte)r], Confidence: 0.80))];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("store", "PCOMPRESS0 (stored)"), new("fast", "PCOMPRESS1 (zlib 3)"), new("normal", "PCOMPRESS2 (zlib 6)"), new("best", "PCOMPRESS3 (zlib 9)"),
  ];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description => "PeaZip's PEA archive: zlib blocks with per-object, stream and volume checks, as one file or a volume set (name.000001.pea, ...)";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => PeaArchive.Read(stream).Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Size, -1,
      e.IsDirectory ? "" : "PEA", e.IsDirectory, false, e.LastModified)).ToList();

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in PeaArchive.Read(stream)) {
      if (e.IsDirectory || files is { Length: > 0 } && !MatchesFilter(e.Path, files)) continue;
      WriteFile(outputDir, e.Path, e.Data);
    }
  }

  /// <inheritdoc />
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password)
    => PeaArchive.Read(archive).FirstOrDefault(e => !e.IsDirectory && string.Equals(e.Path, entryName, StringComparison.OrdinalIgnoreCase))?.Data ?? [];

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(inputs);
    if (!string.IsNullOrEmpty(options.Password))
      throw new NotSupportedException("PEA encryption (EAX / Twofish / Serpent with scrypt or PBKDF2) is not written.");
    var compression = (options.MethodName ?? "normal").Trim().ToLowerInvariant() switch {
      "store" or "pcompress0" => 0,
      "fast" or "pcompress1" => 1,
      "" or "normal" or "pcompress2" => 2,
      "best" or "pcompress3" => 3,
      var m => throw new NotSupportedException($"PEA method '{m}' is not supported."),
    };
    var volumeSize = ParseVolumeSize(options.GetString(FormatOptionKeys.VolumeSize));
    Func<int, Stream>? openVolume = null;
    if (volumeSize > 0) {
      var path = OutputFilePath(output) ?? throw new ArgumentException("A PEA volume set must be written to a file, so its other volumes can go beside it.");
      var setBase = PeaArchive.VolumeSetBase(path);
      if (setBase is null || !Path.GetFileName(path).Equals(Path.GetFileName(PeaArchive.VolumePath(setBase, 1)), StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException($"The first volume of a PEA volume set is named name.000001.pea; '{Path.GetFileName(path)}' is not.");
      openVolume = n => File.Create(PeaArchive.VolumePath(setBase, n));
    }
    PeaArchive.Write(output, inputs.Select(i => new PeaArchive.Input(i.ArchiveName, i.IsDirectory ? null : i.ReadContent(),
      i.Metadata?.LastWriteTimeUtc?.UtcDateTime ?? DateTime.UtcNow)), compression, volumeSize: volumeSize, openVolume: openVolume);
  }

  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: FormatOptionKeys.VolumeSize,
      DisplayName: "Volume size",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "Split into volumes of this size, volume check included (bytes, or with k/m/g). The target must be named name.000001.pea; name.000002.pea, ... follow beside it. Empty writes one file.",
      IsOptimizationAxis: false),
  ];
}
