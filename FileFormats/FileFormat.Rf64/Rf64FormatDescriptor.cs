#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Rf64;

/// <summary>
/// Exposes an EBU RF64 / Broadcast Wave (.wav with "RF64" magic) file as an
/// archive of <c>FULL.rf64</c>, a <c>metadata.ini</c> built from the <c>fmt</c>,
/// <c>ds64</c> and (if present) <c>bext</c> chunks, and one mono WAV per channel
/// when the audio is linear integer PCM. <c>Create</c> wraps per-channel WAVs into
/// an RF64 + ds64 + fmt + data container.
/// </summary>
/// <remarks>
/// RF64 is the EBU extension of RIFF/WAVE for files larger than 4 GB. The header
/// reads "RF64" + 0xFFFFFFFF + "WAVE", and a mandatory <c>ds64</c> chunk carries
/// 64-bit riffSize, dataSize and sampleCount; any chunk whose 32-bit size is
/// 0xFFFFFFFF takes its real size from <c>ds64</c>. A <c>bext</c> Broadcast Wave
/// chunk may carry description / originator / time-reference metadata.
/// </remarks>
public sealed class Rf64FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveCreatable, IArchiveWriteConstraints {

  public string Id => "Rf64";
  public string DisplayName => "EBU RF64 / Broadcast Wave (.rf64)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".rf64";
  public IReadOnlyList<string> Extensions => [".rf64", ".bwf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("RF64"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "EBU RF64 / Broadcast Wave; full file + ds64/fmt/bext metadata + per-channel PCM WAV. Create wraps PCM with a ds64 chunk.";

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
    "RF64 archive accepts: FULL.rf64, or one/more per-channel mono WAVs (LEFT/RIGHT/…).";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.rf64" or "full.bwf" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not an RF64-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.rf64", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) { output.Write(full.Data); return; }

    var channels = WavChannelMux.GatherChannels(fileList);
    if (channels.Count == 0)
      throw new InvalidOperationException("RF64 archive create needs either FULL.rf64 or per-channel WAVs.");

