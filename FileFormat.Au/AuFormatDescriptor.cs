#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Au;

/// <summary>
/// Exposes a Sun/NeXT <c>.au</c> / <c>.snd</c> file as an archive of
/// <c>FULL.au</c>, one WAV per channel (after decoding μ-law/A-law/PCM), and
/// a <c>metadata.ini</c> carrying the encoding type, sample rate and any
/// annotation string.
/// </summary>
public sealed class AuFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints {

  public string Id => "Au";
  public string DisplayName => "Sun/NeXT .au (.snd)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".au";
  public IReadOnlyList<string> Extensions => [".au", ".snd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x2E, 0x73, 0x6E, 0x64], Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => ".au (Sun / NeXT) audio; μ-law / A-law / PCM decoded to per-channel WAV.";

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
    ".au archive accepts: FULL.au, LEFT/RIGHT/… .wav (per-channel), metadata.ini";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.au" or "full.snd" or "metadata.ini" ||
        name.EndsWith(".wav")) {
      reason = null; return true;
    }
    reason = $"not a .au-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new AuReader().Read(blob);

    var entries = new List<(string, string, byte[])> {
      ("FULL.au", "Track", blob),
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
      default: return (null, 0);                                 // float/G.721: not decoded
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
    27 => "8-bit G.711 A-law",
    _ => $"unknown ({e})",
  };
}
