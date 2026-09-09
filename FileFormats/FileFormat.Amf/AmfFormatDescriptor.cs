#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Amf;

/// <summary>
/// DSMI Advanced Module Format (<c>.amf</c>) reader/writer.
/// </summary>
/// <remarks>
/// <para>
/// DSMI AMF is a tracker module, not a linear audio codec. The pseudo-archive view therefore
/// surfaces <c>FULL.amf</c>, <c>metadata.ini</c>, and one playable mono WAV per instrument sample.
/// Creation accepts an existing <c>FULL.amf</c> for byte-exact remuxing, or one or more mono WAVs
/// and builds a deterministic tracker module that triggers every sample once.
/// </para>
/// <para>
/// Supported DSMI versions are 0.1, 0.8, 0.9 and 1.0 through 1.4 (stored version bytes
/// <c>1</c>, <c>8</c> through <c>14</c>). Versions before 0.9 have four fixed channels; 0.9–1.1
/// allow up to 16 channels; 1.2–1.4 allow up to 32. Pattern length is fixed at 64 rows before
/// 1.4. Tempo/speed fields exist from 1.3. Samples are mono unsigned 8-bit PCM with a 16-bit
/// playback rate. Standard 1.0 sample headers are emitted; the known historical truncated-1.0
/// writer variant is accepted only as damaged input and is never generated.
/// </para>
/// <para>
/// The layout follows the DSMI format behaviour documented by the libxmp and OpenMPT loaders:
/// header; channel-remap/panning table; order/pattern track references; sample headers; logical
/// track map; packed 3-byte track events; then unsigned sample data.
/// </para>
/// </remarks>
public sealed class AmfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveCreatable, IArchiveWriteConstraints, IFormatOptionsSchema {

  private const int DefaultRate = 8363;
  private const int DefaultTempo = 125;
  private const int DefaultSpeed = 6;
  private const int DefaultRows = 64;
  private const int DefaultChannels = 4;
  private const int DefaultVolume = 64;
  private const int DefaultNote = 48;