    var (interleaved, channelCount, sampleRate, bitsPerSample) = WavChannelMux.Interleave(channels);
    output.Write(WriteRf64(interleaved, channelCount, sampleRate, bitsPerSample));
  }

  /// <summary>Builds a minimal RF64 (RF64 + ds64 + fmt + data) around interleaved
  /// little-endian integer PCM. The on-wire 32-bit RIFF + data sizes are set to
  /// 0xFFFFFFFF, with the real 64-bit sizes carried in the ds64 chunk.</summary>
  internal static byte[] WriteRf64(byte[] interleavedLe, int channels, int sampleRate, int bitsPerSample) {
    var fmtBody = BuildFmtBody(channels, sampleRate, bitsPerSample);
    var bytesPerFrame = channels * bitsPerSample / 8;
    var sampleCount = bytesPerFrame == 0 ? 0L : interleavedLe.Length / bytesPerFrame;

    // ds64 body: riffSize(8) + dataSize(8) + sampleCount(8) + tableLength(4) = 28 bytes.
    var ds64Body = new byte[28];
    var dataSize = (long)interleavedLe.Length;
    // riffSize = everything after "RF64"+size(8 bytes header) — computed below once total known.
    using var ms = new MemoryStream();
    ms.Write("RF64"u8);
    WriteU32Le(ms, 0xFFFFFFFF);
    ms.Write("WAVE"u8);
    WriteChunk(ms, "ds64", ds64Body);       // placeholder ds64 (patched after we know riffSize)
    WriteChunk(ms, "fmt ", fmtBody);
    // data chunk with 0xFFFFFFFF size marker
    ms.Write("data"u8);
    WriteU32Le(ms, 0xFFFFFFFF);
    ms.Write(interleavedLe);
    if ((interleavedLe.Length & 1) == 1) ms.WriteByte(0);

    var blob = ms.ToArray();
    // riffSize = total file length - 8 (the "RF64" + size fields).
    var riffSize = (long)blob.Length - 8;
    // Patch ds64 body: it sits right after the 12-byte header + 8-byte chunk header.
    var ds64BodyOffset = 12 + 8;
    BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(ds64BodyOffset + 0), riffSize);
    BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(ds64BodyOffset + 8), dataSize);
    BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(ds64BodyOffset + 16), sampleCount);
    BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(ds64BodyOffset + 24), 0); // table length
    return blob;
  }

  private static byte[] BuildFmtBody(int channels, int sampleRate, int bitsPerSample) {
    var body = new byte[16];
    var byteRate = (uint)(sampleRate * channels * bitsPerSample / 8);
    var blockAlign = (ushort)(channels * bitsPerSample / 8);
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)channels);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), byteRate);
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(12), blockAlign);
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), (ushort)bitsPerSample);
    return body;
  }

  private static void WriteChunk(Stream s, string id, byte[] body) {
    s.Write(Encoding.ASCII.GetBytes(id));
    WriteU32Le(s, (uint)body.Length);
    s.Write(body);
    if ((body.Length & 1) == 1) s.WriteByte(0); // word align
  }

  private static void WriteU32Le(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    s.Write(b);
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var file = ms.ToArray();

    var entries = new List<(string, string, byte[])> {
      ("FULL.rf64", "Track", file),
    };

    try {
      ParseRf64(file, entries);
    } catch {
      entries.Add(("metadata.ini", "Tag",
        Encoding.UTF8.GetBytes("[rf64]\r\nparse_status=partial\r\n")));
    }
    return entries;
  }

  private static void ParseRf64(byte[] file, List<(string Name, string Kind, byte[] Data)> entries) {
    var isRf64 = file.Length >= 12 && file[0] == 'R' && file[1] == 'F' && file[2] == '6' && file[3] == '4';
    var isRiff = file.Length >= 12 && file[0] == 'R' && file[1] == 'I' && file[2] == 'F' && file[3] == 'F';
    var isWave = file.Length >= 12 && file[8] == 'W' && file[9] == 'A' && file[10] == 'V' && file[11] == 'E';
    if (!(isRf64 || isRiff) || !isWave) {
      entries.Add(("metadata.ini", "Tag",
        Encoding.UTF8.GetBytes("[rf64]\r\nparse_status=partial\r\n")));
      return;
    }

    int formatCode = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;
    var haveFmt = false;
    long ds64DataSize = -1, ds64RiffSize = -1, ds64SampleCount = -1;
    byte[]? audio = null;
    string? bextDescription = null, bextOriginator = null, bextOriginationDate = null;
    long? bextTimeReference = null;

    var pos = 12;
    while (pos + 8 <= file.Length) {
      var id = Encoding.ASCII.GetString(file, pos, 4);
      var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
      var bodyStart = pos + 8;

      // RF64 size escape: 0xFFFFFFFF means "look in ds64".
      if (size == 0xFFFFFFFF) {
        if (id == "data" && ds64DataSize >= 0) size = ds64DataSize;
        else size = file.Length - bodyStart;
      }
      var bodyLen = (int)Math.Min(size, file.Length - bodyStart);
      if (bodyLen < 0) break;

      switch (id) {
        case "ds64" when bodyLen >= 24:
          ds64RiffSize = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(bodyStart));
          ds64DataSize = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(bodyStart + 8));
          ds64SampleCount = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(bodyStart + 16));
          break;
        case "fmt " when bodyLen >= 16:
          formatCode = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(bodyStart));
          channels = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(bodyStart + 2));
          sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(bodyStart + 4));
          bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(bodyStart + 14));
          haveFmt = true;
          break;
        case "data":
          audio = file.AsSpan(bodyStart, bodyLen).ToArray();
          break;
        case "bext" when bodyLen >= 348:
          bextDescription = ReadFixedAscii(file, bodyStart, 256);
          bextOriginator = ReadFixedAscii(file, bodyStart + 256, 32);
          bextOriginationDate = ReadFixedAscii(file, bodyStart + 320, 10);
          bextTimeReference = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(bodyStart + 338));
          break;
      }

      pos = bodyStart + bodyLen + (bodyLen & 1); // word-align
    }

    var meta = new StringBuilder();
    meta.AppendLine("[rf64]");
    meta.Append("container=").AppendLine(isRf64 ? "RF64" : "RIFF");
    if (haveFmt) {
      meta.Append("format_code=").AppendLine(formatCode.ToString(CultureInfo.InvariantCulture));
      meta.Append("sample_rate=").AppendLine(sampleRate.ToString(CultureInfo.InvariantCulture));
      meta.Append("channels=").AppendLine(channels.ToString(CultureInfo.InvariantCulture));
      meta.Append("bits_per_sample=").AppendLine(bitsPerSample.ToString(CultureInfo.InvariantCulture));
    } else {
      meta.AppendLine("parse_status=partial");
    }
    if (ds64DataSize >= 0) {
      meta.Append("ds64_riff_size=").AppendLine(ds64RiffSize.ToString(CultureInfo.InvariantCulture));
      meta.Append("ds64_data_size=").AppendLine(ds64DataSize.ToString(CultureInfo.InvariantCulture));
      meta.Append("ds64_sample_count=").AppendLine(ds64SampleCount.ToString(CultureInfo.InvariantCulture));
    }
    if (bextDescription != null) {
      meta.Append("bext_description=").AppendLine(bextDescription);
      meta.Append("bext_originator=").AppendLine(bextOriginator);
      meta.Append("bext_origination_date=").AppendLine(bextOriginationDate);
      meta.Append("bext_time_reference=").AppendLine(
        (bextTimeReference ?? 0).ToString(CultureInfo.InvariantCulture));
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

  private static string ReadFixedAscii(byte[] file, int offset, int length) {
    var end = offset;
    var limit = Math.Min(offset + length, file.Length);
    while (end < limit && file[end] != 0) ++end;
    return Encoding.ASCII.GetString(file, offset, end - offset).TrimEnd();
  }
}
