#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Au;

/// <summary>
/// Exposes a Sun/NeXT <c>.au</c> / <c>.snd</c> file as an archive of
/// <c>FULL.au</c>, one WAV per channel (after decoding μ-law/A-law/PCM,
/// G.721 (G.726 @ 32 kbit/s), G.723 3-bit (G.726 @ 24 kbit/s) and 5-bit
/// (G.726 @ 40 kbit/s) ADPCM, and G.722 sub-band ADPCM), and
/// a <c>metadata.ini</c> carrying the encoding type, sample rate and any
/// annotation string.
/// </summary>
public sealed class AuFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Au";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Sun/NeXT .au (.snd)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".au";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".au", ".snd"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x2E, 0x73, 0x6E, 0x64], Confidence: 0.90),
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
  public string Description => ".au (Sun / NeXT) audio; μ-law / A-law / PCM decoded to per-channel WAV.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    ".au archive accepts: FULL.au, LEFT/RIGHT/… .wav (per-channel), metadata.ini";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.au" or "full.snd" or "metadata.ini" ||
        name.EndsWith(".wav")) {
      reason = null; return true;
    }
    reason = $"not a .au-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── IArchiveCreatable: assemble a multi-channel .au from per-channel mono WAVs ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f => {
      var n = Path.GetFileName(f.Name).ToLowerInvariant();
      return n is "full.au" or "full.snd";
    });
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelOrder(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException(".au archive create needs either FULL.au or one or more per-channel WAVs.");

    var channels = channelBlobs.Select(b => new WavReader().ReadCanonicalPcm(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");
    if (channels.Any(c => c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");

    // .au stores big-endian PCM; the per-channel WAVs are little-endian.
    var interleavedLe = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), first.BitsPerSample);
    var interleavedBe = SwapSampleEndianness(interleavedLe, first.BitsPerSample / 8);

    var blob = new AuWriter().Write(interleavedBe, channels.Count, first.SampleRate, first.BitsPerSample);
    output.Write(blob);
  }

  /// <summary>Reverses the byte order within each fixed-width sample (LE↔BE).</summary>
  private static byte[] SwapSampleEndianness(byte[] pcm, int bytesPerSample) {
    if (bytesPerSample <= 1) return pcm;
    var swapped = new byte[pcm.Length];
    for (var i = 0; i + bytesPerSample <= pcm.Length; i += bytesPerSample)
      for (var j = 0; j < bytesPerSample; ++j)
        swapped[i + j] = pcm[i + bytesPerSample - 1 - j];
    return swapped;
  }

  // Canonical speaker ordering (FFmpeg/WAVE bit order, mono through 22.2).
  private static int ChannelOrder(string name) => ChannelLayout.OrderIndex(name);

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new AuReader().Read(blob);

    var entries = new List<(string, string, byte[])> {
      ("FULL.au", "Container", blob),
    };

    var (pcm, bitsOut) = DecodeToPcm(parsed);
    if (pcm != null && bitsOut is 8 or 16 or 24 or 32 && parsed.NumChannels >= 1) {
      if (parsed.NumChannels == 1) {
        entries.Add(("MONO.wav", "Channel", PcmCodec.ToWavBlob(pcm, 1, parsed.SampleRate, bitsOut, formatCode: 1)));
      } else {
        foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
            pcm, parsed.NumChannels, parsed.SampleRate, bitsOut))
          entries.Add(($"{name}.wav", "Channel", wavBlob));
      }
    }

    var info = new StringBuilder();
    info.AppendLine($"encoding={parsed.Encoding} ({EncodingName(parsed.Encoding)})");
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"channels={parsed.NumChannels}");
    if (!string.IsNullOrEmpty(parsed.Annotation))
      info.AppendLine($"annotation={parsed.Annotation}");
    entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static (byte[]? pcm, int bits) DecodeToPcm(AuReader.ParsedAu p) {
    switch (p.Encoding) {
      case 1: { // μ-law
        var decoded = Codec.MuLaw.MuLawCodec.Decode(p.SoundData);
        return (ShortsToLePcm(decoded), 16);
      }
      case 27: { // A-law
        var decoded = Codec.ALaw.ALawCodec.Decode(p.SoundData);
        return (ShortsToLePcm(decoded), 16);
      }
      case 2: return ((byte[])p.SoundData.Clone(), 8);         // 8-bit signed PCM
      case 3: return (ConvertBeToLe(p.SoundData, 2), 16);       // 16-bit BE PCM
      case 4: return (ConvertBeToLe(p.SoundData, 3), 24);       // 24-bit BE PCM
      case 5: return (ConvertBeToLe(p.SoundData, 4), 32);       // 32-bit BE PCM
      case 23: { // G.721 (G.726 @ 32 kbit/s) 4-bit ADPCM
        var decoded = Codec.G72x.G72xCodec.DecodeG721(p.SoundData);
        return (ShortsToLePcm(decoded), 16);
      }
      case 24: { // G.722 sub-band ADPCM (decodes to 16 kHz linear)
        var decoded = Codec.G722.G722Codec.Decode(p.SoundData);
        return (ShortsToLePcm(decoded), 16);
      }
      case 25: { // G.723 3-bit (G.726 @ 24 kbit/s) ADPCM
        var decoded = Codec.G72x.G72xCodec.DecodeG726(p.SoundData, 3);
        return (ShortsToLePcm(decoded), 16);
      }
      case 26: { // G.723 5-bit (G.726 @ 40 kbit/s) ADPCM
        var decoded = Codec.G72x.G72xCodec.DecodeG726(p.SoundData, 5);
        return (ShortsToLePcm(decoded), 16);
      }
      default: return (null, 0);                                 // float: not decoded
    }
  }

  private static byte[] ConvertBeToLe(byte[] be, int bytesPerSample) {
    if (bytesPerSample <= 1) return (byte[])be.Clone();
    var le = new byte[be.Length];
    for (var i = 0; i + bytesPerSample <= be.Length; i += bytesPerSample)
      for (var j = 0; j < bytesPerSample; ++j)
        le[i + j] = be[i + bytesPerSample - 1 - j];
    return le;
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static string EncodingName(uint e) => e switch {
    1 => "8-bit G.711 μ-law",
    2 => "8-bit linear PCM",
    3 => "16-bit linear PCM (BE)",
    4 => "24-bit linear PCM (BE)",
    5 => "32-bit linear PCM (BE)",
    6 => "32-bit IEEE float",
    7 => "64-bit IEEE float",
    23 => "G.721 4-bit ADPCM",
    24 => "G.722 ADPCM",
    25 => "G.723 3-bit ADPCM",
    26 => "G.723 5-bit ADPCM",
    27 => "8-bit G.711 A-law",
    _ => $"unknown ({e})",
  };
}
