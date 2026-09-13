#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using Codec.Aac;
using Codec.MsAdpcm;
using Codec.Vorbis;
using Compression.Registry;
using FileFormat.Mp4;

namespace FileFormat.Akb;

public sealed partial class AkbFormatDescriptor {
  public IReadOnlyList<string> SupportedMuxCodecs => MuxCodecs;

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    stream = null;
    try {
      using var reader = new AkbReader(input, leaveOpen: true);
      if (reader.Entries.Count != 1)
        return false;
      var entry = reader.Entries[0];
      var properties = BuildStreamProperties(reader.ContainerKind, entry);
      var codecId = entry.Codec switch {
        AkbCodec.Pcm16Le => "pcm16le",
        AkbCodec.MsAdpcm => "ms-adpcm",
        AkbCodec.OggVorbis => "ogg-vorbis",
        AkbCodec.M4aAac => "m4a-aac",
        _ => null,
      };
      if (codecId is null)
        return false;
      var bits = entry.Codec switch {
        AkbCodec.Pcm16Le => 16,
        AkbCodec.MsAdpcm => 4,
        _ => 0,
      };
      stream = new AudioEncodedStream(
        new AudioStreamFormat(codecId, entry.SampleRate, entry.Channels, bits, properties),
        [new AudioPacket(reader.Extract(entry), entry.SampleCount)]);
      return true;
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException) {
      return false;
    }
  }

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    reason = null;
    if (!MuxCodecs.Contains(stream.CodecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"AKB cannot mux codec '{stream.CodecId}'.";
      return false;
    }
    if (stream.SampleRate is <= 0 or > ushort.MaxValue || stream.Channels is <= 0 or > byte.MaxValue) {
      reason = "AKB material headers require sample rates 1..65535 and channels 1..255.";
      return false;
    }
    try {
      var normalized = NormalizeCodec(stream.CodecId);
      var kind = ResolveVariant(options, normalized, stream.Properties);
      var version = ResolveClassicVersion(options, normalized, stream.Properties);
      var encrypt = ResolveEncrypt(options, stream.Properties);
      if (normalized == "pcm16le" && kind != AkbContainerKind.Akb2)
        throw new NotSupportedException("PCM16LE can be muxed only into AKB2.");
      if (normalized == "ms-adpcm") {
        if (stream.Channels is not (1 or 2))
          throw new NotSupportedException("MS-ADPCM AKB materials support mono/stereo in the managed codec path.");
        if (kind == AkbContainerKind.Classic && version == 0)
          throw new NotSupportedException("Classic-v0 has no documented MS-ADPCM extradata profile.");
        var align = ResolveInt(options, stream.Properties, "BlockAlign", 0);
        if (align < 7 * stream.Channels)
          throw new InvalidDataException("MS-ADPCM remux requires a valid BlockAlign stream property/option.");
      }
      if ((normalized is "m4a-aac" or "aac") && kind != AkbContainerKind.Classic)
        throw new NotSupportedException("M4A/AAC is documented only for classic AKB.");
      if (encrypt && (kind != AkbContainerKind.Classic || version < 3 || normalized != "ogg-vorbis"))
        throw new NotSupportedException("AKB XOR encryption is documented only for classic-v3 Ogg Vorbis.");
      _ = ReadLoopOptions(options, stream.Properties, 0, validateAgainstCount: false);
      return true;
    } catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException or OverflowException) {
      reason = ex.Message;
      return false;
    }
  }

  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);

    var normalized = NormalizeCodec(stream.Format.CodecId);
    byte[] payload;
    AkbCodec codec;
    if (normalized == "aac") {
      if (stream.CodecPrivateData is not { Length: >= 2 })
        throw new InvalidDataException("Raw AAC remux needs AudioSpecificConfig codec-private data so an M4A can be authored.");
      using var m4a = new MemoryStream();
      new Mp4FormatDescriptor().Mux(m4a, stream, options);
      payload = m4a.ToArray();
      codec = AkbCodec.M4aAac;
    } else {
      payload = ConcatenatePackets(stream.Packets);
      codec = normalized switch {
        "pcm16le" => AkbCodec.Pcm16Le,
        "ms-adpcm" => AkbCodec.MsAdpcm,
        "ogg-vorbis" => AkbCodec.OggVorbis,
        "m4a-aac" => AkbCodec.M4aAac,
        _ => throw new NotSupportedException(),
      };
    }

    var sampleCount = ResolveSampleCount(stream);
    var loops = ReadLoopOptions(options, stream.Format.Properties, sampleCount, validateAgainstCount: sampleCount != 0);
    var blockAlign = codec == AkbCodec.MsAdpcm
      ? ResolveInt(options, stream.Format.Properties, "BlockAlign", 0)
      : 0;
    var kind = ResolveVariant(options, normalized, stream.Format.Properties);
    var version = ResolveClassicVersion(options, normalized, stream.Format.Properties);
    var encrypt = ResolveEncrypt(options, stream.Format.Properties);

    var entry = new AkbWriteEntry(
      $"entry_000{AkbReader.Extension(codec)}", payload, codec,
      stream.Format.SampleRate, stream.Format.Channels, sampleCount,
      loops.Start, loops.End, loops.Start2, loops.End2, blockAlign);
    using var writer = new AkbWriter(output, leaveOpen: true) {
      ContainerKind = kind,
      ClassicVersion = version,
      Encrypt = encrypt,
    };
    writer.AddEncodedEntry(entry);
    writer.Write();
  }

  public void Remux(Stream source, Stream output, IReadOnlyList<ArchiveInputInfo> replacements, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(replacements);
    ArgumentNullException.ThrowIfNull(options);

    using var reader = new AkbReader(source, leaveOpen: true);
    var replacementMap = replacements
      .Where(static input => !input.IsDirectory)
      .ToDictionary(static input => input.ArchiveName, static input => input.ReadContent(), StringComparer.OrdinalIgnoreCase);
    var codecForSelection = reader.Entries.Count == 1 ? CodecId(reader.Entries[0].Codec) : "pcm16le";
    var sourceProperties = BuildContainerProperties(reader);
    var kind = ResolveVariant(options, codecForSelection, sourceProperties);
    var version = ResolveClassicVersion(options, codecForSelection, sourceProperties);
    var encrypt = ResolveEncrypt(options, sourceProperties);
    if (kind == AkbContainerKind.Classic && reader.Entries.Count != 1)
      throw new NotSupportedException("Cannot collapse a multi-material AKB2 into classic AKB without selecting a material.");

    using var writer = new AkbWriter(output, leaveOpen: true) {
      ContainerKind = kind,
      ClassicVersion = version,
      Encrypt = encrypt,
    };
    foreach (var entry in reader.Entries) {
      var payload = replacementMap.Remove(entry.Name, out var replacement) ? replacement : reader.Extract(entry);
      writer.AddEncodedEntry(new AkbWriteEntry(
        entry.Name, payload, entry.Codec, entry.SampleRate, entry.Channels,
        entry.SampleCount, entry.LoopStart, entry.LoopEnd,
        entry.AlternateLoopStart, entry.AlternateLoopEnd, entry.BlockAlign,
        entry.Flags & 0x07));
    }
    if (replacementMap.Count != 0)
      throw new FileNotFoundException($"AKB replacement entry not found: {replacementMap.Keys.First()}");
    writer.Write();
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    using var rebuilt = new MemoryStream();
    this.Remux(archive, rebuilt, [], new FormatCreateOptions());
    if (!archive.CanWrite || !archive.CanSeek)
      throw new NotSupportedException("AKB defragmentation requires a seekable writable stream.");
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Position = 0;
  }

}
