#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Caf;

/// <summary>
/// Exposes an Apple Core Audio Format (.caf) file as an archive of
/// <c>FULL.caf</c>, a <c>metadata.ini</c> built from the <c>desc</c> chunk, one
/// mono WAV per channel when the audio is linear integer PCM (<c>lpcm</c>), and
/// an <c>info.ini</c> carrying the <c>info</c> key-value chunk.
/// </summary>
/// <remarks>
/// CAF is big-endian. Header is "caff" + version(u16) + flags(u16). Each chunk is
/// a 4-char type + signed int64 size + payload. The Audio Stream Description
/// (<c>desc</c>) is: sample rate (float64), format id (4 chars), format flags
/// (u32), bytes per packet (u32), frames per packet (u32), channels per frame
/// (u32), bits per channel (u32). All multi-byte fields are big-endian.
/// <para>Per the spec, a <c>data</c> chunk whose size is -1 (or which runs to EOF)
/// carries an "edit count" u32 prefix before the audio bytes.</para>
/// </remarks>
public sealed class CafFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveCreatable, IArchiveWriteConstraints {

  public string Id => "Caf";
  public string DisplayName => "Apple Core Audio Format (.caf)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".caf";
  public IReadOnlyList<string> Extensions => [".caf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("caff"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Apple Core Audio Format; full file + desc/info metadata + per-channel PCM WAV. Create wraps PCM into desc+data.";

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

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "CAF archive accepts: FULL.caf, or one/more per-channel mono WAVs (LEFT/RIGHT/…).";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.caf" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not a CAF-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── IArchiveCreatable: interleave per-channel WAVs into a desc+data CAF ─────

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.caf", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) { output.Write(full.Data); return; }

    var channels = WavChannelMux.GatherChannels(fileList);
    if (channels.Count == 0)
      throw new InvalidOperationException("CAF archive create needs either FULL.caf or per-channel WAVs.");

    var (interleaved, channelCount, sampleRate, bitsPerSample) = WavChannelMux.Interleave(channels);
    output.Write(WriteCaf(interleaved, channelCount, sampleRate, bitsPerSample));
  }

  /// <summary>Wraps interleaved little-endian integer PCM into a minimal CAF
  /// (caff header + desc + data). Audio is converted to big-endian per the CAF
  /// <c>lpcm</c> convention with the big-endian flag clear meaning little-endian;
  /// here we keep little-endian samples and clear bit 1 of the format flags.</summary>
  internal static byte[] WriteCaf(byte[] interleavedLe, int channels, int sampleRate, int bitsPerSample) {
    using var ms = new MemoryStream();
    ms.Write("caff"u8);
    Span<byte> u16 = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(u16, 1); ms.Write(u16); // version
    BinaryPrimitives.WriteUInt16BigEndian(u16, 0); ms.Write(u16); // flags

    // desc chunk
    ms.Write("desc"u8);
    WriteI64Be(ms, 32);
    Span<byte> f64 = stackalloc byte[8];
    BinaryPrimitives.WriteDoubleBigEndian(f64, sampleRate); ms.Write(f64);
    ms.Write("lpcm"u8);
    var bytesPerChannel = bitsPerSample / 8;
    var bytesPerPacket = (uint)(bytesPerChannel * channels);
    // Format flags: bit 0 = float (0 here), bit 1 = little-endian (1 here, since
    // our PCM bytes are little-endian).
    const uint kCafLinearPcmFormatFlagIsLittleEndian = 1u << 1;
    WriteU32Be(ms, kCafLinearPcmFormatFlagIsLittleEndian);
    WriteU32Be(ms, bytesPerPacket);          // bytes per packet
    WriteU32Be(ms, 1);                        // frames per packet
    WriteU32Be(ms, (uint)channels);           // channels per frame
    WriteU32Be(ms, (uint)bitsPerSample);      // bits per channel

    // data chunk: int64 size includes the leading u32 "edit count".
    ms.Write("data"u8);
    WriteI64Be(ms, 4L + interleavedLe.Length);
    WriteU32Be(ms, 0);                        // edit count
    ms.Write(interleavedLe);

    return ms.ToArray();
  }

