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
  public IReadOnlyList<string> SupportedEncodeCodecs => EncodeCodecs;

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);
    ArgumentNullException.ThrowIfNull(options);
    reason = null;
    if (!EncodeCodecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"AKB cannot encode codec '{codecId}'.";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "AKB encoders accept signed PCM16 input.";
      return false;
    }
    if (format.SampleRate is <= 0 or > ushort.MaxValue || format.Channels is <= 0 or > byte.MaxValue) {
      reason = "AKB material headers require sample rates 1..65535 and channels 1..255.";
      return false;
    }

    try {
      var kind = ResolveVariant(options, codecId, null);
      var version = ResolveClassicVersion(options, codecId, null);
      var encrypt = ResolveEncrypt(options, null);
      var normalized = NormalizeCodec(codecId);
      if (normalized == "pcm16le" && kind != AkbContainerKind.Akb2)
        throw new NotSupportedException("PCM16LE is a documented AKB2 profile, not a production classic-AKB profile.");
      if (normalized == "ms-adpcm") {
        if (format.Channels is not (1 or 2))
          throw new NotSupportedException("Managed MS-ADPCM encoding supports mono or stereo.");
        if (kind == AkbContainerKind.Classic && version == 0)
          throw new NotSupportedException("Classic-v0 has no documented MS-ADPCM extradata profile.");
      }
      if (normalized == "vorbis" && format.Channels > 8)
        throw new NotSupportedException("The managed Vorbis encoder supports up to eight channels in this package.");
      if (normalized == "aac") {
        if (kind != AkbContainerKind.Classic)
          throw new NotSupportedException("M4A/AAC is documented for classic AKB, not AKB2.");
        if (format.Channels is not (1 or 2))
          throw new NotSupportedException("The managed AAC-LC encoder supports mono or stereo.");
        if (Array.IndexOf(AacAdtsReader.SampleRateTable, format.SampleRate) is < 0 or > 12)
          throw new NotSupportedException("AAC sample rate is not an ADTS standard rate.");
      }
      if (encrypt && (kind != AkbContainerKind.Classic || version < 3 || normalized != "vorbis"))
        throw new NotSupportedException("AKB XOR encryption is documented only for classic-v3 Ogg Vorbis.");
      _ = ReadLoopOptions(options, null, sampleCount: 0, validateAgainstCount: false);
      if (normalized == "ms-adpcm") {
        var blockAlign = options.GetOptionInt("BlockAlign", 512);
        if (blockAlign < 7 * format.Channels)
          throw new ArgumentOutOfRangeException(nameof(options), "MS-ADPCM BlockAlign is smaller than its channel header.");
      }
      return true;
    } catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException or OverflowException) {
      reason = ex.Message;
      return false;
    }
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(pcm);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);
    if (pcm.InterleavedData.LongLength % pcm.Format.BytesPerFrame != 0)
      throw new InvalidDataException("PCM byte count is not an integral number of frames.");
    if (pcm.FrameCount > uint.MaxValue)
      throw new NotSupportedException("AKB sample-count fields are 32-bit.");

    var sampleCount = checked((uint)pcm.FrameCount);
    var loops = ReadLoopOptions(options, null, sampleCount, validateAgainstCount: true);
    var normalized = NormalizeCodec(codecId);
    var codec = normalized switch {
      "pcm16le" => AkbCodec.Pcm16Le,
      "ms-adpcm" => AkbCodec.MsAdpcm,
      "vorbis" => AkbCodec.OggVorbis,
      "aac" => AkbCodec.M4aAac,
      _ => throw new NotSupportedException(),
    };

    var blockAlign = normalized == "ms-adpcm" ? options.GetOptionInt("BlockAlign", 512) : 0;
    var encoded = normalized switch {
      "pcm16le" => pcm.InterleavedData.ToArray(),
      "ms-adpcm" => MsAdpcmCodec.Encode(ToShorts(pcm.InterleavedData), pcm.Format.Channels, blockAlign),
      "vorbis" => EncodeVorbis(pcm, options, loops),
      "aac" => EncodeM4a(pcm, options),
      _ => throw new NotSupportedException(),
    };

    var kind = ResolveVariant(options, codecId, null);
    var version = ResolveClassicVersion(options, codecId, null);
    var encrypt = ResolveEncrypt(options, null);
    var entry = new AkbWriteEntry(
      $"entry_000{AkbReader.Extension(codec)}", encoded, codec,
      pcm.Format.SampleRate, pcm.Format.Channels, sampleCount,
      loops.Start, loops.End, loops.Start2, loops.End2, blockAlign);
    using var writer = new AkbWriter(output, leaveOpen: true) {
      ContainerKind = kind,
      ClassicVersion = version,
      Encrypt = encrypt,
    };
    writer.AddEncodedEntry(entry);
    writer.Write();
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    using var reader = new AkbReader(input, leaveOpen: true);
    if (reader.Entries.Count != 1)
      throw new NotSupportedException("The PCM conversion interface requires a single-material AKB; extract a specific AKB2 material first.");
    var entry = reader.Entries[0];
    var payload = reader.Extract(entry);
    byte[] pcm = entry.Codec switch {
      AkbCodec.Pcm16Le => payload,
      AkbCodec.MsAdpcm => DecodeMsAdpcm(payload, entry),
      AkbCodec.OggVorbis => DecodeVorbis(payload),
      AkbCodec.M4aAac => DecodeM4aAac(payload, entry),
      _ => throw new NotSupportedException($"Unsupported AKB codec {entry.Codec}."),
    };
    pcm = TrimPcm(pcm, entry.SampleCount, entry.Channels);
    return new AudioPcmBuffer(new AudioPcmFormat(entry.SampleRate, entry.Channels, 16), pcm);
  }

}