  public string Id => "Amf";
  public string DisplayName => "AMF (DSMI Advanced Module Format)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".amf";
  public IReadOnlyList<string> Extensions => [".amf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("AMF"u8.ToArray(), Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Unsigned 8-bit PCM samples")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description =>
    "DSMI AMF tracker module; versions 0.1/0.8-1.4, full-file remux + mono sample WAV authoring/demux.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Version", "AMF version", FormatOptionKind.Enum, "1.4",
      ["0.1", "0.8", "0.9", "1.0", "1.1", "1.2", "1.3", "1.4"],
      "DSMI module version. 0.1/0.8 have four fixed channels; 0.9-1.1 allow 1-16; 1.2-1.4 allow 1-32. 1.4 adds variable pattern row counts."),
    new("Title", "Module title", FormatOptionKind.String, "CompressionWorkbench",
      Description: "ASCII module title, truncated to 32 bytes."),
    new("Channels", "Tracker channels", FormatOptionKind.Integer, "4",
      Description: "Channels used to schedule imported samples. Fixed at 4 for 0.1/0.8; 1-16 for 0.9-1.1; 1-32 for 1.2-1.4."),
    new("Rows", "Rows per pattern", FormatOptionKind.Integer, "64",
      Description: "Pattern rows. Versions before 1.4 require exactly 64; AMF 1.4 permits 1-256."),
    new("Tempo", "Initial tempo", FormatOptionKind.Integer, "125",
      Description: "Initial BPM byte, 1-255. Stored by AMF 1.3+; earlier versions cannot represent a custom value."),
    new("Speed", "Initial speed", FormatOptionKind.Integer, "6",
      Description: "Initial tracker speed byte, 1-255. Stored by AMF 1.3+; earlier versions cannot represent a custom value."),
    new("Panning", "Channel panning", FormatOptionKind.String, "",
      Description: "AMF 1.1+ comma-separated signed panning values (-64..63) for active channels. Empty uses alternating hard-left/hard-right; unused table slots are centred."),
    new("Volume", "Sample volume", FormatOptionKind.Integer, "64",
      Description: "Default instrument volume, 0-64, used when metadata.ini has no per-sample volume."),
    new("LoopStart", "Sample loop start", FormatOptionKind.Integer, "0",
      Description: "Default loop start in sample frames. A loop is enabled only when LoopEnd is greater than LoopStart; metadata.ini may override it per sample."),
    new("LoopEnd", "Sample loop end", FormatOptionKind.Integer, "0",
      Description: "Default loop end in sample frames. Zero disables looping; metadata.ini may override it per sample."),
    new("Note", "Trigger note", FormatOptionKind.Integer, "48",
      Description: "AMF note byte used by the deterministic generated pattern, 1-126. Every imported sample is triggered once."),
  ];

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "FULL.amf for byte-exact remuxing, or 1-255 mono WAV sample files plus optional metadata.ini";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) {
      reason = "AMF does not accept directory entries.";
      return false;
    }

    var name = Path.GetFileName(input.ArchiveName);
    if (name.Equals("FULL.amf", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }

    reason = $"unsupported AMF input '{input.ArchiveName}'; {AcceptedInputsDescription}";
    return false;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    options ??= new FormatCreateOptions();

    var files = FormatHelpers.FilesOnly(inputs).ToList();
    var full = files.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("FULL.amf", StringComparison.OrdinalIgnoreCase));
    if (full.Data is not null) {
      output.Write(full.Data);
      return;
    }

    var wavs = files
      .Where(static file => file.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .ToList();
    if (wavs.Count is < 1 or > byte.MaxValue)
      throw new InvalidOperationException("AMF creation requires between 1 and 255 mono WAV samples.");

    var metadata = files.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("metadata.ini", StringComparison.OrdinalIgnoreCase));
    var metadataValues = metadata.Data is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      : ParseMetadata(metadata.Data);

    var version = ParseVersion(options.GetOption("Version", "1.4"));
    var channels = options.GetOptionInt("Channels", DefaultChannels);
    ValidateChannels(version, channels, options.HasOption("Channels"));
    if (version < 9) channels = DefaultChannels;

    var configuredRows = options.GetOptionInt("Rows", DefaultRows);
    if (version < 14 && configuredRows != DefaultRows)
      throw new NotSupportedException($"AMF {DisplayVersion(version)} has fixed 64-row patterns.");
    if (version >= 14 && configuredRows is < 1 or > 256)
      throw new ArgumentOutOfRangeException(nameof(options), "AMF 1.4 Rows must be in the range 1..256.");
    var rows = version >= 14 ? configuredRows : DefaultRows;

    var tempo = options.GetOptionInt("Tempo", DefaultTempo);
    var speed = options.GetOptionInt("Speed", DefaultSpeed);
    if (tempo is < 1 or > byte.MaxValue || speed is < 1 or > byte.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(options), "AMF Tempo and Speed must be in the range 1..255.");
    if (version < 13 && ((options.HasOption("Tempo") && tempo != DefaultTempo) ||
                         (options.HasOption("Speed") && speed != DefaultSpeed)))
      throw new NotSupportedException($"AMF {DisplayVersion(version)} does not store initial tempo/speed fields.");

    var note = options.GetOptionInt("Note", DefaultNote);
    if (note is < 1 or > 126)
      throw new ArgumentOutOfRangeException(nameof(options), "AMF Note must be in the range 1..126.");
    var defaultVolume = options.GetOptionInt("Volume", DefaultVolume);
    if (defaultVolume is < 0 or > 64)
      throw new ArgumentOutOfRangeException(nameof(options), "AMF sample Volume must be in the range 0..64.");
    var defaultLoopStart = options.GetOptionInt("LoopStart", 0);
    var defaultLoopEnd = options.GetOptionInt("LoopEnd", 0);
    if (defaultLoopStart < 0 || defaultLoopEnd < 0)
      throw new ArgumentOutOfRangeException(nameof(options), "AMF loop points cannot be negative.");

    var samples = new List<WriteSample>(wavs.Count);
    for (var index = 0; index < wavs.Count; ++index) {
      var input = wavs[index];
      var wav = new WavReader().ReadCanonicalPcm(input.Data);
      if (wav.NumChannels != 1)
        throw new NotSupportedException($"AMF sample '{input.Name}' is {wav.NumChannels}-channel; DSMI AMF samples are mono.");
      if (wav.FormatCode != 1 || wav.BitsPerSample is not (8 or 16 or 24 or 32))
        throw new NotSupportedException($"AMF sample '{input.Name}' must decode to 8/16/24/32-bit integer PCM.");
      if (wav.SampleRate is < 1 or > ushort.MaxValue)
        throw new NotSupportedException($"AMF sample '{input.Name}' rate {wav.SampleRate} Hz does not fit the 16-bit DSMI rate field.");

      var pcm = wav.BitsPerSample == 8
        ? wav.InterleavedPcm
        : PcmCodec.Requantize(wav.InterleavedPcm, wav.BitsPerSample, 8);
      var maxLength = version >= 10 ? uint.MaxValue : ushort.MaxValue;
      if ((ulong)pcm.LongLength > maxLength)
        throw new NotSupportedException(
          $"AMF {DisplayVersion(version)} sample '{input.Name}' has {pcm.LongLength} frames; maximum is {maxLength}.");

      var ordinal = index + 1;
      var baseName = Path.GetFileNameWithoutExtension(input.Name);
      var sampleName = GetMetadata(metadataValues, ordinal, "name", baseName);
      var dosName = GetMetadata(metadataValues, ordinal, "filename", Path.GetFileName(input.Name));
      var volume = GetMetadataInt(metadataValues, ordinal, "volume", defaultVolume);
      var loopStart = GetMetadataInt(metadataValues, ordinal, "loop_start", defaultLoopStart);
      var loopEnd = GetMetadataInt(metadataValues, ordinal, "loop_end", defaultLoopEnd);
      if (volume is < 0 or > 64)
        throw new InvalidDataException($"metadata.ini sample {ordinal} volume must be 0..64.");
      if (loopStart < 0 || loopEnd < 0 || loopStart > pcm.Length || loopEnd > pcm.Length)
        throw new InvalidDataException($"metadata.ini sample {ordinal} loop points must lie inside the sample.");
      if (loopEnd != 0 && loopEnd <= loopStart)
        throw new InvalidDataException($"metadata.ini sample {ordinal} LoopEnd must exceed LoopStart, or be zero to disable looping.");

      samples.Add(new WriteSample(
        sampleName,
        dosName,
        pcm,
        checked((ushort)wav.SampleRate),
        checked((byte)volume),
        checked((uint)loopStart),
        checked((uint)loopEnd)));
    }

    WriteModule(
      output,
      version,
      options.GetOption("Title", GetGlobalMetadata(metadataValues, "title", "CompressionWorkbench")),
      channels,
      rows,
      checked((byte)tempo),
      checked((byte)speed),
      ParsePanning(options.GetOption("Panning", string.Empty), version, channels),
      checked((byte)note),
      samples);
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Parse(ms.ToArray());
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> Parse(byte[] blob) {
    var entries = new List<AudioPseudoArchive.Entry> { new("FULL.amf", "Container", blob) };
    var info = new StringBuilder().AppendLine("format=AMF");

    try {
      var parsed = ParseModule(blob);
      info.AppendLine("parsed=true");
      info.AppendLine($"version={DisplayVersion(parsed.Version)}");
      info.AppendLine($"version_byte={parsed.Version}");
      info.AppendLine($"title={parsed.Title}");
      info.AppendLine($"num_samples={parsed.Samples.Count}");
      info.AppendLine($"num_orders={parsed.NumOrders}");
      info.AppendLine($"num_tracks={parsed.NumLogicalTracks}");
      info.AppendLine($"num_channels={parsed.NumChannels}");
      if (parsed.Version >= 13) {
        info.AppendLine($"tempo={parsed.Tempo}");
        info.AppendLine($"speed={parsed.Speed}");
      }
      if (parsed.Panning.Count > 0)
        info.AppendLine($"panning={string.Join(',', parsed.Panning.Take(parsed.NumChannels))}");
      for (var i = 0; i < parsed.PatternRows.Count; ++i)
        info.AppendLine($"pattern.{i + 1:D3}.rows={parsed.PatternRows[i]}");

      var sampleDataOffset = parsed.SampleDataOffset;
      var samplesWithData = 0;
      for (var index = 0; index < parsed.Samples.Count; ++index) {
        var sample = parsed.Samples[index];
        info.AppendLine($"sample.{index + 1:D3}.name={sample.Name}");
        info.AppendLine($"sample.{index + 1:D3}.filename={sample.FileName}");
        info.AppendLine($"sample.{index + 1:D3}.index={sample.Index}");
        info.AppendLine($"sample.{index + 1:D3}.length={sample.Length}");
        info.AppendLine($"sample.{index + 1:D3}.rate={sample.C2Spd}");
        info.AppendLine($"sample.{index + 1:D3}.volume={sample.Volume}");
        info.AppendLine($"sample.{index + 1:D3}.loop_start={sample.LoopStart}");
        info.AppendLine($"sample.{index + 1:D3}.loop_end={sample.LoopEnd}");

        if (sample.Length == 0) continue;
        if (sampleDataOffset + sample.Length > (ulong)blob.LongLength)
          throw new InvalidDataException($"AMF sample {index + 1} runs past end of file.");
        if (sample.Length > int.MaxValue)
          throw new InvalidDataException($"AMF sample {index + 1} is too large for in-memory extraction.");

        var pcm = blob.AsSpan(checked((int)sampleDataOffset), checked((int)sample.Length)).ToArray();
        sampleDataOffset += sample.Length;
        var rate = sample.C2Spd == 0 ? DefaultRate : sample.C2Spd;
        var label = string.IsNullOrWhiteSpace(sample.Name) ? "sample" : SanitizeFileName(sample.Name);
        entries.Add(new(
          $"samples/{index + 1:D2}_{label}.wav",
          "Sample",
          PcmCodec.ToWavBlob(pcm, channels: 1, rate, bitsPerSample: 8),
          "stored"));
        ++samplesWithData;
      }

      info.AppendLine($"samples_with_data={samplesWithData}");
      info.AppendLine("sample_8bit_encoding=unsigned");
    } catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentOutOfRangeException) {
      info.AppendLine("parsed=false");
      info.AppendLine($"error={SanitizeMetadata(ex.Message)}");
    }

    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
    return entries;
  }

  private static ParsedModule ParseModule(ReadOnlySpan<byte> blob) {
    if (blob.Length < 40 || !blob[..3].SequenceEqual("AMF"u8))
      throw new InvalidDataException("Missing DSMI AMF signature or truncated header.");

    var version = blob[3];
    if (!IsSupportedVersion(version))
      throw new InvalidDataException($"Unsupported DSMI AMF version byte {version}.");

    var offset = 4;
    var title = ReadAsciiTrim(blob, offset, 32);
    offset += 32;
    Require(blob, offset, 4, "core header");
    var numSamples = blob[offset++];
    var numOrders = blob[offset++];
    var numLogicalTracks = BinaryPrimitives.ReadUInt16LittleEndian(blob[offset..]);
    offset += 2;
    var numChannels = version >= 9 ? ReadByte(blob, ref offset, "channel count") : DefaultChannels;
    if (numSamples == 0 || numOrders == 0 || numLogicalTracks == 0)
      throw new InvalidDataException("AMF sample, order and logical-track counts must be non-zero.");
    ValidateChannels(version, numChannels, explicitlyRequested: true);

    var panning = new List<sbyte>();
    if (version is 9 or 10) {
      Require(blob, offset, 16, "channel-remap table");
      offset += 16;
    } else if (version >= 11) {
      var tableLength = version >= 12 ? 32 : 16;
      Require(blob, offset, tableLength, "panning table");
      for (var i = 0; i < tableLength; ++i)
        panning.Add(unchecked((sbyte)blob[offset + i]));
      offset += tableLength;
    }

    byte tempo = DefaultTempo, speed = DefaultSpeed;
    if (version >= 13) {
      tempo = ReadByte(blob, ref offset, "initial tempo");
      speed = ReadByte(blob, ref offset, "initial speed");
    }

    var rows = new List<int>(numOrders);
    for (var order = 0; order < numOrders; ++order) {
      var patternRows = version >= 14 ? ReadUInt16(blob, ref offset, "pattern row count") : DefaultRows;
      if (patternRows is < 1 or > 256)
        throw new InvalidDataException($"AMF pattern {order} declares invalid row count {patternRows}.");
      rows.Add(patternRows);
      Require(blob, offset, checked(numChannels * 2), "pattern track references");
      offset += numChannels * 2;
    }

    var samples = new List<ParsedSample>(numSamples);
    for (var sampleIndex = 0; sampleIndex < numSamples; ++sampleIndex) {
      var headerSize = version >= 10 ? 65 : 59;
      Require(blob, offset, headerSize, $"sample header {sampleIndex + 1}");
      var type = blob[offset];
      var name = ReadAsciiTrim(blob, offset + 1, 32);
      var fileName = ReadAsciiTrim(blob, offset + 33, 13);
      var dataIndex = BinaryPrimitives.ReadUInt32LittleEndian(blob[(offset + 46)..]);
      ulong length;
      ushort c2spd;
      byte volume;
      ulong loopStart;
      ulong loopEnd;
      if (version >= 10) {
        length = BinaryPrimitives.ReadUInt32LittleEndian(blob[(offset + 50)..]);
        c2spd = BinaryPrimitives.ReadUInt16LittleEndian(blob[(offset + 54)..]);
        volume = blob[offset + 56];
        loopStart = BinaryPrimitives.ReadUInt32LittleEndian(blob[(offset + 57)..]);
        loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(blob[(offset + 61)..]);
      } else {
        length = BinaryPrimitives.ReadUInt16LittleEndian(blob[(offset + 50)..]);
        c2spd = BinaryPrimitives.ReadUInt16LittleEndian(blob[(offset + 52)..]);
        volume = blob[offset + 54];
        loopStart = BinaryPrimitives.ReadUInt16LittleEndian(blob[(offset + 55)..]);
        loopEnd = BinaryPrimitives.ReadUInt16LittleEndian(blob[(offset + 57)..]);
        if (loopEnd == ushort.MaxValue) loopEnd = 0;
      }
      if (type > 1 || volume > 64 || loopStart > length || (loopEnd != 0 && loopEnd > length))
        throw new InvalidDataException($"AMF sample header {sampleIndex + 1} contains invalid bounds or flags.");
      samples.Add(new ParsedSample(name, fileName, dataIndex, length, c2spd, volume, loopStart, loopEnd));
      offset += headerSize;
    }

    Require(blob, offset, checked(numLogicalTracks * 2), "logical track map");
    var physicalTracks = 0;
    for (var logical = 0; logical < numLogicalTracks; ++logical) {
      physicalTracks = Math.Max(physicalTracks, BinaryPrimitives.ReadUInt16LittleEndian(blob[offset..]));
      offset += 2;
    }

    for (var track = 0; track < physicalTracks; ++track) {
      Require(blob, offset, 3, $"track {track + 1} header");
      var eventCount = blob[offset] | (blob[offset + 1] << 8) | (blob[offset + 2] << 16);
      offset += 3;
      // AMF 0.1 stores the number of real events and omits the terminator from that
      // count, but a zero-count track contains no terminator either (libxmp's size!=0 rule).
      if (version == 1 && eventCount > 0) ++eventCount;
      var trackBytes = checked(eventCount * 3);
      Require(blob, offset, trackBytes, $"track {track + 1} events");
      offset += trackBytes;
    }

    return new ParsedModule(version, title, numOrders, numLogicalTracks, numChannels, tempo, speed, panning, rows, samples, checked((ulong)offset));
  }

  private static void WriteModule(
    Stream output,
    byte version,
    string title,
    int channels,
    int rows,
    byte tempo,
    byte speed,
    IReadOnlyList<sbyte> panning,
    byte note,
    IReadOnlyList<WriteSample> samples) {
    var samplesPerPattern = checked(channels * rows);
    var numOrders = (samples.Count + samplesPerPattern - 1) / samplesPerPattern;
    if (numOrders is < 1 or > byte.MaxValue)
      throw new NotSupportedException("Requested AMF layout requires more than 255 patterns/orders.");
    var logicalTrackCount = checked(numOrders * channels);
    if (logicalTrackCount > ushort.MaxValue)
      throw new NotSupportedException("Requested AMF layout requires more than 65535 logical tracks.");

    output.Write("AMF"u8);
    output.WriteByte(version);
    WriteAsciiFixed(output, title, 32);
    output.WriteByte(checked((byte)samples.Count));
    output.WriteByte(checked((byte)numOrders));
    WriteUInt16(output, checked((ushort)logicalTrackCount));
    if (version >= 9) output.WriteByte(checked((byte)channels));

    if (version is 9 or 10) {
      Span<byte> remap = stackalloc byte[16];
      for (var i = 0; i < remap.Length; ++i) remap[i] = checked((byte)i);
      output.Write(remap);
    } else if (version >= 11) {
      var tableLength = version >= 12 ? 32 : 16;
      Span<byte> pan = stackalloc byte[32];
      for (var i = 0; i < tableLength; ++i)
        pan[i] = unchecked((byte)(i < panning.Count ? panning[i] : 0));
      output.Write(pan[..tableLength]);
    }

    if (version >= 13) {
      output.WriteByte(tempo);
      output.WriteByte(speed);
    }

    for (var order = 0; order < numOrders; ++order) {
      if (version >= 14) WriteUInt16(output, checked((ushort)rows));
      for (var channel = 0; channel < channels; ++channel) {
        var logicalTrack = checked((ushort)(order * channels + channel + 1));
        WriteUInt16(output, logicalTrack);
      }
    }

    for (var index = 0; index < samples.Count; ++index)
      WriteSampleHeader(output, version, index, samples[index]);

    for (var logical = 1; logical <= logicalTrackCount; ++logical)
      WriteUInt16(output, checked((ushort)logical));

    for (var order = 0; order < numOrders; ++order) {
      for (var channel = 0; channel < channels; ++channel) {
        var events = new List<TrackEvent>();
        for (var row = 0; row < rows; ++row) {
          var sampleIndex = order * samplesPerPattern + row * channels + channel;
          if (sampleIndex >= samples.Count) break;
          events.Add(new TrackEvent(checked((byte)row), 0x80, checked((byte)sampleIndex)));
          events.Add(new TrackEvent(checked((byte)row), note, 0xFF));
        }
        WriteTrack(output, version, events);
      }
    }

    foreach (var sample in samples)
      output.Write(sample.Pcm);
  }

  private static void WriteSampleHeader(Stream output, byte version, int index, WriteSample sample) {
    var loopEnabled = sample.LoopEnd > sample.LoopStart;
    output.WriteByte(loopEnabled ? (byte)1 : (byte)0);
    WriteAsciiFixed(output, sample.Name, 32);
    WriteAsciiFixed(output, sample.FileName, 13);
    WriteUInt32(output, checked((uint)(index + 1)));
    if (version >= 10) {
      WriteUInt32(output, checked((uint)sample.Pcm.LongLength));
      WriteUInt16(output, sample.C2Spd);
      output.WriteByte(sample.Volume);
      WriteUInt32(output, sample.LoopStart);
      WriteUInt32(output, sample.LoopEnd);
    } else {
      WriteUInt16(output, checked((ushort)sample.Pcm.Length));
      WriteUInt16(output, sample.C2Spd);
      output.WriteByte(sample.Volume);
      WriteUInt16(output, checked((ushort)sample.LoopStart));
      WriteUInt16(output, loopEnabled ? checked((ushort)sample.LoopEnd) : ushort.MaxValue);
    }
  }

  private static void WriteTrack(Stream output, byte version, IReadOnlyList<TrackEvent> events) {
    var storedCount = version == 1 ? events.Count : checked(events.Count + 1);
    if (storedCount > ushort.MaxValue)
      throw new NotSupportedException("AMF track has too many packed events for interoperable DSMI readers.");
    output.WriteByte((byte)(storedCount & 0xFF));
    output.WriteByte((byte)((storedCount >> 8) & 0xFF));
    output.WriteByte(0);
    foreach (var item in events) {
      output.WriteByte(item.Row);
      output.WriteByte(item.Command);
      output.WriteByte(item.Value);
    }

    // Version 0.1's size excludes the terminator, and the historical reader rule
    // only adds that terminator back for non-empty tracks. Emitting one after a
    // zero-sized track shifts every following track header and ultimately sample PCM.
    if (version != 1 || events.Count != 0)
      output.Write([0xFF, 0xFF, 0xFF]);
  }

  private static byte ParseVersion(string value) => value.Trim() switch {
    "0.1" or "1" => 1,
    "0.8" or "8" => 8,
    "0.9" or "9" => 9,
    "1.0" or "10" => 10,
    "1.1" or "11" => 11,
    "1.2" or "12" => 12,
    "1.3" or "13" => 13,
    "1.4" or "14" => 14,
    _ => throw new ArgumentException($"Unsupported DSMI AMF Version '{value}'.")
  };

  private static bool IsSupportedVersion(byte version) => version is 1 or >= 8 and <= 14;

  private static string DisplayVersion(byte version)
    => version == 1 ? "0.1" : $"{version / 10}.{version % 10}";

  private static void ValidateChannels(byte version, int channels, bool explicitlyRequested) {
    if (version < 9) {
      if (explicitlyRequested && channels != DefaultChannels)
        throw new NotSupportedException($"AMF {DisplayVersion(version)} has exactly four channels.");
      return;
    }
    var max = version < 12 ? 16 : 32;
    if (channels is < 1 || channels > max)
      throw new ArgumentOutOfRangeException(nameof(channels),
        $"AMF {DisplayVersion(version)} supports 1..{max} channels.");
  }

  private static IReadOnlyList<sbyte> ParsePanning(string text, byte version, int channels) {
    if (string.IsNullOrWhiteSpace(text)) {
      if (version < 11) return [];
      var defaults = new sbyte[channels];
      for (var i = 0; i < defaults.Length; ++i) defaults[i] = i % 2 == 0 ? (sbyte)-64 : (sbyte)63;
      return defaults;
    }
    if (version < 11)
      throw new NotSupportedException($"AMF {DisplayVersion(version)} has no explicit panning table.");

    var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != channels)
      throw new ArgumentException($"AMF Panning must contain exactly {channels} comma-separated values.");
    var result = new sbyte[channels];
    for (var i = 0; i < parts.Length; ++i) {
      if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value is < -64 or > 63)
        throw new ArgumentException("AMF Panning values must be signed integers in the range -64..63.");
      result[i] = checked((sbyte)value);
    }
    return result;
  }

  private static Dictionary<string, string> ParseMetadata(byte[] data) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var rawLine in Encoding.UTF8.GetString(data).Split('\n')) {
      var line = rawLine.Trim();
      if (line.Length == 0 || line[0] is '#' or ';') continue;
      var equals = line.IndexOf('=');
      if (equals <= 0) continue;
      result[line[..equals].Trim()] = line[(equals + 1)..].Trim();
    }
    return result;
  }

  private static string GetGlobalMetadata(IReadOnlyDictionary<string, string> metadata, string key, string fallback)
    => metadata.TryGetValue(key, out var value) ? value : fallback;

  private static string GetMetadata(IReadOnlyDictionary<string, string> metadata, int ordinal, string key, string fallback)
    => metadata.TryGetValue($"sample.{ordinal:D3}.{key}", out var value) ? value : fallback;

  private static int GetMetadataInt(IReadOnlyDictionary<string, string> metadata, int ordinal, string key, int fallback) {
    var text = GetMetadata(metadata, ordinal, key, string.Empty);
    return string.IsNullOrEmpty(text) ? fallback
      : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value
      : throw new InvalidDataException($"metadata.ini sample {ordinal} '{key}' is not an integer.");
  }

  private static byte ReadByte(ReadOnlySpan<byte> blob, ref int offset, string what) {
    Require(blob, offset, 1, what);
    return blob[offset++];
  }

  private static ushort ReadUInt16(ReadOnlySpan<byte> blob, ref int offset, string what) {
    Require(blob, offset, 2, what);
    var value = BinaryPrimitives.ReadUInt16LittleEndian(blob[offset..]);
    offset += 2;
    return value;
  }

  private static void Require(ReadOnlySpan<byte> blob, int offset, int length, string what) {
    if (offset < 0 || length < 0 || (long)offset + length > blob.Length)
      throw new InvalidDataException($"Truncated AMF {what}.");
  }

  private static string ReadAsciiTrim(ReadOnlySpan<byte> blob, int offset, int length) {
    Require(blob, offset, length, "text field");
    var field = blob.Slice(offset, length);
    var zero = field.IndexOf((byte)0);
    if (zero >= 0) field = field[..zero];
    Span<char> chars = stackalloc char[field.Length];
    var count = 0;
    foreach (var b in field)
      if (b is >= 0x20 and < 0x7F) chars[count++] = (char)b;
    return new string(chars[..count]).Trim();
  }

  private static string SanitizeFileName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    var value = sb.ToString().Trim('.');
    return value.Length == 0 ? "sample" : value;
  }

  private static string SanitizeMetadata(string text)
    => text.Replace('\r', ' ').Replace('\n', ' ').Replace('=', ':');

  private static void WriteAsciiFixed(Stream output, string value, int length) {
    Span<byte> field = length <= 64 ? stackalloc byte[length] : new byte[length];
    var take = Math.Min(value.Length, length);
    for (var i = 0; i < take; ++i) {
      var c = value[i];
      field[i] = c is >= ' ' and <= '~' ? (byte)c : (byte)'_';
    }
    output.Write(field);
  }

  private static void WriteUInt16(Stream output, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteUInt32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private readonly record struct TrackEvent(byte Row, byte Command, byte Value);
  private sealed record WriteSample(
    string Name,
    string FileName,
    byte[] Pcm,
    ushort C2Spd,
    byte Volume,
    uint LoopStart,
    uint LoopEnd);
  private sealed record ParsedSample(
    string Name,
    string FileName,
    uint Index,
    ulong Length,
    ushort C2Spd,
    byte Volume,
    ulong LoopStart,
    ulong LoopEnd);
  private sealed record ParsedModule(
    byte Version,
    string Title,
    int NumOrders,
    int NumLogicalTracks,
    int NumChannels,
    byte Tempo,
    byte Speed,
    IReadOnlyList<sbyte> Panning,
    IReadOnlyList<int> PatternRows,
    IReadOnlyList<ParsedSample> Samples,
    ulong SampleDataOffset);
}
