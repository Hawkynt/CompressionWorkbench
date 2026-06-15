#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.WsAdpcm;
using Compression.Registry;

namespace FileFormat.Apc;

/// <summary>
/// CRYO APC (<c>.apc</c>) audio — the IMA-ADPCM voice/effect format of CRYO Interactive
/// titles. The little-endian header is
/// <c>"CRYO_APC" (8) | version (4, e.g. "1.20") | u32 sampleCount | u32 sampleRate |
/// u32 leftInitialSample | u32 rightInitialSample | u32 stereoFlag</c>, followed by raw
/// IMA nibbles (low nibble first). The two "initial sample" fields seed the IMA
/// predictor(s) (step index starts at 0); for stereo, nibbles interleave per channel
/// — low nibble left, high nibble right — each driving its own continuous predictor.
/// <para>
/// Surfaced as a read-only pseudo-archive: <c>FULL.apc</c> (Container), one mono
/// <c>MONO.wav</c> or <c>LEFT.wav</c>/<c>RIGHT.wav</c> (Channel) and <c>metadata.ini</c>
/// (Tag). Decoding uses the continuous IMA state machine in <see cref="StandardImaCodec"/>.
/// </para>
/// </summary>
public sealed class ApcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  public string Id => "Apc";
  public string DisplayName => "CRYO APC";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".apc";
  public IReadOnlyList<string> Extensions => [".apc"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // "CRYO" at 0, "_APC" at 4.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("CRYO"u8.ToArray(), Confidence: 0.9),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ima-adpcm", "IMA-ADPCM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "CRYO APC; IMA-ADPCM, full file + decoded WAV channels.";

  private const int HeaderSize = 32;

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── parsing ─────────────────────────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = ParseApc(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.apc", "Container", blob),
    };

    var pcm = ShortsToLePcm(parsed.Samples);
    if (parsed.Channels == 1) {
      entries.Add(new("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(pcm, channels: 1, parsed.SampleRate, bitsPerSample: 16, formatCode: 1), "ima-adpcm"));
    } else {
      foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, parsed.Channels, parsed.SampleRate, 16))
        entries.Add(new($"{name}.wav", "Channel", wav, "ima-adpcm"));
    }

    var info = new StringBuilder();
    info.AppendLine($"version={parsed.Version}");
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"channels={parsed.Channels}");
    info.AppendLine($"sample_count={parsed.SampleCount}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private readonly record struct ParsedApc(
    string Version, int SampleRate, int Channels, uint SampleCount, short[] Samples);

  private static ParsedApc ParseApc(byte[] blob) {
    if (blob.Length < HeaderSize)
      throw new InvalidDataException("APC too short for 32-byte header.");
    if (!blob.AsSpan(0, 8).SequenceEqual("CRYO_APC"u8))
      throw new InvalidDataException("Missing CRYO_APC magic.");

    var version = Encoding.ASCII.GetString(blob.AsSpan(8, 4)).TrimEnd('\0', ' ');
    var sampleCount = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(12));
    var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(16));
    var leftInit = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(20));
    var rightInit = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(24));
    var stereoFlag = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(28));
    var channels = stereoFlag != 0 ? 2 : 1;

    var data = blob.AsSpan(HeaderSize);
    var left = new StandardImaCodec.State(leftInit, 0);

    short[] samples;
    if (channels == 1) {
      samples = StandardImaCodec.Decode(data, ref left);
    } else {
      // Stereo: each byte carries one left nibble (low) and one right nibble (high);
      // every byte advances both predictors once. Output is interleaved L,R.
      var right = new StandardImaCodec.State(rightInit, 0);
      samples = new short[data.Length * 2];
      var o = 0;
      foreach (var b in data) {
        samples[o++] = StandardImaCodec.DecodeOneNibble((byte)(b & 0x0F), ref left);
        samples[o++] = StandardImaCodec.DecodeOneNibble((byte)(b >> 4), ref right);
      }
    }

    return new ParsedApc(version, sampleRate <= 0 ? 22050 : sampleRate, channels, sampleCount, samples);
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
