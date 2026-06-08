#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.TrackerXmIt;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Xm;

/// <summary>
/// Exposes a FastTracker II XM module as an archive of <c>FULL.xm</c>, <c>metadata.ini</c>,
/// a rendered <c>SONG.wav</c> (Kind <c>Track</c>; 44100 Hz stereo 16-bit), the packed
/// <c>patterns/pattern_NN.bin</c> data, and per-instrument samples decoded to playable mono
/// WAVs (<c>instruments/NN_{name}/MM_{sample}.wav</c>) at each sample's relative-note/finetune
/// rate. The song is rendered by <see cref="XmPlayer"/> (linear/Amiga frequency tables,
/// envelopes, auto-vibrato, fadeout, volume column and effects 0..X); rendering failures degrade
/// gracefully to the non-rendered entries.
/// </summary>
public sealed class XmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Xm";
  public string DisplayName => "XM (FastTracker II)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".xm";
  public IReadOnlyList<string> Extensions => [".xm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("Extended Module: "u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "FastTracker II module; full file + patterns + per-instrument raw samples.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
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

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    return Parse(blob);
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> Parse(byte[] blob) {
    try {
      return ParseCore(blob);
    } catch {
      // Malformed input degrades to FULL-only.
      return [("FULL.xm", "Container", blob)];
    }
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> ParseCore(byte[] blob) {
    var entries = new List<(string, string, byte[])> {
      ("FULL.xm", "Container", blob),
    };
    if (blob.Length < 80) return entries;

    var songName = ReadAsciiTrim(blob, 17, 20);
    var trackerName = ReadAsciiTrim(blob, 38, 20);
    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(60, 4));
    var songLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(64, 2));
    var numChannels = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(68, 2));
    var numPatterns = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(70, 2));
    var numInstruments = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(72, 2));

    var totalSamples = 0;
    var cursor = 60 + (int)headerSize;

    // Patterns.
    for (var p = 0; p < numPatterns; ++p) {
      if (cursor + 9 > blob.Length) break;
      var patHdrLen = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(cursor, 4));
      var packedSize = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(cursor + 7, 2));
      var dataOff = cursor + (int)patHdrLen;
      if (dataOff + packedSize > blob.Length) {
        cursor = blob.Length;
        break;
      }
      var data = new byte[packedSize];
      if (packedSize > 0) Buffer.BlockCopy(blob, dataOff, data, 0, packedSize);
      entries.Add(($"patterns/pattern_{p:D2}.bin", "Pattern", data));
      cursor = dataOff + packedSize;
    }

    // Instruments.
    for (var ins = 0; ins < numInstruments; ++ins) {
      if (cursor + 29 > blob.Length) break;
      var insHdrSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(cursor, 4));
      var instrName = ReadAsciiTrim(blob, cursor + 4, 22);
      var numSamples = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(cursor + 27, 2));

      var insLabel = string.IsNullOrWhiteSpace(instrName) ? $"instrument_{ins + 1:D2}" : SanitizeFileName(instrName);
      var insDir = $"instruments/{(ins + 1):D2}_{insLabel}";
      var afterInsHeader = cursor + (int)insHdrSize;

      if (numSamples == 0) {
        cursor = afterInsHeader;
        continue;
      }

      // Sample headers (40 bytes each) immediately follow the instrument header.
      var sampleHeadersStart = afterInsHeader;
      var sampleHeaderSize = 40;
      if (sampleHeadersStart + numSamples * sampleHeaderSize > blob.Length) break;

      var lengths = new int[numSamples];
      var flags = new byte[numSamples];
      var names = new string[numSamples];
      for (var si = 0; si < numSamples; ++si) {
        var shOff = sampleHeadersStart + si * sampleHeaderSize;
        lengths[si] = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(shOff, 4)));
        flags[si] = blob[shOff + 14];
        names[si] = ReadAsciiTrim(blob, shOff + 18, 22);
      }

      var relNotes = new sbyte[numSamples];
      var finetunes = new sbyte[numSamples];
      for (var si = 0; si < numSamples; ++si) {
        var shOff = sampleHeadersStart + si * sampleHeaderSize;
        finetunes[si] = unchecked((sbyte)blob[shOff + 13]);
        relNotes[si] = unchecked((sbyte)blob[shOff + 16]);
      }

      var sampleDataOff = sampleHeadersStart + numSamples * sampleHeaderSize;
      for (var si = 0; si < numSamples; ++si) {
        var len = lengths[si];
        if (len <= 0 || sampleDataOff >= blob.Length) continue;
        var take = Math.Min(len, blob.Length - sampleDataOff);
        if (take <= 0) continue;
        var data = new byte[take];
        Buffer.BlockCopy(blob, sampleDataOff, data, 0, take);
        var sampleName = string.IsNullOrWhiteSpace(names[si]) ? $"sample_{si + 1:D2}" : SanitizeFileName(names[si]);
        var is16 = (flags[si] & 0x10) != 0;
        // Decode the delta-coded XM sample to signed 16-bit and surface a playable mono WAV at
        // the relative-note/finetune rate: rate = 8363 * 2^((relnote*128 + finetune)/(128*12)).
        var wav = BuildSampleWav(data, is16, relNotes[si], finetunes[si]);
        entries.Add(($"{insDir}/{(si + 1):D2}_{sampleName}.wav", "Sample", wav));
        sampleDataOff += len;
        ++totalSamples;
      }
      cursor = sampleDataOff;
    }

    // Render the whole song to a stereo WAV (graceful: any failure leaves SONG.wav out).
    byte[]? songWav = null;
    double renderedSeconds = 0;
    var renderNote = "ok";
    try {
      var player = XmPlayer.Load(blob);
      var pcm = player.Render();
      renderedSeconds = pcm.Length / (double)(TrackerRender.OutputSampleRate * TrackerRender.OutputChannels * 2);
      songWav = PcmCodec.ToWavBlob(pcm, TrackerRender.OutputChannels, TrackerRender.OutputSampleRate, TrackerRender.OutputBits);
    } catch {
      renderNote = "render failed";
    }

    var info = new StringBuilder();
    info.AppendLine($"name={songName}");
    info.AppendLine($"tracker={trackerName}");
    info.AppendLine($"song_length={songLen}");
    info.AppendLine($"channels={numChannels}");
    info.AppendLine($"patterns_count={numPatterns}");
    info.AppendLine($"instruments_count={numInstruments}");
    info.AppendLine($"samples_count={totalSamples}");
    if (songWav != null) {
      info.AppendLine($"rendered_sample_rate={TrackerRender.OutputSampleRate}");
      info.AppendLine($"rendered_channels={TrackerRender.OutputChannels}");
      info.AppendLine($"rendered_duration={renderedSeconds:0.##}s");
    }
    info.AppendLine($"rendered_status={renderNote}");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    if (songWav != null)
      entries.Insert(2, ("SONG.wav", "Track", songWav));

    return entries;
  }

  /// <summary>
  /// Decodes a delta-coded XM sample into signed 16-bit PCM and wraps it in a mono WAV at the
  /// sample's relative-note/finetune playback rate.
  /// </summary>
  private static byte[] BuildSampleWav(byte[] raw, bool is16, sbyte relNote, sbyte finetune) {
    var sample = new XmSample();
    sample.SetData(raw, is16);
    var pcm = new byte[sample.Pcm.Length * 2];
    for (var i = 0; i < sample.Pcm.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), sample.Pcm[i]);
    var rate = (int)Math.Round(8363.0 * Math.Pow(2.0, (relNote * 128 + finetune) / (128.0 * 12.0)));
    if (rate <= 0) rate = 8363;
    return PcmCodec.ToWavBlob(pcm, 1, rate, 16);
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
