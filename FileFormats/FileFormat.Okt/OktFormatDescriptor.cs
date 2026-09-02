#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Okt;

/// <summary>
/// Exposes an Oktalyzer (OKTASONG) module as a pseudo-archive of <c>FULL.okt</c>
/// (Kind <c>Container</c>), a <c>metadata.ini</c> (Kind <c>Tag</c>),
/// <c>patterns/pattern_NN.bin</c> from each <c>PBOD</c> chunk (Kind <c>Pattern</c>),
/// and one playable WAV per <c>SBOD</c> sample body (Kind <c>Sample</c>).
/// </summary>
/// <remarks>
/// The container is a sequence of IFF-style chunks (4CC + big-endian u32 length + body)
/// directly after the 8-byte <c>OKTASONG</c> magic, WITHOUT a FORM wrapper. <c>SAMP</c>
/// holds 36-byte sample descriptors; each <c>SBOD</c> body is the 8-bit signed PCM for the
/// next non-zero-length descriptor in <c>SAMP</c> order. No per-sample rate is stored, so
/// 8363 Hz is assumed (documented in metadata).
/// </remarks>
public sealed class OktFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Okt";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Oktalyzer";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Audio;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".okt";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".okt"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("OKTASONG"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Classic;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Oktalyzer tracker module; full file + patterns + playable 8-bit sample WAVs.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

    /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    return Parse(blob);
  }

  private const int AssumedSampleRate = 8363;

  private readonly record struct SampleDesc(string Name, long Length, byte Volume);

  private static IReadOnlyList<AudioPseudoArchive.Entry> Parse(byte[] blob) {
    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.okt", "Container", blob),
    };

    if (blob.Length < 8 || Encoding.ASCII.GetString(blob, 0, 8) != "OKTASONG")
      return entries;

    var sampleDescs = new List<SampleDesc>();
    var sbodIndex = 0;
    var patternIndex = 0;
    var sampleEntryIndex = 0;

    try {
      var pos = 8;
      while (pos + 8 <= blob.Length) {
        var id = Encoding.ASCII.GetString(blob, pos, 4);
        var len = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(pos + 4, 4)));
        var body = pos + 8;
        if (len < 0 || body + len > blob.Length) break;

        switch (id) {
          case "SAMP":
            // 36-byte descriptors: char[20] name | u32 length | u16 repStart | u16 repLen | u8 pad | u8 volume | u16 pad.
            for (var o = 0; o + 36 <= len; o += 36) {
              var dOff = body + o;
              var name = ReadAsciiTrim(blob, dOff, 20);
              var sampLen = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(dOff + 20, 4));
              var vol = blob[dOff + 28];
              sampleDescs.Add(new SampleDesc(name, sampLen, vol));
            }
            break;
          case "PBOD":
            var pat = blob.AsSpan(body, len).ToArray();
            entries.Add(new($"patterns/pattern_{patternIndex:D2}.bin", "Pattern", pat));
            ++patternIndex;
            break;
          case "SBOD":
            // Map this body onto the next non-zero-length SAMP descriptor.
            while (sbodIndex < sampleDescs.Count && sampleDescs[sbodIndex].Length == 0)
              ++sbodIndex;
            var desc = sbodIndex < sampleDescs.Count ? sampleDescs[sbodIndex] : default;
            ++sbodIndex;
            ++sampleEntryIndex;
            var pcm = ToUnsigned8(blob.AsSpan(body, len));
            var label = string.IsNullOrWhiteSpace(desc.Name) ? "sample" : SanitizeFileName(desc.Name);
            entries.Add(new($"samples/{sampleEntryIndex:D2}_{label}.wav", "Sample",
              PcmCodec.ToWavBlob(pcm, 1, AssumedSampleRate, 8)));
            break;
        }
        pos = body + len;
      }
    } catch {
      // Graceful FULL-only fallback.
    }

    var info = new StringBuilder();
    info.AppendLine("format=OKTASONG");
    info.AppendLine($"sample_descriptor_count={sampleDescs.Count}");
    info.AppendLine($"sample_count={sampleEntryIndex}");
    info.AppendLine($"pattern_count={patternIndex}");
    info.AppendLine($"sample_rate_assumed={AssumedSampleRate} (OKT carries no per-sample rate)");
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
