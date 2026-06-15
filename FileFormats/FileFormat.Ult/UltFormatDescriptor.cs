#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Ult;

/// <summary>
/// Exposes an UltraTracker (MAS_UTrack) module as a pseudo-archive of <c>FULL.ult</c>
/// (Kind <c>Container</c>), a <c>metadata.ini</c> (Kind <c>Tag</c>) and one playable WAV
/// per sample (Kind <c>Sample</c>).
/// </summary>
/// <remarks>
/// PRAGMATIC SCOPE: only the header up to the sample-descriptor table is parsed
/// (<c>char[15]</c> magic+version, <c>char[32]</c> title, <c>u8</c> message-line count +
/// message, <c>u8</c> numSamples, then V004 sample headers: <c>char[32]</c> name,
/// <c>char[12]</c> DOS name, <c>u32</c> loopStart, <c>u32</c> loopEnd, <c>u32</c> sizeStart,
/// <c>u32</c> sizeEnd, <c>u8</c> volume, <c>u8</c> flags, <c>u16</c> speed, <c>s16</c> finetune).
/// Sample byte length = <c>(sizeEnd - sizeStart)</c> × (bits/8) where bit&#160;2 of flags marks
/// 16-bit. Rather than walk the order/pattern tables, the sample PCM is taken from the END
/// of the file: the last Σ(byte length) bytes, sliced in descriptor order. Per-sample rate
/// is not surfaced by this view, so 8363 Hz is assumed (documented in metadata).
/// </remarks>
public sealed class UltFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Ult";
  public string DisplayName => "UltraTracker";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ult";
  public IReadOnlyList<string> Extensions => [".ult"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MAS_UTrack_V00"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "UltraTracker module; full file + playable 8/16-bit sample WAVs.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    return Parse(blob);
  }

  private const int AssumedSampleRate = 8363;
  private const int SampleHeaderSize = 64; // 32+12+4+4+4+4+1+1+2 = 64.

  private readonly record struct SampleInfo(string Name, long ByteLength, bool Is16Bit, byte Volume);

  private static IReadOnlyList<AudioPseudoArchive.Entry> Parse(byte[] blob) {
    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.ult", "Container", blob),
    };

    if (blob.Length < 48 || Encoding.ASCII.GetString(blob, 0, 14) != "MAS_UTrack_V00")
      return entries;

    var version = (char)blob[14];
    var samples = new List<SampleInfo>();

    try {
      var pos = 15;                       // after magic+version
      var title = ReadAsciiTrim(blob, pos, 32);
      pos += 32;
      var messageLines = blob[pos];
      ++pos;
      pos += messageLines * 32;           // song message
      if (pos >= blob.Length) throw new InvalidDataException("truncated header");
      var numSamples = blob[pos];
      ++pos;

      for (var s = 0; s < numSamples; ++s) {
        if (pos + SampleHeaderSize > blob.Length) throw new InvalidDataException("truncated sample header");
        var name = ReadAsciiTrim(blob, pos, 32);
        var sizeStart = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + 52, 4));
        var sizeEnd = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + 56, 4));
        var vol = blob[pos + 60];
        var flags = blob[pos + 61];
        var is16 = (flags & 0x04) != 0;
        var sampleCount = sizeEnd >= sizeStart ? sizeEnd - sizeStart : 0;
        var byteLen = (long)sampleCount * (is16 ? 2 : 1);
        samples.Add(new SampleInfo(name, byteLen, is16, vol));
        pos += SampleHeaderSize;
      }

      // Sample PCM is the trailing Σ(byteLen) bytes of the file, in descriptor order.
      var totalBytes = samples.Sum(s => s.ByteLength);
      var dataStart = blob.LongLength - totalBytes;
      if (totalBytes > 0 && dataStart >= 0) {
        var cursor = dataStart;
        for (var s = 0; s < samples.Count; ++s) {
          var si = samples[s];
          if (si.ByteLength <= 0) continue;
          var take = (int)Math.Min(si.ByteLength, blob.LongLength - cursor);
          if (take <= 0) break;
          var raw = blob.AsSpan((int)cursor, take);
          var bits = si.Is16Bit ? 16 : 8;
          // 8-bit signed → unsigned WAV; 16-bit signed stays as-is (little-endian).
          var pcm = si.Is16Bit ? raw.ToArray() : ToUnsigned8(raw);
          var label = string.IsNullOrWhiteSpace(si.Name) ? "sample" : SanitizeFileName(si.Name);
          entries.Add(new($"samples/{(s + 1):D2}_{label}.wav", "Sample",
            PcmCodec.ToWavBlob(pcm, 1, AssumedSampleRate, bits)));
          cursor += si.ByteLength;
        }
      }
    } catch {
      // Graceful FULL-only fallback.
    }

    var info = new StringBuilder();
    info.AppendLine($"format=MAS_UTrack_V00{version}");
    info.AppendLine($"sample_count={samples.Count}");
    info.AppendLine($"sample_rate_assumed={AssumedSampleRate} (ULT view carries no per-sample rate)");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static byte[] ToUnsigned8(ReadOnlySpan<byte> signed) {
    var r = new byte[signed.Length];
    for (var i = 0; i < signed.Length; ++i)
      r[i] = unchecked((byte)(signed[i] + 128));
    return r;
  }

  private static string ReadAsciiTrim(byte[] blob, int offset, int length) {
    var end = Math.Min(offset + length, blob.Length);
    var sb = new StringBuilder();
    for (var i = offset; i < end; ++i) {
      var b = blob[i];
      if (b == 0) break;
      if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
    }
    return sb.ToString().Trim();
  }

  private static string SanitizeFileName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    var s = sb.ToString().Trim('.');
    return s.Length == 0 ? "sample" : s;
  }
}
