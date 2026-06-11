#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.W64;

/// <summary>
/// Exposes a Sony Wave64 (.w64) file as an archive of <c>FULL.w64</c>, a
/// <c>metadata.ini</c> built from the <c>fmt</c> chunk, and one mono WAV per
/// channel when the audio is linear integer PCM. <c>Create</c> wraps per-channel
/// WAVs into a fmt/data Wave64 container.
/// </summary>
/// <remarks>
/// Wave64 mirrors RIFF/WAVE but replaces 4-char chunk ids with 128-bit GUIDs and
/// 32-bit sizes with 64-bit sizes so files can exceed 4 GB. The chunk size field
/// counts the entire chunk including the 16-byte GUID and the 8-byte size field.
/// Chunks are padded to an 8-byte boundary. The RIFF/WAVE/fmt/data GUIDs all share
/// the suffix <c>-912E-11CF-A5D6-28DB04C10000</c>; the first 4 bytes spell the
/// classic id ("riff", "wave", "fmt ", "data") stored little-endian.
/// </remarks>
public sealed class W64FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveCreatable, IArchiveWriteConstraints {

  // {66666972-912E-11CF-A5D6-28DB04C10000} — "riff" GUID, stored as 16 raw bytes.
  internal static readonly byte[] RiffGuid =
    [0x72, 0x69, 0x66, 0x66, 0x2E, 0x91, 0xCF, 0x11, 0xA5, 0xD6, 0x28, 0xDB, 0x04, 0xC1, 0x00, 0x00];
  // {77617665-...} — "wave"
  internal static readonly byte[] WaveGuid =
    [0x77, 0x61, 0x76, 0x65, 0x2E, 0x91, 0xCF, 0x11, 0xA5, 0xD6, 0x28, 0xDB, 0x04, 0xC1, 0x00, 0x00];
  // {20746D66-...} — "fmt "
  internal static readonly byte[] FmtGuid =
    [0x66, 0x6D, 0x74, 0x20, 0x2E, 0x91, 0xCF, 0x11, 0xA5, 0xD6, 0x28, 0xDB, 0x04, 0xC1, 0x00, 0x00];
  // {61746164-...} — "data"
  internal static readonly byte[] DataGuid =
    [0x64, 0x61, 0x74, 0x61, 0x2E, 0x91, 0xCF, 0x11, 0xA5, 0xD6, 0x28, 0xDB, 0x04, 0xC1, 0x00, 0x00];

  public string Id => "W64";
  public string DisplayName => "Sony Wave64 (.w64)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".w64";
  public IReadOnlyList<string> Extensions => [".w64"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(RiffGuid, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Sony Wave64; full file + fmt metadata + per-channel PCM WAV. Create wraps PCM into a GUID-keyed fmt/data container.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: e.Kind == "Channel" ? "pcm" : "stored",
      IsDirectory: false, IsEncrypted: false, LastModified: null,
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

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "W64 archive accepts: FULL.w64, or one/more per-channel mono WAVs (LEFT/RIGHT/…).";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.w64" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not a W64-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.w64", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) { output.Write(full.Data); return; }

    var channels = WavChannelMux.GatherChannels(fileList);
    if (channels.Count == 0)
      throw new InvalidOperationException("W64 archive create needs either FULL.w64 or per-channel WAVs.");

    var (interleaved, channelCount, sampleRate, bitsPerSample) = WavChannelMux.Interleave(channels);
    output.Write(WriteW64(interleaved, channelCount, sampleRate, bitsPerSample));
  }

  /// <summary>Builds a minimal Wave64 (riff/wave + fmt + data) around interleaved
  /// little-endian integer PCM. Each chunk's 8-byte size counts the GUID + size
  /// field + payload, and chunks are zero-padded to an 8-byte boundary.</summary>
  internal static byte[] WriteW64(byte[] interleavedLe, int channels, int sampleRate, int bitsPerSample) {
    var fmtBody = BuildFmtBody(channels, sampleRate, bitsPerSample);
    using var inner = new MemoryStream();
    // riff GUID stores "wave" GUID as its form type (after the riff GUID+size).
    WriteChunk(inner, FmtGuid, fmtBody);
    WriteChunk(inner, DataGuid, interleavedLe);
    var innerBytes = inner.ToArray();

    using var ms = new MemoryStream();
    ms.Write(RiffGuid);
    // riff chunk size: 16 (riff guid) + 8 (size) + 16 (wave guid) + inner.
    var riffSize = (long)16 + 8 + 16 + innerBytes.Length;
    WriteU64Le(ms, riffSize);
    ms.Write(WaveGuid);
    ms.Write(innerBytes);
    return ms.ToArray();
  }

