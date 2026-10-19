using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.SeqBox;

/// <summary>
/// SeqBox (.sbx), Marco Pontello's recoverable single-file container. Lists as one entry named by
/// the FNM metadata; creates the sbxenc layout (version 1, 512-byte blocks, full metadata).
/// </summary>
public sealed class SeqBoxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  /// <summary>Entry name used when the container has no metadata block (sbxdec appends ".out").</summary>
  public const string UnnamedEntry = "sbx.out";

  /// <inheritdoc />
  public string Id => "SeqBox";
  /// <inheritdoc />
  public string DisplayName => "SeqBox";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <inheritdoc />
  public string DefaultExtension => ".sbx";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".sbx"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SBx\x01"u8.ToArray(), Confidence: 0.85),
    new("SBx\x02"u8.ToArray(), Confidence: 0.85),
    new("SBx\x03"u8.ToArray(), Confidence: 0.85),
    new("SBx\x04"u8.ToArray(), Confidence: 0.85),
  ];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("sbx1", "512-byte blocks"), new("sbx2", "128-byte blocks"), new("sbx3", "4096-byte blocks"), new("sbx4", "488-byte blocks")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description => "SeqBox recoverable container: file split into self-identifying, CRC-checked, sequence-numbered blocks";

  private static string EntryName(SeqBoxContainer.Parsed p)
    => string.IsNullOrEmpty(p.Metadata?.FileName) ? UnnamedEntry : Path.GetFileName(p.Metadata!.FileName.Replace('\\', '/'));

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var p = SeqBoxContainer.Read(stream);
    DateTime? modified = p.Metadata?.FileTime is { } t ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime : null;
    return [new ArchiveEntryInfo(0, EntryName(p), p.PayloadLength, stream.Length, $"sbx{p.Version}", false, false, modified)];
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var p = SeqBoxContainer.Read(stream);
    var name = EntryName(p);
    if (files is { Length: > 0 } && !MatchesFilter(name, files))
      return;
    WriteFile(outputDir, name, p.Data);
  }

  /// <inheritdoc />
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    var p = SeqBoxContainer.Read(archive);
    return string.Equals(EntryName(p), entryName, StringComparison.OrdinalIgnoreCase) ? p.Data : [];
  }

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(i => !i.IsDirectory).ToList();
    if (files.Count != 1)
      throw new NotSupportedException($"A SeqBox container holds exactly one file ({files.Count} given).");
    if (!string.IsNullOrEmpty(options.Password))
      throw new NotSupportedException("SeqBox password scrambling is not an encryption method and is not written.");
    var version = (options.MethodName ?? "sbx1").Trim().ToLowerInvariant() switch {
      "" or "sbx1" or "1" => 1,
      "sbx2" or "2" => 2,
      "sbx3" or "3" => 3,
      "sbx4" or "4" => 4,
      var m => throw new NotSupportedException($"SeqBox method '{m}' is not supported (sbx1-sbx4)."),
    };
    var input = files[0];
    var name = Path.GetFileName(input.ArchiveName.Replace('\\', '/'));
    var mtime = input.Metadata?.LastWriteTimeUtc;
    SeqBoxContainer.Write(output, input.ReadContent(), name, version, fileTime: mtime);
  }
}
