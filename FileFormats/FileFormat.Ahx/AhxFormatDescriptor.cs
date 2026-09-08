#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ahx;

/// <summary>
/// Reads and writes the published four-channel AHX0/AHX1 tracker-module format.
/// The pseudo-archive exposes both the byte-exact original and normalized structural
/// blocks; packet demux/mux treats a complete AHX module as one opaque encoded unit.
/// PCM rendering or PCM-to-tracker transcription is deliberately not claimed.
/// </summary>
public sealed class AhxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable, IFormatOptionsSchema,
  IAudioContainerFormat, IAudioDemuxSource, IAudioMuxTarget {

  private const string CodecId = "ahx";

  public string Id => "Ahx";
  public string DisplayName => "AHX / THX Synth-Tracker";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ahx";
  public IReadOnlyList<string> Extensions => [".ahx", ".thx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("THX"u8.ToArray(), Offset: 0, Confidence: 0.9),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored tracker module")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description =>
    "Amiga AHX0/AHX1 synth-tracker module with structural write, packet-preserving mux/remux, " +
    "and pseudo-archive demux of subsongs, positions, logical tracks, instruments, and names.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Version", "AHX version", FormatOptionKind.Enum, "Auto", ["Auto", "AHX0", "AHX1"],
      "Preserve the source version, or rewrite to AHX0/AHX1 when every used feature is representable."),
    new("SpeedMultiplier", "Timing multiplier", FormatOptionKind.Enum, "Auto", ["Auto", "1", "2", "3", "4"],
      "CIA timing multiplier: 1=50 Hz, 2=100 Hz, 3=150 Hz, 4=200 Hz. AHX0 permits only 1."),
    new("TrackZeroStorage", "Track 0 storage", FormatOptionKind.Enum, "Auto", ["Auto", "Stored", "Omitted"],
      "Preserve the source choice, force track 0 into the file, or omit it when it is completely empty."),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => Decompose(ReadAll(stream)).Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Data.LongLength, entry.Data.LongLength,
      "stored", false, false, null, entry.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var entry in Decompose(ReadAll(stream))) {
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files))
        continue;
      WriteFile(outputDir, entry.Name, entry.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var entry = Decompose(ReadAll(input)).FirstOrDefault(candidate =>
      candidate.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));
    if (entry.Data is null)
      throw new FileNotFoundException($"AHX pseudo-entry '{entryName}' was not found.", entryName);
    output.Write(entry.Data);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var files = FormatHelpers.FilesOnly(inputs).ToArray();
    var full = files.FirstOrDefault(static input => {
      var name = Path.GetFileName(input.ArchiveName);
      return name.Equals("FULL.ahx", StringComparison.OrdinalIgnoreCase)
             || name.Equals("FULL.thx", StringComparison.OrdinalIgnoreCase);
    });

    if (full is not null) {
      var bytes = full.ReadContent();
      var module = AhxModule.Read(bytes);
      WriteWithOptions(output, bytes, module, options);
      return;
    }

    var byName = files
      .GroupBy(static input => Path.GetFileName(input.ArchiveName), StringComparer.OrdinalIgnoreCase)
      .ToDictionary(static group => group.Key, static group => group.Single(), StringComparer.OrdinalIgnoreCase);

    var metadata = ParseMetadata(Required(byName, "metadata.ini").ReadContent());
    var version = checked((byte)RequiredInt(metadata, "version"));
    var speedMultiplier = RequiredInt(metadata, "speed_multiplier");
    var restart = RequiredInt(metadata, "restart");
    var trackLength = RequiredInt(metadata, "track_length");
    var maxTrack = RequiredInt(metadata, "max_track");
    var instrumentCount = RequiredInt(metadata, "num_instruments");
    var subsongCount = RequiredInt(metadata, "subsongs");
    var trackZeroStored = RequiredBool(metadata, "track_zero_stored");

    var subsongs = Optional(byName, "subsongs.bin")?.ReadContent() ?? [];
    var positions = Required(byName, "positions.bin").ReadContent();
    var tracks = Required(byName, "tracks.bin").ReadContent();
    var instruments = Optional(byName, "instruments.bin")?.ReadContent() ?? [];
    var names = Required(byName, "names.bin").ReadContent();

    var module = AhxModule.FromParts(
      version, speedMultiplier, restart, trackLength, maxTrack, instrumentCount, subsongCount,
      trackZeroStored, subsongs, positions, tracks, instruments, names);
    var encoded = Rewrite(module, options);
    output.Write(encoded);
  }

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "AHX accepts FULL.ahx/FULL.thx, or metadata.ini + positions.bin + tracks.bin + names.bin " +
    "with optional subsongs.bin/instruments.bin when their metadata counts are zero.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) {
      reason = "AHX modules do not contain directories.";
      return false;
    }

    var name = Path.GetFileName(input.ArchiveName);
    if (name.Equals("FULL.ahx", StringComparison.OrdinalIgnoreCase)
        || name.Equals("FULL.thx", StringComparison.OrdinalIgnoreCase)
        || name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)
        || name.Equals("subsongs.bin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("positions.bin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("tracks.bin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("instruments.bin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("names.bin", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }

    reason = $"'{input.ArchiveName}' is not an AHX structural input; {this.AcceptedInputsDescription}";
    return false;
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    var bytes = ReadAll(input);
    if (!AhxModule.TryRead(bytes, out var module, out _)) {
      stream = null;
      return false;
    }

    var properties = new Dictionary<string, string>(StringComparer.Ordinal) {
      ["Version"] = $"AHX{module!.Version}",
      ["SpeedMultiplier"] = module.SpeedMultiplier.ToString(CultureInfo.InvariantCulture),
      ["TickRateHz"] = module.TickRateHz.ToString(CultureInfo.InvariantCulture),
      ["TrackZeroStorage"] = module.TrackZeroStored ? "Stored" : "Omitted",
      ["Positions"] = module.PositionCount.ToString(CultureInfo.InvariantCulture),
      ["TrackLength"] = module.TrackLength.ToString(CultureInfo.InvariantCulture),
    };
    stream = new AudioEncodedStream(
      new AudioStreamFormat(CodecId, SampleRate: 0, Channels: AhxModule.Channels, BitsPerSample: 0, properties),
      [new AudioPacket(bytes)]);
    return true;
  }

  public IReadOnlyList<string> SupportedMuxCodecs => [CodecId];

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);

    if (!stream.CodecId.Equals(CodecId, StringComparison.OrdinalIgnoreCase)) {
      reason = $"AHX mux accepts codec '{CodecId}', not '{stream.CodecId}'.";
      return false;
    }
    if (stream.Channels != AhxModule.Channels) {
      reason = "AHX is intrinsically a four-channel tracker format.";
      return false;
    }
    if (!TryValidateOptionSyntax(options, out reason))
      return false;

    reason = null;
    return true;
  }

  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new InvalidOperationException(reason);
    if (stream.Packets.Count != 1 || stream.Packets[0].IsHeader)
      throw new InvalidDataException("AHX mux expects exactly one non-header packet containing one complete module.");

    var bytes = stream.Packets[0].Data;
    var module = AhxModule.Read(bytes);
    WriteWithOptions(output, bytes, module, options);
  }

  private readonly record struct Entry(string Name, byte[] Data, string Kind);

  private static List<Entry> Decompose(byte[] bytes) {
    var entries = new List<Entry> { new("FULL.ahx", bytes, "Container") };
    if (!AhxModule.TryRead(bytes, out var module, out var error)) {
      var partial = "[ahx]\nparse_status = partial\nerror = " + Sanitize(error ?? "unknown parse error") + "\n";
      entries.Add(new("metadata.ini", Encoding.UTF8.GetBytes(partial), "Tag"));
      return entries;
    }

    var metadata = new StringBuilder()
      .AppendLine("[ahx]")
      .AppendLine("parse_status = ok")
      .Append("magic = THX\n")
      .Append("version = ").Append(module!.Version).Append('\n')
      .Append("names_offset_stored = ").Append(module.DeclaredNamesOffset).Append('\n')
      .Append("names_offset_actual = ").Append(module.ActualNamesOffset).Append('\n')
      .Append("positions = ").Append(module.PositionCount).Append('\n')
      .Append("restart = ").Append(module.RestartPosition).Append('\n')
      .Append("track_length = ").Append(module.TrackLength).Append('\n')
      .Append("max_track = ").Append(module.MaxTrack).Append('\n')
      .Append("logical_tracks = ").Append(module.MaxTrack + 1).Append('\n')
      .Append("stored_tracks = ").Append(module.StoredTrackCount).Append('\n')
      .Append("num_instruments = ").Append(module.InstrumentCount).Append('\n')
      .Append("subsongs = ").Append(module.SubsongCount).Append('\n')
      .Append("speed_multiplier = ").Append(module.SpeedMultiplier).Append('\n')
      .Append("tick_rate_hz = ").Append(module.TickRateHz).Append('\n')
      .Append("track_zero_stored = ").Append(module.TrackZeroStored ? "true" : "false").Append('\n')
      .Append("title = ").Append(module.Title).Append('\n');

    entries.Add(new("metadata.ini", Encoding.UTF8.GetBytes(metadata.ToString()), "Tag"));
    entries.Add(new("subsongs.bin", module.Subsongs, "Pattern"));
    entries.Add(new("positions.bin", module.Positions, "Pattern"));
    entries.Add(new("tracks.bin", module.LogicalTracks, "Pattern"));
    entries.Add(new("instruments.bin", module.Instruments, "Instrument"));
    entries.Add(new("names.bin", module.Names, "Tag"));
    return entries;
  }

  private static byte[] Rewrite(AhxModule module, FormatCreateOptions options) {
    var version = ResolveVersion(options.GetOption("Version", "Auto"), module.Version);
    var speedMultiplier = ResolveSpeed(options.GetOption("SpeedMultiplier", "Auto"), module.SpeedMultiplier);
    var storeTrackZero = ResolveTrackZero(options.GetOption("TrackZeroStorage", "Auto"), module.TrackZeroStored);
    return module.Write(version, speedMultiplier, storeTrackZero);
  }

  private static void WriteWithOptions(Stream output, byte[] original, AhxModule module, FormatCreateOptions options) {
    if (UsesOnlyAutoOptions(options)) {
      output.Write(original);
      return;
    }
    output.Write(Rewrite(module, options));
  }

  private static bool UsesOnlyAutoOptions(FormatCreateOptions options)
    => options.GetOption("Version", "Auto").Equals("Auto", StringComparison.OrdinalIgnoreCase)
       && options.GetOption("SpeedMultiplier", "Auto").Equals("Auto", StringComparison.OrdinalIgnoreCase)
       && options.GetOption("TrackZeroStorage", "Auto").Equals("Auto", StringComparison.OrdinalIgnoreCase);

  private static byte ResolveVersion(string value, byte fallback)
    => value.ToUpperInvariant() switch {
      "AUTO" => fallback,
      "AHX0" or "0" => 0,
      "AHX1" or "1" => 1,
      _ => throw new InvalidDataException($"Unknown AHX Version option '{value}'."),
    };

  private static int ResolveSpeed(string value, int fallback)
    => value.Equals("Auto", StringComparison.OrdinalIgnoreCase)
      ? fallback
      : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 1 and <= 4
        ? parsed
        : throw new InvalidDataException($"Unknown AHX SpeedMultiplier option '{value}'.");

  private static bool ResolveTrackZero(string value, bool fallback)
    => value.ToUpperInvariant() switch {
      "AUTO" => fallback,
      "STORED" => true,
      "OMITTED" => false,
      _ => throw new InvalidDataException($"Unknown AHX TrackZeroStorage option '{value}'."),
    };

  private static bool TryValidateOptionSyntax(FormatCreateOptions options, out string? reason) {
    try {
      _ = ResolveVersion(options.GetOption("Version", "Auto"), 1);
      _ = ResolveSpeed(options.GetOption("SpeedMultiplier", "Auto"), 1);
      _ = ResolveTrackZero(options.GetOption("TrackZeroStorage", "Auto"), true);
      if (ResolveVersion(options.GetOption("Version", "Auto"), 1) == 0
          && ResolveSpeed(options.GetOption("SpeedMultiplier", "Auto"), 1) != 1) {
        reason = "AHX0 supports only SpeedMultiplier=1.";
        return false;
      }
      reason = null;
      return true;
    } catch (InvalidDataException ex) {
      reason = ex.Message;
      return false;
    }
  }

  private static Dictionary<string, string> ParseMetadata(byte[] data) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var raw in Encoding.UTF8.GetString(data).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
      var line = raw.Trim();
      if (line.Length == 0 || line[0] is '#' or ';' or '[')
        continue;
      var equals = line.IndexOf('=');
      if (equals <= 0)
        continue;
      result[line[..equals].Trim()] = line[(equals + 1)..].Trim();
    }
    return result;
  }

  private static int RequiredInt(IReadOnlyDictionary<string, string> metadata, string key) {
    if (!metadata.TryGetValue(key, out var text)
        || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
      throw new InvalidDataException($"metadata.ini is missing integer key '{key}'.");
    return value;
  }

  private static bool RequiredBool(IReadOnlyDictionary<string, string> metadata, string key) {
    if (!metadata.TryGetValue(key, out var text))
      throw new InvalidDataException($"metadata.ini is missing boolean key '{key}'.");
    if (bool.TryParse(text, out var value))
      return value;
    if (text == "1")
      return true;
    if (text == "0")
      return false;
    throw new InvalidDataException($"metadata.ini key '{key}' must be true/false/1/0.");
  }

  private static ArchiveInputInfo Required(IReadOnlyDictionary<string, ArchiveInputInfo> inputs, string name)
    => inputs.TryGetValue(name, out var input)
      ? input
      : throw new InvalidOperationException($"AHX structural create requires '{name}'.");

  private static ArchiveInputInfo? Optional(IReadOnlyDictionary<string, ArchiveInputInfo> inputs, string name)
    => inputs.GetValueOrDefault(name);

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek)
      stream.Position = 0;
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
  }

  private static string Sanitize(string value)
    => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
