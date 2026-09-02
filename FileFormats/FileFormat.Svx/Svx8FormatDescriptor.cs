#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Svx;

/// <summary>
/// Exposes an Amiga IFF / 8SVX file as an archive of <c>FULL.8svx</c> plus one mono
/// WAV per channel (the 8-bit signed PCM is decoded — Fibonacci-delta when needed —
/// and rebiased to WAV's unsigned 8-bit), plus a <c>metadata.ini</c> and any text
/// chunks (<c>NAME</c>, <c>ANNO</c>, …). Mono surfaces as <c>MONO.wav</c>; stereo
/// surfaces as <c>LEFT.wav</c> / <c>RIGHT.wav</c> by de-planarising the BODY halves.
/// </summary>
public sealed class Svx8FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Svx8";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "IFF/8SVX (Amiga)";
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
public string DefaultExtension => ".8svx";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".8svx", ".svx"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];

  // FORM at offset 0 is shared with AIFF's generic IFF registration; the form type
  // at offset 8 ("8SVX") is the discriminator that identifies this format.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("8SVX"u8.ToArray(), Offset: 8, Confidence: 0.90),
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
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "IFF/8SVX (Amiga 8-bit voice); full file + per-channel WAV (Fibonacci-delta decoded).";

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

  // ── IArchiveCreatable: assemble an uncompressed 8SVX from per-channel mono WAVs ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // Passthrough a provided FULL.8svx verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.8svx", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => {
        var name = Path.GetFileName(f.Name);
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("FULL.8svx", StringComparison.OrdinalIgnoreCase);
      })
      .OrderBy(f => ChannelOrder(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count is 0 or > 2)
      throw new InvalidOperationException("8SVX archive create needs FULL.8svx or one (mono) / two (stereo) per-channel WAVs.");

    var channels = channelBlobs.Select(b => new WavReader().Read(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");
    if (channels.Any(c => c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");
    if (first.BitsPerSample is not (8 or 16))
      throw new InvalidOperationException("8SVX assembly accepts 8-bit or 16-bit mono WAVs.");

    // 8SVX stores signed 8-bit PCM. WAV 8-bit is unsigned (subtract 128); 16-bit is
    // signed, so its high byte already is the signed top octave (samples truncated).
    var halves = channels.Select(c => ToSigned8(c.InterleavedPcm, c.BitsPerSample)).ToList();
    var blob = new SvxWriter().Write(halves, first.SampleRate);
    output.Write(blob);
  }

  /// <summary>WAV PCM → signed 8-bit: rebias unsigned 8-bit, or truncate 16-bit to its signed top byte.</summary>
  private static byte[] ToSigned8(byte[] pcm, int bitsPerSample) {
    if (bitsPerSample == 8) {
      var s = new byte[pcm.Length];
      for (var i = 0; i < pcm.Length; ++i)
        s[i] = unchecked((byte)(pcm[i] - 128));
      return s;
    }
    // 16-bit little-endian signed → take the signed high byte of each sample.
    var samples = pcm.Length / 2;
    var r = new byte[samples];
    for (var i = 0; i < samples; ++i)
      r[i] = pcm[i * 2 + 1];
    return r;
  }

  // Canonical speaker ordering (FFmpeg/WAVE bit order, mono through 22.2).
  private static int ChannelOrder(string name) => ChannelLayout.OrderIndex(name);

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "8SVX archive accepts: FULL.8svx, MONO/LEFT/RIGHT .wav (per-channel)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.8svx" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not an 8SVX-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new SvxReader().Read(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.8svx", "Container", blob),
    };

    // Decode the BODY to signed 8-bit PCM, then rebias to WAV's unsigned 8-bit.
    var signed = parsed.Compression == SvxReader.CompressionFibonacci
      ? SvxReader.DecodeFibonacciDelta(parsed.Body)
      : parsed.Body;
    var rate = parsed.SampleRate > 0 ? parsed.SampleRate : 8000;

    if (signed.Length > 0) {
      if (parsed.Channels == SvxReader.ChannelStereo && signed.Length % 2 == 0) {
        var half = signed.Length / 2;
        var left = ToUnsigned8(signed.AsSpan(0, half));
        var right = ToUnsigned8(signed.AsSpan(half, half));
        entries.Add(new("LEFT.wav", "Channel", PcmCodec.ToWavBlob(left, 1, rate, 8)));
        entries.Add(new("RIGHT.wav", "Channel", PcmCodec.ToWavBlob(right, 1, rate, 8)));
      } else {
        var mono = ToUnsigned8(signed);
        entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(mono, 1, rate, 8)));
      }
    }

    var info = new StringBuilder();
    info.AppendLine($"sample_rate={rate}");
    info.AppendLine($"octaves={parsed.Octaves}");
    info.AppendLine($"compression={parsed.Compression} ({CompressionName(parsed.Compression)})");
    info.AppendLine($"channels={ChannelsName(parsed.Channels)}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    foreach (var (id, text) in parsed.Tags)
      entries.Add(new($"metadata/{id.Trim()}.txt", "Tag", Encoding.UTF8.GetBytes(text)));

    return entries;
  }

  /// <summary>Signed 8-bit PCM → WAV's unsigned 8-bit (add 128).</summary>
  private static byte[] ToUnsigned8(ReadOnlySpan<byte> signed) {
    var r = new byte[signed.Length];
    for (var i = 0; i < signed.Length; ++i)
      r[i] = unchecked((byte)(signed[i] + 128));
    return r;
  }

  private static string CompressionName(int c) => c switch {
    SvxReader.CompressionNone => "none (8-bit signed PCM)",
    SvxReader.CompressionFibonacci => "Fibonacci-delta",
    _ => $"unknown ({c})",
  };

  private static string ChannelsName(int c) => c switch {
    SvxReader.ChannelLeft => "left",
    SvxReader.ChannelRight => "right",
    SvxReader.ChannelStereo => "stereo",
    _ => $"unknown ({c})",
  };
}