  private static byte[] BuildFmtBody(int channels, int sampleRate, int bitsPerSample) {
    var body = new byte[16];
    var byteRate = (uint)(sampleRate * channels * bitsPerSample / 8);
    var blockAlign = (ushort)(channels * bitsPerSample / 8);
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), 1);                 // PCM
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)channels);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), byteRate);
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(12), blockAlign);
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), (ushort)bitsPerSample);
    return body;
  }

  private static void WriteChunk(Stream s, byte[] guid, byte[] body) {
    var size = (long)guid.Length + 8 + body.Length; // includes guid + size field + body
    s.Write(guid);
    WriteU64Le(s, size);
    s.Write(body);
    // Pad the whole chunk to an 8-byte boundary.
    var pad = (int)((8 - size % 8) % 8);
    for (var i = 0; i < pad; ++i) s.WriteByte(0);
  }

  private static void WriteU64Le(Stream s, long v) {
    Span<byte> b = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(b, (ulong)v);
    s.Write(b);
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var file = ms.ToArray();

    var entries = new List<(string, string, byte[])> {
      ("FULL.w64", "Track", file),
    };

    try {
      ParseW64(file, entries);
    } catch {
      entries.Add(("metadata.ini", "Tag",
        Encoding.UTF8.GetBytes("[w64]\r\nparse_status=partial\r\n")));
    }
    return entries;
  }

  private static void ParseW64(byte[] file, List<(string Name, string Kind, byte[] Data)> entries) {
    if (file.Length < 40 || !GuidMatches(file, 0, RiffGuid)) {
      entries.Add(("metadata.ini", "Tag",
        Encoding.UTF8.GetBytes("[w64]\r\nparse_status=partial\r\n")));
      return;
    }

    int formatCode = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;
    var haveFmt = false;
    byte[]? audio = null;

    // riff header is 16 (guid) + 8 (size) + 16 (wave guid) = 40 bytes; chunks follow.
    var pos = 40;
    while (pos + 24 <= file.Length) {
      var guid = file.AsSpan(pos, 16);
      var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(pos + 16));
      if (size < 24) break;
      var bodyStart = pos + 24;
      var bodyLen = (int)Math.Min(size - 24, file.Length - bodyStart);
      if (bodyLen < 0) break;

      if (guid.SequenceEqual(FmtGuid) && bodyLen >= 16) {
        formatCode = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(bodyStart));
        channels = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(bodyStart + 2));
        sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(bodyStart + 4));
        bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(bodyStart + 14));
        haveFmt = true;
      } else if (guid.SequenceEqual(DataGuid)) {
        audio = file.AsSpan(bodyStart, bodyLen).ToArray();
      }

      // Advance to next 8-byte-aligned chunk.
      var next = pos + (long)((size + 7) & ~7L);
      if (next <= pos) break;
      pos = (int)next;
    }

    var meta = new StringBuilder();
    meta.AppendLine("[w64]");
    if (haveFmt) {
      meta.Append("format_code=").AppendLine(formatCode.ToString(CultureInfo.InvariantCulture));
      meta.Append("sample_rate=").AppendLine(sampleRate.ToString(CultureInfo.InvariantCulture));
      meta.Append("channels=").AppendLine(channels.ToString(CultureInfo.InvariantCulture));
      meta.Append("bits_per_sample=").AppendLine(bitsPerSample.ToString(CultureInfo.InvariantCulture));
    } else {
      meta.AppendLine("parse_status=partial");
    }
    entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    if (haveFmt && formatCode == 1 && bitsPerSample is 8 or 16 or 24 or 32 &&
        channels >= 1 && audio is { Length: > 0 }) {
      var frameBytes = bitsPerSample / 8 * channels;
      if (audio.Length % frameBytes == 0) {
        if (channels == 1) {
          entries.Add(("MONO.wav", "Channel",
            PcmCodec.ToWavBlob(audio, 1, sampleRate, bitsPerSample, formatCode: 1)));
        } else {
          foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
              audio, channels, sampleRate, bitsPerSample))
            entries.Add(($"{name}.wav", "Channel", wavBlob));
        }
      }
    }
  }

  private static bool GuidMatches(byte[] file, int offset, byte[] guid) {
    if (offset + guid.Length > file.Length) return false;
    return file.AsSpan(offset, guid.Length).SequenceEqual(guid);
  }
}
