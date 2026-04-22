#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Aiff;

/// <summary>
/// Exposes an AIFF / AIFC file as an archive of <c>FULL.aif</c>, one <c>LEFT.wav</c>/
/// <c>RIGHT.wav</c>/… per channel, plus <c>metadata/annotations.txt</c> and
/// <c>metadata/markers.bin</c>. Compressed AIFC payloads are decoded to linear PCM
/// before being split per channel (μ-law, A-law, and <c>fl32</c>/<c>fl64</c> IEEE
/// float are supported; <c>ima4</c> and <c>GSM</c> are recognised but passed
/// through as raw bytes in the <c>FULL.aif</c> entry only).
/// </summary>
public sealed class AiffFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints {

  public string Id => "Aiff";
  public string DisplayName => "AIFF / AIFC (Apple audio)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".aif";
  public IReadOnlyList<string> Extensions => [".aif", ".aiff", ".aifc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("FORM"u8.ToArray(), Confidence: 0.55),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "AIFF / AIFC audio; full file + per-channel PCM + markers + annotations.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "AIFF archive accepts: FULL.aif, LEFT/RIGHT/… .wav (per-channel), metadata/*.txt|bin";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";
    if (dir == "" && (name.EndsWith(".aif") || name.EndsWith(".aiff") || name.EndsWith(".aifc") || name.EndsWith(".wav"))) {
      reason = null; return true;
    }
    if (dir == "metadata") { reason = null; return true; }
    reason = $"not an AIFF-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new AiffReader().Read(blob);

    var entries = new List<(string, string, byte[])> {
      ("FULL.aif", "Track", blob),
    };

    // Decode to linear PCM (LE) if possible.
    var pcm = DecodeToPcm(parsed, out var bitsOut);
    if (pcm != null && bitsOut is 8 or 16 or 24 or 32 && parsed.NumChannels >= 1) {
      if (parsed.NumChannels == 1) {
        entries.Add(("MONO.wav", "Channel", PcmCodec.ToWavBlob(pcm, 1, parsed.SampleRate, bitsOut, formatCode: 1)));
      } else {
        foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
            pcm, parsed.NumChannels, parsed.SampleRate, bitsOut))
          entries.Add(($"{name}.wav", "Channel", wavBlob));
      }
    }

    if (parsed.Annotations != null && parsed.Annotations.Length > 0)
      entries.Add(("metadata/annotations.txt", "Tag", parsed.Annotations));
    if (parsed.Markers != null)
      entries.Add(("metadata/markers.bin", "Tag", parsed.Markers));
    if (parsed.Instrument != null)
      entries.Add(("metadata/instrument.bin", "Tag", parsed.Instrument));
    if (parsed.Id3 != null)
      entries.Add(("metadata/id3.bin", "Tag", parsed.Id3));
    foreach (var (id, data) in parsed.OtherChunks)
      entries.Add(($"metadata/{id.Trim()}.bin", "Tag", data));

    // Synthetic info file.
    var info = new StringBuilder();
    info.AppendLine($"format={(parsed.IsAifc ? "AIFC" : "AIFF")}");
    info.AppendLine($"compression_id={parsed.CompressionId}");
    info.AppendLine($"compression_name={parsed.CompressionName}");
    info.AppendLine($"channels={parsed.NumChannels}");
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"bits_per_sample={parsed.BitsPerSample}");
    info.AppendLine($"sample_frames={parsed.SampleFrames}");
    entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  /// <summary>
  /// Decodes the AIFF/AIFC sound data to little-endian linear PCM appropriate for
  /// wrapping in a standard RIFF/WAVE file. Returns null when the compression isn't
  /// decodable to linear PCM (then only FULL.aif is exposed).
  /// </summary>
  private static byte[]? DecodeToPcm(AiffReader.ParsedAiff p, out int bitsOut) {
    bitsOut = p.BitsPerSample;
    var id = p.CompressionId;

    // Uncompressed AIFF (no AIFC compression ID) → big-endian PCM.
    if (!p.IsAifc || id == "NONE" || id == "twos") {
      return ConvertBigEndianPcmToLittleEndian(p.SoundData, p.BitsPerSample);
    }
    if (id == "sowt") {
      // Already little-endian.
      return p.SoundData.ToArray();
    }
    if (id == "ulaw" || id == "ULAW") {
      var decoded = Codec.MuLaw.MuLawCodec.Decode(p.SoundData);
      bitsOut = 16;
      return ShortsToLePcm(decoded);
    }
    if (id == "alaw" || id == "ALAW") {
      var decoded = Codec.ALaw.ALawCodec.Decode(p.SoundData);
      bitsOut = 16;
      return ShortsToLePcm(decoded);
    }
    if (id == "fl32" || id == "FL32") {
      bitsOut = 32;
      return BigEndianFloat32ToLeFloat32(p.SoundData);
    }
    if (id == "fl64" || id == "FL64") {
      bitsOut = 64;
      return BigEndianFloat64ToLeFloat64(p.SoundData);
    }
    // ima4/GSM: not decoded (would need QuickTime-specific adpcm frame shape).
    return null;
  }

  private static byte[] ConvertBigEndianPcmToLittleEndian(byte[] be, int bitsPerSample) {
    var bytesPerSample = bitsPerSample / 8;
    if (bytesPerSample <= 1) return (byte[])be.Clone();
    var le = new byte[be.Length];
    for (var i = 0; i < be.Length; i += bytesPerSample)
      for (var j = 0; j < bytesPerSample && i + j < be.Length; ++j)
        le[i + j] = be[i + bytesPerSample - 1 - j];
    return le;
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static byte[] BigEndianFloat32ToLeFloat32(byte[] be) {
    var le = new byte[be.Length];
    for (var i = 0; i + 4 <= be.Length; i += 4) {
      le[i] = be[i + 3]; le[i + 1] = be[i + 2]; le[i + 2] = be[i + 1]; le[i + 3] = be[i];
    }
    return le;
  }

  private static byte[] BigEndianFloat64ToLeFloat64(byte[] be) {
    var le = new byte[be.Length];
    for (var i = 0; i + 8 <= be.Length; i += 8)
      for (var j = 0; j < 8; ++j) le[i + j] = be[i + 7 - j];
    return le;
  }
}
