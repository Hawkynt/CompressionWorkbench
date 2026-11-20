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
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories | FormatCapabilities.SupportsPassword;
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
  public string Description => "PeaZip's PEA archive: zlib blocks with per-object, stream and volume checks or stream encryption (AES, Twofish, Serpent), as one file or a volume set (name.000001.pea, ...)";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => PeaArchive.Read(stream, password).Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Size, -1,
      e.IsDirectory ? "" : "PEA", e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in PeaArchive.Read(stream, password)) {
      if (e.IsDirectory || files is { Length: > 0 } && !MatchesFilter(e.Path, files)) continue;
      WriteFile(outputDir, e.Path, e.Data);
    }
  }

  /// <inheritdoc />
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password)
    => PeaArchive.Read(archive, password).FirstOrDefault(e => !e.IsDirectory && string.Equals(e.Path, entryName, StringComparison.OrdinalIgnoreCase))?.Data ?? [];

  /// <summary>
  /// The stream encryption for <see cref="FormatCreateOptions.EncryptionMethod"/>: PeaZip's names
  /// (EAX256 — the default —, TF256, SP256, EAX, TF, SP, HMAC, and the triple cascades TRIATS …
  /// HRISAT with an optional strength digit 0-7, as in SRIATS3) or aes256 / twofish256 / serpent256.
  /// </summary>
  internal static (PeaArchive.Check Check, int Strength) EncryptionOf(string? method) {
    var name = (method ?? "").Trim().ToUpperInvariant();
    string[] cascades = ["TRIATS", "TRITSA", "TRISAT", "SRIATS", "SRITSA", "SRISAT", "HRIATS", "HRITSA", "HRISAT"];
    if (name.Length is 6 or 7 && Array.IndexOf(cascades, name[..6]) is var index and >= 0) {
      var strength = name.Length == 7 ? name[6] - '0' : 0;
      if (strength is < 0 or > 7) throw new NotSupportedException($"PEA cascade strength '{name[6..]}' is not 0-7.");
      return ((PeaArchive.Check)(0x44 + index), strength);
    }
    return (SingleEncryptionOf(name), 0);
  }

  private static PeaArchive.Check SingleEncryptionOf(string name) => name switch {
    "" or "EAX256" or "AES256" or "AES" => PeaArchive.Check.Aes256Eax,
    "TF256" or "TWOFISH256" or "TWOFISH" => PeaArchive.Check.Twofish256Eax,
    "SP256" or "SERPENT256" or "SERPENT" => PeaArchive.Check.Serpent256Eax,
    "EAX" or "AES128" => PeaArchive.Check.AesEax,
    "TF" or "TWOFISH128" => PeaArchive.Check.TwofishEax,
    "SP" or "SERPENT128" => PeaArchive.Check.SerpentEax,
    "HMAC" => PeaArchive.Check.AesHmac,
    var m => throw new NotSupportedException($"PEA encryption '{m}' is not supported (EAX256, TF256, SP256, EAX, TF, SP, HMAC, TRIATS … HRISAT)."),
  };

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(inputs);
    var encrypted = !string.IsNullOrEmpty(options.Password);
    var (streamCheck, strength) = encrypted ? EncryptionOf(options.EncryptionMethod) : (PeaArchive.Check.Sha256, 0);
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
      i.Metadata?.LastWriteTimeUtc?.UtcDateTime ?? DateTime.UtcNow)), compression, streamCheck: streamCheck,
      volumeSize: volumeSize, openVolume: openVolume, password: encrypted ? options.Password : null, cascadeStrength: strength);
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
