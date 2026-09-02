#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.F669;

/// <summary>
/// Exposes a Composer 669 module as a pseudo-archive of <c>FULL.669</c> (Kind
/// <c>Container</c>), a <c>metadata.ini</c> (Kind <c>Tag</c>),
/// <c>patterns/pattern_NN.bin</c> (Kind <c>Pattern</c>) and one playable WAV per sample
/// (Kind <c>Sample</c>).
/// </summary>
/// <remarks>
/// The magic (<c>if</c> = 0x69 0x66, or <c>JN</c> for extended) is weak, so the 0x1F1-byte
/// header is validated structurally — sample headers and pattern blocks must fit and the
/// declared sample-data lengths must sum to no more than the remaining file — before the
/// format is accepted. Composer 669 sample data is 8-bit signed PCM, rebiased to WAV's
/// unsigned 8-bit. No per-sample rate is stored, so 8363 Hz is assumed.
/// </remarks>
public sealed class F669FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "F669";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Composer 669";
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
public string DefaultExtension => ".669";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".669"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x69, 0x66], Offset: 0, Confidence: 0.5), // "if"
    new("JN"u8.ToArray(), Offset: 0, Confidence: 0.5),
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
public string Description => "Composer 669 tracker module; full file + patterns + playable 8-bit sample WAVs.";

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
  private const int HeaderSize = 0x1F1;     // 497
  private const int SampleHeaderSize = 25;  // char[13] name | u32 length | u32 loopStart | u32 loopEnd
  private const int PatternSize = 1536;

  private static bool IsMagic(byte[] blob)
    => blob.Length >= 2 &&
       ((blob[0] == 0x69 && blob[1] == 0x66) || (blob[0] == (byte)'J' && blob[1] == (byte)'N'));

  private static IReadOnlyList<AudioPseudoArchive.Entry> Parse(byte[] blob) {
    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.669", "Container", blob),
    };

    if (blob.Length < HeaderSize || !IsMagic(blob))
      return entries;

    var sampleCountWritten = 0;
    var patternCountWritten = 0;
    var numSamples = blob[111];
    var numPatterns = blob[112];

    try {
      if (numSamples > 64) throw new InvalidDataException("numSamples out of range");

      var sampleHdrStart = HeaderSize;
      var patternStart = sampleHdrStart + numSamples * SampleHeaderSize;
      var dataStart = patternStart + numPatterns * PatternSize;

      // Structural validation: header table + patterns + summed sample data must fit.
      if (dataStart > blob.Length) throw new InvalidDataException("tables exceed file size");

      var lengths = new int[numSamples];
      var names = new string[numSamples];
      long totalData = 0;
      for (var s = 0; s < numSamples; ++s) {
        var off = sampleHdrStart + s * SampleHeaderSize;
        names[s] = ReadAsciiTrim(blob, off, 13);
        lengths[s] = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 13, 4)));
        totalData += lengths[s];
      }
      if (dataStart + totalData > blob.Length)
        throw new InvalidDataException("sample data lengths exceed file size");

      for (var p = 0; p < numPatterns; ++p) {
        var off = patternStart + p * PatternSize;
        var pat = blob.AsSpan(off, PatternSize).ToArray();
        entries.Add(new($"patterns/pattern_{p:D2}.bin", "Pattern", pat));
        ++patternCountWritten;
      }

      var cursor = dataStart;
      for (var s = 0; s < numSamples; ++s) {
        var len = lengths[s];
        if (len <= 0) continue;
        var take = Math.Min(len, blob.Length - cursor);
        if (take <= 0) break;
        var pcm = ToUnsigned8(blob.AsSpan(cursor, take));
        var label = string.IsNullOrWhiteSpace(names[s]) ? "sample" : SanitizeFileName(names[s]);
        entries.Add(new($"samples/{(s + 1):D2}_{label}.wav", "Sample",
          PcmCodec.ToWavBlob(pcm, 1, AssumedSampleRate, 8)));
        ++sampleCountWritten;
        cursor += len;
      }
    } catch {
      // Graceful FULL-only fallback.
    }

    var info = new StringBuilder();
    info.AppendLine("format=Composer 669");
    info.AppendLine($"num_samples={numSamples}");
    info.AppendLine($"num_patterns={numPatterns}");
    info.AppendLine($"sample_count={sampleCountWritten}");
    info.AppendLine($"pattern_count={patternCountWritten}");
    info.AppendLine($"sample_rate_assumed={AssumedSampleRate} (669 carries no per-sample rate)");
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