  private static void WriteU32Be(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b);
  }

  private static void WriteI64Be(Stream s, long v) {
    Span<byte> b = stackalloc byte[8];
    BinaryPrimitives.WriteInt64BigEndian(b, v); s.Write(b);
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var file = ms.ToArray();

    var entries = new List<(string, string, byte[])> {
      ("FULL.caf", "Track", file),
    };

    try {
      ParseCaf(file, entries);
    } catch {
      entries.Add(("metadata.ini", "Tag",
        Encoding.UTF8.GetBytes("[caf]\r\nparse_status=partial\r\n")));
    }
    return entries;
  }

  private static void ParseCaf(byte[] file, List<(string Name, string Kind, byte[] Data)> entries) {
    if (file.Length < 8 || file[0] != 'c' || file[1] != 'a' || file[2] != 'f' || file[3] != 'f') {
      entries.Add(("metadata.ini", "Tag",
        Encoding.UTF8.GetBytes("[caf]\r\nparse_status=partial\r\n")));
      return;
    }

    double sampleRate = 0;
    var formatId = "";
    uint formatFlags = 0, bytesPerPacket = 0, framesPerPacket = 0, channels = 0, bitsPerChannel = 0;
    var haveDesc = false;
    byte[]? audio = null;
    var infoPairs = new List<(string, string)>();

    var pos = 8; // skip "caff" + version + flags
    while (pos + 12 <= file.Length) {
      var type = Encoding.ASCII.GetString(file, pos, 4);
      var size = BinaryPrimitives.ReadInt64BigEndian(file.AsSpan(pos + 4));
      var bodyStart = pos + 12;
      // size of -1 (or anything reaching EOF) means "to end of file" for data.
      var bodyLen = size < 0 || bodyStart + size > file.Length
        ? file.Length - bodyStart
        : (int)size;
      if (bodyLen < 0) break;

      switch (type) {
        case "desc" when bodyLen >= 32:
          sampleRate = BinaryPrimitives.ReadDoubleBigEndian(file.AsSpan(bodyStart));
          formatId = Encoding.ASCII.GetString(file, bodyStart + 8, 4);
          formatFlags = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(bodyStart + 12));
          bytesPerPacket = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(bodyStart + 16));
          framesPerPacket = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(bodyStart + 20));
          channels = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(bodyStart + 24));
          bitsPerChannel = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(bodyStart + 28));
          haveDesc = true;
          break;
        case "data" when bodyLen >= 4:
          // First u32 is the edit count; audio bytes follow.
          audio = file.AsSpan(bodyStart + 4, bodyLen - 4).ToArray();
          break;
        case "info" when bodyLen >= 4:
          ParseInfoChunk(file.AsSpan(bodyStart, bodyLen), infoPairs);
          break;
      }

      pos = bodyStart + bodyLen;
      if (bodyLen == 0 && size <= 0) break; // guard against infinite loop on bogus size
    }

    var meta = new StringBuilder();
    meta.AppendLine("[caf]");
    if (haveDesc) {
      meta.Append("sample_rate=").AppendLine(sampleRate.ToString(CultureInfo.InvariantCulture));
      meta.Append("format_id=").AppendLine(formatId);
      meta.Append("format_flags=0x").AppendLine(formatFlags.ToString("X8", CultureInfo.InvariantCulture));
      meta.Append("bytes_per_packet=").AppendLine(bytesPerPacket.ToString(CultureInfo.InvariantCulture));
      meta.Append("frames_per_packet=").AppendLine(framesPerPacket.ToString(CultureInfo.InvariantCulture));
      meta.Append("channels=").AppendLine(channels.ToString(CultureInfo.InvariantCulture));
      meta.Append("bits_per_channel=").AppendLine(bitsPerChannel.ToString(CultureInfo.InvariantCulture));
    } else {
      meta.AppendLine("parse_status=partial");
    }
    entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    if (infoPairs.Count > 0) {
      var info = new StringBuilder();
      info.AppendLine("[info]");
      foreach (var (k, v) in infoPairs)
        info.Append(k).Append('=').AppendLine(v);
      entries.Add(("info.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
    }

    // Per-channel split only for linear integer PCM ("lpcm", not float).
    var isFloat = (formatFlags & 0x1) != 0;
    var isBigEndian = (formatFlags & 0x2) == 0; // bit 1 set => little-endian
    if (haveDesc && formatId == "lpcm" && !isFloat &&
        bitsPerChannel is 8 or 16 or 24 or 32 && channels >= 1 && audio is { Length: > 0 }) {
      var bytesPerSample = (int)bitsPerChannel / 8;
      var pcmLe = isBigEndian ? ConvertBeToLe(audio, bytesPerSample) : audio;
      var frameBytes = bytesPerSample * (int)channels;
      if (pcmLe.Length % frameBytes == 0) {
        if (channels == 1) {
          entries.Add(("MONO.wav", "Channel",
            PcmCodec.ToWavBlob(pcmLe, 1, (int)sampleRate, (int)bitsPerChannel, formatCode: 1)));
        } else {
          foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
              pcmLe, (int)channels, (int)sampleRate, (int)bitsPerChannel))
            entries.Add(($"{name}.wav", "Channel", wavBlob));
        }
      }
    }
  }

  // CAF "info" chunk: u32 entry count, then count×(null-terminated key, null-terminated value).
  private static void ParseInfoChunk(ReadOnlySpan<byte> body, List<(string, string)> pairs) {
    var count = BinaryPrimitives.ReadUInt32BigEndian(body);
    var p = 4;
    for (var i = 0; i < count && p < body.Length; ++i) {
      var keyEnd = p;
      while (keyEnd < body.Length && body[keyEnd] != 0) ++keyEnd;
      if (keyEnd >= body.Length) break;
      var key = Encoding.UTF8.GetString(body.Slice(p, keyEnd - p));
      p = keyEnd + 1;
      var valEnd = p;
      while (valEnd < body.Length && body[valEnd] != 0) ++valEnd;
      var val = Encoding.UTF8.GetString(body.Slice(p, Math.Min(valEnd, body.Length) - p));
      p = valEnd + 1;
      pairs.Add((key, val));
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
}
