#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.TrackerXmIt;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.It;

/// <summary>
/// Exposes an Impulse Tracker (IT) module as an archive of <c>FULL.it</c>, <c>metadata.ini</c>,
/// a rendered <c>SONG.wav</c> (Kind <c>Track</c>; 44100 Hz stereo 16-bit), the packed
/// <c>patterns/pattern_NN.bin</c> data, <c>instruments/NN_{name}.bin</c> instrument blocks, and
/// each sample decoded to a playable mono WAV (<c>samples/NN_{name}.wav</c>) at its C5 speed.
/// IT214/IT215-compressed samples are decompressed via <see cref="ItSampleDecompressor"/>. The
/// song is rendered by <see cref="ItPlayer"/> (NNA/virtual channels, envelopes, resonant filter,
/// effects A..Z); rendering failures degrade gracefully to the non-rendered entries.
/// </summary>
public sealed class ItFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "It";
  public string DisplayName => "IT (Impulse Tracker)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".it";
  public IReadOnlyList<string> Extensions => [".it"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("IMPM"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Impulse Tracker module; full file + patterns + instruments + raw PCM samples.";

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
      return [("FULL.it", "Container", blob)];
    }
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> ParseCore(byte[] blob) {
    var entries = new List<(string, string, byte[])> {
      ("FULL.it", "Container", blob),
    };
    if (blob.Length < 192) return entries;

    var songName = ReadAsciiTrim(blob, 4, 26);
    var ordNum = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(32, 2));
    var insNum = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(34, 2));
    var smpNum = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(36, 2));
    var patNum = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(38, 2));

    // Offset tables start at 192 + ordNum (order list) — instrument offsets, sample offsets, pattern offsets.
    var insOffsetsStart = 192 + ordNum;
    var smpOffsetsStart = insOffsetsStart + insNum * 4;
    var patOffsetsStart = smpOffsetsStart + smpNum * 4;

    // Patterns first (preserve insert order stability for tests).
    for (var p = 0; p < patNum; ++p) {
      var off = patOffsetsStart + p * 4;
      if (off + 4 > blob.Length) break;
      var patOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off, 4));
      if (patOff == 0 || patOff + 4 > blob.Length) continue;
      var packedSize = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(patOff, 2));
      var rows = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(patOff + 2, 2));
      var dataStart = patOff + 8;
      if (dataStart + packedSize > blob.Length) continue;
      var data = new byte[packedSize];
      if (packedSize > 0) Buffer.BlockCopy(blob, dataStart, data, 0, packedSize);
      entries.Add(($"patterns/pattern_{p:D2}_r{rows}.bin", "Pattern", data));
    }

    // Instruments — surface the raw instrument block as metadata (554 bytes for IT 2.00+,
    // 64 bytes for pre-2.00 "old instrument" blocks). We just emit a sized chunk up to
    // the next known boundary.
    for (var i = 0; i < insNum; ++i) {
      var tableOff = insOffsetsStart + i * 4;
      if (tableOff + 4 > blob.Length) break;
      var insOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(tableOff, 4));
      if (insOff == 0 || insOff + 64 > blob.Length) continue;
      // IT 2.00+ instrument header is 554 bytes and starts with "IMPI".
      var isNew = insOff + 4 <= blob.Length && blob[insOff] == (byte)'I' && blob[insOff + 1] == (byte)'M' &&
                  blob[insOff + 2] == (byte)'P' && blob[insOff + 3] == (byte)'I';
      var insSize = isNew ? 554 : 64;
      var take = Math.Min(insSize, blob.Length - insOff);
      if (take <= 0) continue;
      var data = new byte[take];
      Buffer.BlockCopy(blob, insOff, data, 0, take);
      var nameOff = isNew ? insOff + 32 : insOff + 20;
      var name = ReadAsciiTrim(blob, nameOff, 26);
      var label = string.IsNullOrWhiteSpace(name) ? $"instrument_{i + 1:D2}" : SanitizeFileName(name);
      entries.Add(($"instruments/{(i + 1):D2}_{label}.bin", "Instrument", data));
    }

    // Samples.
    for (var s = 0; s < smpNum; ++s) {
      var tableOff = smpOffsetsStart + s * 4;
      if (tableOff + 4 > blob.Length) break;
      var smpHdrOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(tableOff, 4));
      if (smpHdrOff == 0 || smpHdrOff + 80 > blob.Length) continue;
      // "IMPS" magic at the sample header.
      if (blob[smpHdrOff] != (byte)'I' || blob[smpHdrOff + 1] != (byte)'M' ||
          blob[smpHdrOff + 2] != (byte)'P' || blob[smpHdrOff + 3] != (byte)'S') continue;
      var dosName = ReadAsciiTrim(blob, smpHdrOff + 4, 12);
      var sampleName = ReadAsciiTrim(blob, smpHdrOff + 20, 26);
      var label = string.IsNullOrWhiteSpace(sampleName) ? (string.IsNullOrWhiteSpace(dosName) ? $"sample_{s + 1:D2}" : dosName) : sampleName;
      var safeLabel = SanitizeFileName(label);

      // Decode (incl. IT214/IT215 decompression) to signed 16-bit PCM and surface a playable
      // mono WAV at the sample's C5 speed.
      var sample = ItSample.Parse(blob, smpHdrOff);
      if (sample.Pcm.Length == 0) continue;
      var pcm = new byte[sample.Pcm.Length * 2];
      for (var i = 0; i < sample.Pcm.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), sample.Pcm[i]);
      var wav = PcmCodec.ToWavBlob(pcm, 1, sample.C5Speed, 16);
      entries.Add(($"samples/{(s + 1):D2}_{safeLabel}.wav", "Sample", wav));
    }

    byte[]? songWav = null;
    byte[]? songPcm = null;
    double renderedSeconds = 0;
    var renderNote = "ok";
    try {
      var player = ItPlayer.Load(blob);
      var pcm = player.Render();
      renderedSeconds = pcm.Length / (double)(TrackerRender.OutputSampleRate * TrackerRender.OutputChannels * 2);
      songWav = PcmCodec.ToWavBlob(pcm, TrackerRender.OutputChannels, TrackerRender.OutputSampleRate, TrackerRender.OutputBits);
      songPcm = pcm;
    } catch {
      renderNote = "render failed";
    }

    var info = new StringBuilder();
    info.AppendLine($"name={songName}");
    info.AppendLine($"signature=IMPM");
    info.AppendLine($"order_num={ordNum}");
    info.AppendLine($"patterns_count={patNum}");
    info.AppendLine($"instruments_count={insNum}");
    info.AppendLine($"samples_count={smpNum}");
    if (songWav != null) {
      info.AppendLine($"rendered_sample_rate={TrackerRender.OutputSampleRate}");
      info.AppendLine($"rendered_channels={TrackerRender.OutputChannels}");
      info.AppendLine($"rendered_duration={renderedSeconds:0.##}s");
    }
    info.AppendLine($"rendered_status={renderNote}");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    if (songWav != null) {
      entries.Insert(2, ("SONG.wav", "Track", songWav));
      // Also surface the rendered stereo mix as individual mono speaker channels.
      var at = 3;
      foreach (var (name, channelWav) in PcmCodec.SplitInterleavedPcm(songPcm!, TrackerRender.OutputChannels, TrackerRender.OutputSampleRate, TrackerRender.OutputBits))
        entries.Insert(at++, ($"SONG_{name}.wav", "Channel", channelWav));
    }

    return entries;
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
