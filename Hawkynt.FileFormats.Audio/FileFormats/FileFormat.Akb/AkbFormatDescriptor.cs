#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Aac;
using Codec.MsAdpcm;
using Codec.Vorbis;
using Compression.Registry;
using FileFormat.Mp4;

namespace FileFormat.Akb;

/// <summary>
/// Square Enix classic AKB / AKB2 audio container.
/// <para>
/// The layout is implemented independently from the public behaviour documented by vgmstream's
/// AKB parser and cross-checked against Memoria's AKB2 writer. Classic AKB carries one material;
/// AKB2 carries a sound table with one or more materials.
/// </para>
/// </summary>
public sealed partial class AkbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable,
  IArchiveDefragmentable, IArchiveLayoutMap, IContainerRemuxable, IFormatOptionsSchema,
  IAudioContainerFormat, IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget {

  private static readonly string[] EncodeCodecs = ["pcm16le", "ms-adpcm", "vorbis", "aac"];
  private static readonly string[] MuxCodecs = ["pcm16le", "ms-adpcm", "ogg-vorbis", "m4a-aac", "aac"];

  public string Id => "Akb";
  public string DisplayName => "Square Enix AKB / AKB2";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".akb";
  public IReadOnlyList<string> Extensions => [".akb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("AKB "u8.ToArray(), Confidence: 0.98),
    new("AKB2"u8.ToArray(), Confidence: 0.98),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("pcm16le", "PCM16LE (AKB2)"),
    new("ms-adpcm", "Microsoft ADPCM"),
    new("vorbis", "Ogg Vorbis"),
    new("aac", "AAC-LC in M4A (classic AKB)"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Square Enix AKB/AKB2 audio: PCM16LE, MS-ADPCM, Ogg Vorbis and classic M4A/AAC.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Variant", "AKB variant", FormatOptionKind.Enum, "Auto", ["Auto", "Akb2", "Classic"],
      "Auto selects AKB2 except where the codec/encryption requires classic AKB."),
    new("ClassicVersion", "Classic version", FormatOptionKind.Enum, "Auto", ["Auto", "0", "2", "3"],
      "Observed classic versions are 0, 2 and 3. Auto uses v0 for AAC, v2 otherwise, and v3 for encryption.", "Variant=Auto|Classic"),
    new("Encrypt", "Encrypt payload", FormatOptionKind.Boolean, "false", null,
      "Square Enix sdlib XOR; documented only for classic-v3 Ogg Vorbis."),
    new("BlockAlign", "MS-ADPCM block align", FormatOptionKind.Integer, "512", null,
      "Microsoft ADPCM block size in bytes."),
    new("SampleRate", "Sample rate", FormatOptionKind.Integer, "44100", null,
      "Required for raw PCM/MS-ADPCM/M4A archive-create inputs; encoded PCM conversion uses the source rate."),
    new("Channels", "Channels", FormatOptionKind.Integer, "2", null,
      "Required for raw PCM/MS-ADPCM/M4A archive-create inputs; encoded PCM conversion uses the source channel count."),
    new("SampleCount", "Sample count", FormatOptionKind.Integer, "0", null,
      "Optional sample count for raw encoded archive-create inputs."),
    new("LoopStart", "Loop start", FormatOptionKind.Integer, "0"),
    new("LoopEnd", "Loop end", FormatOptionKind.Integer, "0"),
    new("LoopStart2", "Alternate loop start", FormatOptionKind.Integer, "0"),
    new("LoopEnd2", "Alternate loop end", FormatOptionKind.Integer, "0"),
    new("Quality", "Vorbis quality", FormatOptionKind.String, "0.5", null,
      "Managed Vorbis VBR quality from -0.1 through 1.0."),
    new("Bitrate", "AAC bitrate", FormatOptionKind.Integer, "128000", null,
      "Forwarded to the managed AAC-LC encoder."),
  ];

  public long? MaxTotalArchiveSize => uint.MaxValue;
  public string AcceptedInputsDescription =>
    "AKB accepts FULL.akb or complete .ogg/.m4a streams and raw .pcm/.msadpcm payloads; classic AKB holds one material, AKB2 can hold multiple.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) {
      reason = "AKB does not contain directories.";
      return false;
    }
    var name = Path.GetFileName(input.ArchiveName);
    if (name.Equals("FULL.akb", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".pcm", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".msadpcm", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }
    reason = $"unsupported AKB input '{input.ArchiveName}'; {this.AcceptedInputsDescription}";
    return false;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new AkbReader(stream, leaveOpen: true);
    var result = new List<ArchiveEntryInfo>(reader.Entries.Count + 1);
    for (var i = 0; i < reader.Entries.Count; ++i) {
      var entry = reader.Entries[i];
      result.Add(new ArchiveEntryInfo(i, entry.Name, entry.Size, entry.Size,
        CodecLabel(entry.Codec), false, entry.Encrypted, null, Kind: "Stream"));
    }
    var metadata = BuildMetadata(reader);
    result.Add(new ArchiveEntryInfo(reader.Entries.Count, AkbConstants.MetadataEntryName,
      metadata.Length, metadata.Length, "metadata", false, false, null, Kind: "Tag"));
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new AkbReader(stream, leaveOpen: true);
    foreach (var entry in reader.Entries) {
      if (files is { Length: > 0 } && !FormatHelpers.MatchesFilter(entry.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
    if (files is not { Length: > 0 } || FormatHelpers.MatchesFilter(AkbConstants.MetadataEntryName, files))
      FormatHelpers.WriteFile(outputDir, AkbConstants.MetadataEntryName, BuildMetadata(reader));
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    using var entry = this.OpenEntry(input, entryName, password);
    entry.CopyTo(output);
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    using var reader = new AkbReader(archive, leaveOpen: true);
    byte[] bytes;
    if (entryName.Equals(AkbConstants.MetadataEntryName, StringComparison.OrdinalIgnoreCase))
      bytes = BuildMetadata(reader);
    else {
      var entry = reader.Entries.FirstOrDefault(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                  ?? throw new FileNotFoundException($"Entry not found: {entryName}");
      bytes = reader.Extract(entry);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var entry = this.OpenEntry(archive, entryName, password);
    using var memory = new MemoryStream();
    entry.CopyTo(memory);
    return memory.ToArray();
  }

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    using var reader = new AkbReader(archive, leaveOpen: true);
    foreach (var entry in reader.Entries)
      if (entry.Size > 0)
        yield return new DefragBlockInfo(entry.Offset, entry.Size, DefragBlockKind.Used, FileName: entry.Name);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var files = inputs.Where(static input => !input.IsDirectory).ToArray();
    var full = files.FirstOrDefault(static input =>
      Path.GetFileName(input.ArchiveName).Equals("FULL.akb", StringComparison.OrdinalIgnoreCase));
    if (full is not null) {
      var bytes = full.ReadContent();
      using (var validate = new AkbReader(new MemoryStream(bytes, writable: false))) { }
      output.Write(bytes);
      return;
    }
    if (files.Length == 0)
      throw new InvalidOperationException("AKB creation requires at least one encoded audio input.");

    var prepared = new List<AkbWriteEntry>(files.Length);
    foreach (var file in files) {
      var data = file.ReadContent();
      var extension = Path.GetExtension(file.ArchiveName).ToLowerInvariant();
      prepared.Add(extension switch {
        ".ogg" => BuildOggInput(file.ArchiveName, data, options),
        ".m4a" => BuildRawInput(file.ArchiveName, data, AkbCodec.M4aAac, options),
        ".pcm" => BuildPcmInput(file.ArchiveName, data, options),
        ".msadpcm" => BuildMsAdpcmInput(file.ArchiveName, data, options),
        _ => throw new NotSupportedException($"Unsupported AKB creation input '{file.ArchiveName}'."),
      });
    }

    var codecForSelection = prepared.Any(static entry => entry.Codec == AkbCodec.M4aAac) ? "aac" : CodecId(prepared[0].Codec);
    var kind = ResolveVariant(options, codecForSelection, properties: null);
    var version = ResolveClassicVersion(options, codecForSelection, properties: null);
    var encrypt = ResolveEncrypt(options, properties: null);
    if (kind == AkbContainerKind.Classic && prepared.Count != 1)
      throw new NotSupportedException("Classic AKB contains exactly one material; use Variant=Akb2 for multiple inputs.");

    using var writer = new AkbWriter(output, leaveOpen: true) {
      ContainerKind = kind,
      ClassicVersion = version,
      Encrypt = encrypt,
    };
    foreach (var entry in prepared)
      writer.AddEncodedEntry(entry);
    writer.Write();
  }

}
