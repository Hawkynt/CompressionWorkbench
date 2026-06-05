#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.Sid;
using Compression.Registry;

namespace FileFormat.Sid;

/// <summary>
/// Surfaces a Commodore 64 SID tune (<c>.sid</c>) as a metadata-rich pseudo-archive. A SID file
/// carries a 6510 program that drives the C64's MOS 6581/8580 SID chip. The C64 program image is
/// surfaced verbatim as a Kind <c>Stream</c> blob, and — for PSID tunes — the start song is
/// emulated (6510 core + register-level SID synthesis) and rendered to a playable <c>MONO.wav</c>
/// (44100 Hz, 16-bit), respecting the header's SID model (6581 vs 8580) and clock (PAL/NTSC).
/// RSID and BASIC tunes degrade to header/program-only.
/// <para>The header is <b>big-endian</b> and exists in two magic variants — <c>PSID</c>
/// (BASIC/KERNAL-assisted) and <c>RSID</c> (real C64 environment). Fields: u16 version (1-4),
/// u16 dataOffset, u16 loadAddr, u16 initAddr, u16 playAddr, u16 songs, u16 startSong,
/// u32 speed, then three 32-byte name/author/released strings. Version 2+ adds a u16 flags word
/// (decoded into clock PAL/NTSC and SID model 6581/8580), plus startPage / pageLength and the
/// second/third SID chip addresses. The C64 program begins at <c>dataOffset</c>; when
/// <c>loadAddr == 0</c> the real load address is the first two (little-endian) bytes of that
/// program. The program is surfaced as <c>program.bin</c>.</para>
/// Read-only; parsing degrades to FULL-only on malformed input.
/// </summary>
public sealed class SidFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Sid";
  public string DisplayName => "Commodore 64 SID";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".sid";
  public IReadOnlyList<string> Extensions => [".sid"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PSID"u8.ToArray(), Confidence: 0.95),
    new("RSID"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore 64 SID tune (PSID/RSID); full file + header metadata + 6510 program image.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.sid", "Container", blob),
    };

    if (blob.Length < 0x76)
      return entries;

    var magic = Encoding.ASCII.GetString(blob, 0, 4);
    var version = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x04));
    var dataOffset = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x06));
    var loadAddr = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x08));
    var initAddr = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x0A));
    var playAddr = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x0C));
    var songs = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x0E));
    var startSong = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x10));
    var speed = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(0x12));
    var name = ReadFixed(blob, 0x16, 32);
    var author = ReadFixed(blob, 0x36, 32);
    var released = ReadFixed(blob, 0x56, 32);

    var sb = new StringBuilder();
    sb.AppendLine("[sid]");
    sb.AppendLine($"magic={magic}");
    sb.AppendLine($"version={version}");
    sb.AppendLine($"data_offset=0x{dataOffset:X4}");
    sb.AppendLine($"load_addr=0x{loadAddr:X4}");
    sb.AppendLine($"init_addr=0x{initAddr:X4}");
    sb.AppendLine($"play_addr=0x{playAddr:X4}");
    sb.AppendLine($"songs={songs}");
    sb.AppendLine($"start_song={startSong}");
    sb.AppendLine($"speed=0x{speed:X8}");
    AppendField(sb, "name", name);
    AppendField(sb, "author", author);
    AppendField(sb, "released", released);

    // Version 2+ header extension (the v2 header is 0x7C bytes vs. 0x76 for v1).
    if (version >= 2 && blob.Length >= 0x7C) {
      var flags = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x76));
      var startPage = blob[0x78];
      var pageLength = blob[0x79];
      var secondSid = blob[0x7A];
      var thirdSid = blob[0x7B];
      sb.AppendLine($"flags=0x{flags:X4}");
      sb.AppendLine($"clock={DescribeClock(flags)}");
      sb.AppendLine($"sid_model={DescribeSidModel(flags)}");
      sb.AppendLine($"start_page=0x{startPage:X2}");
      sb.AppendLine($"page_length=0x{pageLength:X2}");
      if (secondSid != 0)
        sb.AppendLine($"second_sid_addr=0x{0xD000 | (secondSid << 4):X4}");
      if (thirdSid != 0)
        sb.AppendLine($"third_sid_addr=0x{0xD000 | (thirdSid << 4):X4}");
    }

    // Render the start song to a playable mono WAV. SID model and clock come from the v2+
    // flags (unknown → 6581 / PAL). Any failure (RSID, BASIC tune, unsupported player path,
    // malformed program) degrades silently to the existing header/program-only behaviour.
    var (model, clock) = ResolveModelAndClock(version, blob);
    var rendered = TryRender(blob, model, clock);
    if (rendered is { } wav) {
      sb.AppendLine($"rendered_duration={RenderSeconds:0.#}s");
      sb.AppendLine($"rendered_model={(model == SidModel.Mos8580 ? "MOS8580" : "MOS6581")}");
      sb.AppendLine($"rendered_clock={(clock < 1_000_000 ? "PAL" : "NTSC")}");
    }

    // C64 program: at dataOffset; when loadAddr==0 the first two LE bytes are the load address.
    if (dataOffset <= blob.Length) {
      var program = blob[dataOffset..];
      if (loadAddr == 0 && program.Length >= 2) {
        var realLoad = BinaryPrimitives.ReadUInt16LittleEndian(program);
        sb.AppendLine($"real_load_addr=0x{realLoad:X4}");
      } else if (loadAddr != 0) {
        sb.AppendLine($"real_load_addr=0x{loadAddr:X4}");
      }
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
      if (rendered is { } w)
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(w, channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1), "sid"));
      if (program.Length > 0)
        entries.Add(new("program.bin", "Stream", program));
    } else {
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
    }

    return entries;
  }

  /// <summary>Output sample rate for the rendered MONO.wav.</summary>
  private const int OutputSampleRate = 44100;

  /// <summary>
  /// Maximum rendered duration. Bounded at 30 seconds so a long or non-terminating tune
  /// still yields a reasonable, quickly-produced preview.
  /// </summary>
  private const double RenderSeconds = 30.0;

  // PAL/NTSC SID master clock frequencies.
  private const double PalClockHz = 985248.0;
  private const double NtscClockHz = 1022727.0;

  /// <summary>Resolves the SID model and clock from the v2+ flags word (unknown → 6581 / PAL).</summary>
  private static (SidModel Model, double ClockHz) ResolveModelAndClock(ushort version, byte[] blob) {
    var model = SidModel.Mos6581;
    var clock = PalClockHz;
    if (version >= 2 && blob.Length >= 0x7C) {
      var flags = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x76));
      if ((flags >> 4 & 0x03) == 2)
        model = SidModel.Mos8580;
      if ((flags >> 2 & 0x03) == 2)
        clock = NtscClockHz;
    }
    return (model, clock);
  }

  /// <summary>Renders the start song to 16-bit mono LE PCM, or null on any failure.</summary>
  private static byte[]? TryRender(byte[] blob, SidModel model, double clockHz) {
    try {
      var player = new PsidPlayer(blob, model, clockHz);
      var samples = player.Render(RenderSeconds, OutputSampleRate);
      var pcm = new byte[samples.Length * 2];
      for (var i = 0; i < samples.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
      return pcm;
    } catch {
      return null;
    }
  }

  /// <summary>Decodes bits 2-3 of the v2+ flags word into the SID clock standard.</summary>
  private static string DescribeClock(ushort flags) => (flags >> 2 & 0x03) switch {
    1 => "PAL",
    2 => "NTSC",
    3 => "PAL/NTSC (both)",
    _ => "unknown",
  };

  /// <summary>Decodes bits 4-5 of the v2+ flags word into the SID chip model.</summary>
  private static string DescribeSidModel(ushort flags) => (flags >> 4 & 0x03) switch {
    1 => "MOS6581",
    2 => "MOS8580",
    3 => "MOS6581/8580 (both)",
    _ => "unknown",
  };

  private static string ReadFixed(byte[] blob, int offset, int length) {
    if (offset + length > blob.Length)
      length = Math.Max(0, blob.Length - offset);
    var raw = blob.AsSpan(offset, length);
    var end = raw.IndexOf((byte)0);
    if (end < 0)
      end = raw.Length;
    return Encoding.Latin1.GetString(raw[..end]).Trim();
  }

  private static void AppendField(StringBuilder sb, string key, string value) {
    value = value.Trim();
    if (value.Length > 0)
      sb.AppendLine($"{key}={value}");
  }
}
