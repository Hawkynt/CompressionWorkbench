#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Sphere;

/// <summary>
/// Exposes a NIST SPHERE (<c>.sph</c>) speech file as an archive of
/// <c>FULL.sph</c>, one mono WAV per channel (after decoding μ-law or byte-swapping
/// big-endian linear PCM), and a <c>metadata.ini</c> carrying every parsed header
/// field. Compressed codings (<c>embedded-shorten</c>, <c>embedded-wavpack</c>) and
/// any unrecognised coding are surfaced as <c>FULL.sph</c> only — no channel entries,
/// no throw.
/// </summary>
public sealed class SphereFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Sphere";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "NIST SPHERE";
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
  public string DefaultExtension => ".sph";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".sph", ".nist"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("NIST_1A"u8.ToArray(), Confidence: 0.95),
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
  public string Description => "NIST SPHERE (.sph) speech; μ-law / linear PCM decoded to per-channel WAV.";

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

  // ── IArchiveCreatable: assemble a SPHERE from per-channel mono WAVs ───────────

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.sph", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelOrder(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("SPHERE archive create needs either FULL.sph or one or more per-channel WAVs.");

    var channels = channelBlobs.Select(b => new WavReader().Read(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");
    if (channels.Any(c => c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");
    if (first.BitsPerSample != 16)
      throw new InvalidOperationException("SPHERE create writes 16-bit PCM; channel WAVs must be 16-bit.");

    var interleaved = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), first.BitsPerSample);
    var blob = new SphereWriter().Write(interleaved, channels.Count, first.SampleRate, first.BitsPerSample);
    output.Write(blob);
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
    "SPHERE archive accepts: FULL.sph, LEFT/RIGHT/CENTER/… .wav (per-channel), metadata.ini";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.sph" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null; return true;
    }
    reason = $"not a SPHERE-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new SphereReader().Read(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.sph", "Container", blob),
    };

    var (pcm, bits) = DecodeToPcm(parsed);
    if (pcm != null && bits is 8 or 16 && parsed.ChannelCount >= 1) {
      foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
          pcm, parsed.ChannelCount, parsed.SampleRate, bits))
        entries.Add(new($"{name}.wav", "Channel", wavBlob, "pcm"));
    }

    var info = new StringBuilder();
    foreach (var (name, value) in parsed.Fields)
      info.AppendLine($"{name}={value}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  /// <summary>
  /// Decodes SPHERE sample data to interleaved little-endian PCM. Returns
  /// <c>(null, 0)</c> for compressed (<c>embedded-shorten</c>/<c>embedded-wavpack</c>)
  /// or otherwise unsupported codings — those surface as FULL-only.
  /// </summary>
  private static (byte[]? pcm, int bits) DecodeToPcm(SphereReader.ParsedSphere p) {
    var coding = p.SampleCoding.Trim().ToLowerInvariant();
    if (coding.Contains("shorten") || coding.Contains("wavpack"))
      return (null, 0);

    if (coding.StartsWith("ulaw") || coding.StartsWith("mu-law") || coding == "alaw") {
      if (coding == "alaw") {
        var aDecoded = Codec.ALaw.ALawCodec.Decode(p.SampleData);
        return (ShortsToLePcm(aDecoded), 16);
      }
      var decoded = Codec.MuLaw.MuLawCodec.Decode(p.SampleData);
      return (ShortsToLePcm(decoded), 16);
    }

    if (!coding.StartsWith("pcm"))
      return (null, 0);

    switch (p.SampleNBytes) {
      case 1:
        // SPHERE 8-bit linear PCM: pass through as unsigned 8-bit.
        return ((byte[])p.SampleData.Clone(), 8);
      case 2: {
        // 10 → big-endian on disk, swap to little-endian; 01 → already little-endian.
        var bigEndian = p.SampleByteFormat.Trim() == "10";
        return (bigEndian ? SwapSampleEndianness(p.SampleData, 2) : (byte[])p.SampleData.Clone(), 16);
      }
      default:
        return (null, 0);
    }
  }

  private static byte[] SwapSampleEndianness(byte[] pcm, int bytesPerSample) {
    if (bytesPerSample <= 1) return (byte[])pcm.Clone();
    var swapped = new byte[pcm.Length];
    for (var i = 0; i + bytesPerSample <= pcm.Length; i += bytesPerSample)
      for (var j = 0; j < bytesPerSample; ++j)
        swapped[i + j] = pcm[i + bytesPerSample - 1 - j];
    return swapped;
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
