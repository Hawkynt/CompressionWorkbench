#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.Tracker;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.S3m;

/// <summary>
/// Exposes a Scream Tracker 3 (S3M) module as an archive of <c>FULL.s3m</c>,
/// <c>metadata.ini</c>, a rendered <c>SONG.wav</c> (44100 Hz stereo 16-bit, played
/// from order 0 through the shared tracker mixer), <c>patterns/pattern_NN.bin</c>
/// (raw packed pattern blocks with their 2-byte length prefix stripped), and
/// <c>samples/NN_{name}.wav</c> per PCM instrument (decoded to a mono 16-bit WAV at
/// the instrument's C2SPD). Rendering degrades gracefully: any failure leaves the
/// previous surface intact, with samples falling back to their raw blobs.
/// </summary>
public sealed class S3mFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "S3m";
  public string DisplayName => "S3M (Scream Tracker 3)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".s3m";
  public IReadOnlyList<string> Extensions => [".s3m"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SCRM"u8.ToArray(), Offset: 44, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Scream Tracker 3 module; full file + patterns + raw PCM samples.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: e.Method, IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
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

  private static IReadOnlyList<(string Name, string Kind, byte[] Data, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    return Parse(blob);
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data, string Method)> Parse(byte[] blob) {
    var entries = new List<(string Name, string Kind, byte[] Data, string Method)> {
      ("FULL.s3m", "Container", blob, "stored"),
    };
    if (blob.Length < 96) return entries;

    var title = ReadAsciiTrim(blob, 0, 28);
    var songLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(32, 2));
    var numInstruments = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(34, 2));
    var numPatterns = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(36, 2));

    // Order table starts at 96, songLen bytes.
    var instrParaOff = 96 + songLen;
    var patternParaOff = instrParaOff + numInstruments * 2;

    // Parse patterns: each pattern's parapointer → offset (×16); first two bytes give the
    // packed pattern length (including the 2-byte length itself on some writers), then data.
    for (var p = 0; p < numPatterns; ++p) {
      var pp = patternParaOff + p * 2;
      if (pp + 2 > blob.Length) break;
      var para = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pp, 2));
      if (para == 0) continue;
      var off = para * 16;
      if (off + 2 > blob.Length) continue;
      var length = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off, 2));
      if (length < 2 || off + length > blob.Length) continue;
      var data = new byte[length - 2];
      Buffer.BlockCopy(blob, off + 2, data, 0, data.Length);
      entries.Add(($"patterns/pattern_{p:D2}.bin", "Pattern", data, "stored"));
    }

    // Decode each PCM instrument to a mono WAV via the shared tracker player; on
    // failure we fall back to the raw byte payload so the surface stays intact.
    var decoded = TryDecodeSamples(blob);

    // Parse instruments: each instrument header is 80 bytes at (parapointer × 16).
    var instrumentsWithData = 0;
    for (var s = 0; s < numInstruments; ++s) {
      var ip = instrParaOff + s * 2;
      if (ip + 2 > blob.Length) break;
      var para = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(ip, 2));
      if (para == 0) continue;
      var off = para * 16;
      if (off + 80 > blob.Length) continue;
      var type = blob[off];
      // Type 1 = PCM sample; 0 = empty; >1 = adlib/other — skip non-PCM.
      if (type != 1) continue;
      var dosName = ReadAsciiTrim(blob, off + 1, 12);
      // Sample data parapointer stored as MemSeg: high byte at +13, low word at +14 LE.
      var memSegHi = blob[off + 13];
      var memSegLo = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off + 14, 2));
      var memSeg = (memSegHi << 16) | memSegLo;
      var length = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 16, 4));
      var flags = blob[off + 31];
      var is16Bit = (flags & 0x04) != 0;
      var dataOff = memSeg * 16;
      var byteLen = (long)length * (is16Bit ? 2 : 1);
      if (dataOff < 0 || dataOff >= blob.Length || length == 0) continue;
      var take = (int)Math.Min(byteLen, blob.Length - dataOff);
      if (take <= 0) continue;
      var sampleName = ReadAsciiTrim(blob, off + 35, 28);
      var label = string.IsNullOrWhiteSpace(sampleName) ? dosName : sampleName;
      var baseName = $"samples/{(s + 1):D2}_{SanitizeFileName(label)}";

      if (decoded != null && s + 1 < decoded.Count && decoded[s + 1] is { } d && d.Pcm.Length > 0) {
        var pcm = new byte[d.Pcm.Length * 2];
        for (var i = 0; i < d.Pcm.Length; ++i)
          BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), d.Pcm[i]);
        var wav = PcmCodec.ToWavBlob(pcm, channels: 1, d.Rate, bitsPerSample: 16, formatCode: 1);
        entries.Add(($"{baseName}.wav", "Sample", wav, "decode"));
      } else {
        var data = new byte[take];
        Buffer.BlockCopy(blob, dataOff, data, 0, take);
        entries.Add(($"{baseName}.raw", "Sample", data, "stored"));
      }
      ++instrumentsWithData;
    }

    // Render the song from order 0; failure leaves the rest of the surface untouched.
    var rendered = TryRender(blob);

    var info = new StringBuilder();
    info.AppendLine($"title={title}");
    info.AppendLine($"signature=SCRM");
    info.AppendLine($"song_length={songLen}");
    info.AppendLine($"num_patterns={numPatterns}");
    info.AppendLine($"num_instruments={numInstruments}");
    info.AppendLine($"instruments_with_data={instrumentsWithData}");
    if (rendered is { } r) {
      info.AppendLine($"rendered_duration={r.Seconds:0.###}s");
      info.AppendLine($"rendered_sample_rate={OutputSampleRate}");
      info.AppendLine($"rendered_channels=2");
      info.AppendLine($"rendered_bits=16");
    }
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString()), "stored"));

    if (rendered is { } song) {
      var wav = PcmCodec.ToWavBlob(song.Pcm, channels: 2, OutputSampleRate, bitsPerSample: 16, formatCode: 1);
      entries.Insert(2, ("SONG.wav", "Track", wav, "render"));
      // Also surface the rendered stereo mix as individual mono speaker channels.
      var at = 3;
      foreach (var (name, channelWav) in PcmCodec.SplitInterleavedPcm(song.Pcm, channels: 2, OutputSampleRate, bitsPerSample: 16))
        entries.Insert(at++, ($"SONG_{name}.wav", "Channel", channelWav, "render"));
    }

    return entries;
  }

  /// <summary>Output sample rate for the rendered SONG.wav.</summary>
  private const int OutputSampleRate = 44100;

  /// <summary>Maximum rendered duration, bounded so non-terminating songs still produce a finite preview.</summary>
  private const double MaxRenderSeconds = 600.0;

  private static (byte[] Pcm, double Seconds)? TryRender(byte[] blob) {
    try {
      var seconds = S3mModule.EstimateSeconds(blob) ?? MaxRenderSeconds;
      seconds = Math.Min(Math.Max(seconds, 0.1), MaxRenderSeconds);
      return S3mModule.Render(blob, OutputSampleRate, seconds);
    } catch {
      return null;
    }
  }

  private static IReadOnlyList<(short[] Pcm, int Rate)?>? TryDecodeSamples(byte[] blob) {
    try {
      return S3mModule.DecodeSamples(blob);
    } catch {
      return null;
    }
  }

  private static string ReadAsciiTrim(byte[] blob, int offset, int length) {
    var end = offset + length;
    if (end > blob.Length) end = blob.Length;
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
    foreach (var c in name) {
      if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
      else sb.Append('_');
    }
    var s = sb.ToString().Trim('.');
    return s.Length == 0 ? "sample" : s;
  }
}
