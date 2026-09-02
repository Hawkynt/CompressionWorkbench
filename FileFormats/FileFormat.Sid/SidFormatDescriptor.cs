#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.Sid;
using Compression.Registry;

namespace FileFormat.Sid;

/// <summary>
/// Surfaces a Commodore 64 SID tune (<c>.sid</c>) as a metadata-rich pseudo-archive. A SID file
/// carries a 6510 program that drives the C64's MOS 6581/8580 SID chip(s). The C64 program image
/// is surfaced verbatim as a Kind <c>Stream</c> blob, and — for PSID tunes — the start song is
/// emulated (6510 core + register-level SID synthesis) and rendered to playable WAVs (44100 Hz,
/// 16-bit), respecting the header's per-chip SID models (6581 / 8580 / 6582-as-8580) and clock
/// (PAL/NTSC).
/// <para><b>Stereo matrix.</b> A 2SID/3SID tune declares extra chips via the v3/v4
/// secondSIDAddress/thirdSIDAddress bytes ($Dxx0; validated even and in $42-$7E or $E0-$FE).
/// 1 SID → <c>MONO.wav</c>; 2 SID → <c>LEFT.wav</c> (SID #1) + <c>RIGHT.wav</c> (SID #2); 3 SID →
/// adds <c>CENTER.wav</c> (SID #3). Each WAV is one decoded chip (a player mixes CENTER 0.5/0.5).
/// Per-chip models come from flag bits 4-5 (SID #1), 6-7 (SID #2), 8-9 (SID #3); a secondary
/// chip's 00 means "like SID #1". When SID #1's model flag is 00 (unknown) or 11 (either) the set
/// is rendered twice with <c>_6581</c>/<c>_8580</c> suffixes (e.g. <c>MONO_6581.wav</c> +
/// <c>MONO_8580.wav</c>), each capped at 20 s; a single specified-model render runs 30 s.</para>
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

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Sid";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Commodore 64 SID";
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
public string DefaultExtension => ".sid";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".sid"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PSID"u8.ToArray(), Confidence: 0.95),
    new("RSID"u8.ToArray(), Confidence: 0.95),
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
public string Description => "Commodore 64 SID tune (PSID/RSID); full file + header metadata + 6510 program image.";

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
    var sidSetup = ResolveSidSetup(version, blob);
    if (version >= 2 && blob.Length >= 0x7C) {
      var flags = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x76));
      var startPage = blob[0x78];
      var pageLength = blob[0x79];
      var secondSid = blob[0x7A];
      var thirdSid = blob[0x7B];
      sb.AppendLine($"flags=0x{flags:X4}");
      sb.AppendLine($"clock={DescribeClock(flags)}");
      sb.AppendLine($"sid_model={DescribeSidModel(flags, 4)}");      // bits 4-5 → SID #1
      sb.AppendLine($"start_page=0x{startPage:X2}");
      sb.AppendLine($"page_length=0x{pageLength:X2}");
      // SID #2/#3 placement: $Dxx0 = $D000 | (byte << 4). A byte of 0 means "no chip"; an
      // out-of-range byte (see ValidateSidAddress) is reported but the chip is dropped.
      if (version >= 3 && secondSid != 0) {
        sb.AppendLine($"second_sid_addr=0x{0xD000 | (secondSid << 4):X4}");
        sb.AppendLine($"sid2_model={DescribeSidModel(flags, 6)}");   // bits 6-7 → SID #2
        if (ValidateSidAddress(secondSid) is null)
          sb.AppendLine("second_sid_addr_invalid=true");
      }
      if (version >= 4 && thirdSid != 0) {
        sb.AppendLine($"third_sid_addr=0x{0xD000 | (thirdSid << 4):X4}");
        sb.AppendLine($"sid3_model={DescribeSidModel(flags, 8)}");   // bits 8-9 → SID #3
        if (ValidateSidAddress(thirdSid) is null)
          sb.AppendLine("third_sid_addr_invalid=true");
      }
    }

    // Render the start song. SID model(s) and clock come from the v2+ flags. When SID #1's
    // model flag is "unknown" (00) or "either/both" (11), the tune is rendered twice — once as
    // 6581 and once as 8580 — with the chip count deciding mono vs LEFT/RIGHT(/centre) layout.
    // Any failure (RSID, BASIC, unsupported player path, malformed program) degrades silently
    // to the existing header/program-only behaviour. See the naming/mixing notes on the helpers.
    var renderEntries = TryRenderAll(blob, sidSetup, sb);

    // C64 program: at dataOffset; when loadAddr==0 the first two LE bytes are the load address.
    if (dataOffset <= blob.Length) {
      var program = blob[dataOffset..];
      if (loadAddr == 0 && program.Length >= 2) {
        var realLoad = BinaryPrimitives.ReadUInt16LittleEndian(program);
        sb.AppendLine($"real_load_addr=0x{realLoad:X4}");
      } else if (loadAddr != 0) {
        sb.AppendLine($"real_load_addr=0x{loadAddr:X4}");
      }
      // Surface every subtune as lazily-rendered TRACK_nn.wav entries (one per chip channel:
      // TRACK_nn.wav for 1 SID, TRACK_nn_LEFT/RIGHT(/CENTER).wav for 2/3 SID). Each track uses
      // the resolved model of the default render; when that model is unknown/dual the tracks are
      // rendered with 6581 only (the track list is NOT doubled) and track_model=6581 is noted.
      var trackEntries = BuildTrackEntries(blob, sidSetup, songs, sb);

      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
      entries.AddRange(renderEntries);
      entries.AddRange(trackEntries);
      if (program.Length > 0)
        entries.Add(new("program.bin", "Stream", program));
    } else {
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
    }

    return entries;
  }

  /// <summary>Output sample rate for the rendered WAVs.</summary>
  private const int OutputSampleRate = 44100;

  /// <summary>
  /// Maximum rendered duration for a single (specified-model) render. Bounded at 30 seconds so a
  /// long or non-terminating tune still yields a reasonable, quickly-produced preview.
  /// </summary>
  private const double RenderSeconds = 30.0;

  /// <summary>
  /// Maximum rendered duration when a tune is rendered twice (model unknown/either): each render
  /// is capped at 20 seconds because up to six WAVs (3SID × two models) are produced.
  /// </summary>
  private const double DualModelRenderSeconds = 20.0;

  // PAL/NTSC SID master clock frequencies.
  private const double PalClockHz = 985248.0;
  private const double NtscClockHz = 1022727.0;

  /// <summary>The two electrically distinct models a dual (unknown/either) render covers.</summary>
  private static readonly (SidModel Model, string Suffix)[] DualModels =
    [(SidModel.Mos6581, "6581"), (SidModel.Mos8580, "8580")];

  /// <summary>A resolved multi-SID render plan: per-chip configs, the clock, and whether to dual-render.</summary>
  private readonly record struct SidSetup(IReadOnlyList<SidChipConfig> Chips, double ClockHz, bool DualModel);

  /// <summary>
  /// Resolves the per-chip model/placement plan and clock from the v2+ flags + address bytes
  /// (unknown → 6581 / PAL; v1 → single 6581 chip). SID #1's model flag drives the dual-render
  /// decision (00 unknown or 11 either → render both 6581 and 8580). SID #2/#3 model flag 00 means
  /// "like SID #1" per the v4 spec, so it inherits SID #1's resolved model. The 8580 alias 6582 is
  /// never produced from a flag (the header only distinguishes 6581 vs 8580).
  /// </summary>
  private static SidSetup ResolveSidSetup(ushort version, byte[] blob) {
    var clock = PalClockHz;
    if (version < 2 || blob.Length < 0x7C)
      return new SidSetup([new SidChipConfig(0xD400, SidModel.Mos6581)], clock, DualModel: false);

    var flags = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x76));
    if ((flags >> 2 & 0x03) == 2)
      clock = NtscClockHz;

    var sid1Flag = flags >> 4 & 0x03;
    var dualModel = sid1Flag is 0 or 3;          // unknown / either → render twice
    // When dual-rendering, SID #1's "resolved" model used as the inheritance base for "like SID #1"
    // chips is the 6581 leg; each render leg overrides SID #1's own model anyway.
    var sid1Resolved = sid1Flag == 2 ? SidModel.Mos8580 : SidModel.Mos6581;

    var chips = new List<SidChipConfig> { new(0xD400, sid1Resolved) };

    if (version >= 3) {
      var secondSid = blob[0x7A];
      if (secondSid != 0 && ValidateSidAddress(secondSid) is { } addr2)
        chips.Add(new SidChipConfig(addr2, ResolveSecondaryModel(flags >> 6 & 0x03, sid1Resolved)));
    }
    if (version >= 4) {
      var thirdSid = blob[0x7B];
      if (thirdSid != 0 && ValidateSidAddress(thirdSid) is { } addr3)
        chips.Add(new SidChipConfig(addr3, ResolveSecondaryModel(flags >> 8 & 0x03, sid1Resolved)));
    }

    return new SidSetup(chips, clock, dualModel);
  }

  /// <summary>
  /// Maps a SID #2/#3 model flag (2 bits) to a resolved model: 00 = "like SID #1" (inherit
  /// <paramref name="sid1"/>), 01 = 6581, 10 = 8580, 11 = either (treated as 8580 for the
  /// secondary chip since these are not separately dual-rendered).
  /// </summary>
  private static SidModel ResolveSecondaryModel(int flag, SidModel sid1) => flag switch {
    1 => SidModel.Mos6581,
    2 => SidModel.Mos8580,
    3 => SidModel.Mos8580,
    _ => sid1,
  };

  /// <summary>
  /// Validates a second/third SID address byte and returns its $Dxx0 absolute address, or null if
  /// invalid. Per the PSID v3/v4 spec the byte must be even and fall in $42-$7E or $E0-$FE.
  /// </summary>
  private static ushort? ValidateSidAddress(byte addrByte) {
    if ((addrByte & 0x01) != 0)
      return null;                                  // must be even
    var inLow = addrByte is >= 0x42 and <= 0x7E;
    var inHigh = addrByte is >= 0xE0 and <= 0xFE;
    if (!inLow && !inHigh)
      return null;
    return (ushort)(0xD000 | (addrByte << 4));
  }

  /// <summary>
  /// Renders the start song and produces the playable WAV entries, appending render metadata to
  /// <paramref name="sb"/>. Naming/mixing conventions:
  /// <list type="bullet">
  ///   <item><b>1 SID</b> → a single <c>MONO.wav</c>.</item>
  ///   <item><b>2 SID</b> → <c>LEFT.wav</c> (SID #1) + <c>RIGHT.wav</c> (SID #2), the common
  ///     stereo-SID convention.</item>
  ///   <item><b>3 SID</b> → <c>LEFT.wav</c> (SID #1), <c>RIGHT.wav</c> (SID #2), <c>CENTER.wav</c>
  ///     (SID #3); the centre chip is its own mono channel (a player mixes it 0.5/0.5 into both
  ///     sides). Each WAV stays a single decoded chip so the per-channel pseudo-archive model holds.</item>
  /// </list>
  /// When the model is unknown/either the whole set is emitted twice with <c>_6581</c>/<c>_8580</c>
  /// suffixes (e.g. <c>MONO_6581.wav</c> + <c>MONO_8580.wav</c>); otherwise the plain names above.
  /// Returns an empty list on any render failure.
  /// </summary>
  private static List<AudioPseudoArchive.Entry> TryRenderAll(byte[] blob, SidSetup setup, StringBuilder sb) {
    var result = new List<AudioPseudoArchive.Entry>();
    try {
      if (setup.DualModel) {
        var seconds = DualModelRenderSeconds;
        foreach (var (model, suffix) in DualModels)
          RenderLeg(blob, OverrideSid1(setup.Chips, model), setup.ClockHz, seconds, suffix, result);
        sb.AppendLine($"rendered_duration={seconds:0.#}s");
        sb.AppendLine("rendered_model=both (MOS6581 + MOS8580)");
        sb.AppendLine($"rendered_clock={(setup.ClockHz < 1_000_000 ? "PAL" : "NTSC")}");
        sb.AppendLine($"rendered_sids={setup.Chips.Count}");
      } else {
        RenderLeg(blob, setup.Chips, setup.ClockHz, RenderSeconds, suffix: null, result);
        if (result.Count > 0) {
          sb.AppendLine($"rendered_duration={RenderSeconds:0.#}s");
          sb.AppendLine($"rendered_model={DescribeModelSet(setup.Chips)}");
          sb.AppendLine($"rendered_clock={(setup.ClockHz < 1_000_000 ? "PAL" : "NTSC")}");
          sb.AppendLine($"rendered_sids={setup.Chips.Count}");
        }
      }
    } catch {
      return [];
    }
    return result;
  }

  /// <summary>Above this count tracks are still all surfaced but a note records the overflow.</summary>
  private const int MaxSurfacedTracks = 64;

  /// <summary>The exact byte size of one mono 16-bit chip WAV rendered for <see cref="RenderSeconds"/>.</summary>
  private static long MonoTrackWavSize() => 44L + (long)(RenderSeconds * OutputSampleRate) * 2;

  /// <summary>
  /// Surfaces every subtune (1-based, zero-padded) as lazily-rendered Track entries. One WAV per
  /// SID chip channel (MONO / LEFT+RIGHT / LEFT+RIGHT+CENTER), each capped at <see cref="RenderSeconds"/>.
  /// Tracks always use a single specified model: when the default render is dual (unknown/either)
  /// tracks fall back to 6581 and the choice is noted, so the track list is never doubled.
  /// </summary>
  private static List<AudioPseudoArchive.Entry> BuildTrackEntries(byte[] blob, SidSetup setup, int songs, StringBuilder sb) {
    var result = new List<AudioPseudoArchive.Entry>();
    var trackCount = songs;
    if (trackCount <= 0)
      return result;

    // Resolve each chip to a single concrete model: SID #1 → 6581 when the default render is dual.
    var trackChips = setup.DualModel ? OverrideSid1(setup.Chips, SidModel.Mos6581) : setup.Chips;

    sb.AppendLine($"total_tracks={trackCount}");
    if (setup.DualModel)
      sb.AppendLine("track_model=6581");
    if (trackCount > MaxSurfacedTracks)
      sb.AppendLine($"tracks_note=all {trackCount} subtunes surfaced (exceeds {MaxSurfacedTracks})");

    var names = ChannelNames(trackChips.Count);
    var width = Math.Max(2, trackCount.ToString().Length);
    var size = MonoTrackWavSize();

    for (var s = 1; s <= trackCount; ++s) {
      var song = s;
      var no = s.ToString().PadLeft(width, '0');
      // One render per song; cache the per-chip WAVs so additional channels are cheap.
      var lazyChips = new Lazy<byte[][]>(() => RenderTrackChips(blob, trackChips, setup.ClockHz, song));
      for (var c = 0; c < trackChips.Count; ++c) {
        var chip = c;
        var name = trackChips.Count == 1 ? $"TRACK_{no}.wav" : $"TRACK_{no}_{names[c]}.wav";
        result.Add(AudioPseudoArchive.Entry.Lazy(name, "Track", () => lazyChips.Value[chip], size, "render"));
      }
    }

    return result;
  }

  /// <summary>Renders one 1-based subtune, returning a ready WAV blob per SID chip channel.</summary>
  private static byte[][] RenderTrackChips(byte[] blob, IReadOnlyList<SidChipConfig> chips, double clockHz, int song) {
    var player = new PsidPlayer(blob, chips, clockHz, songOverride: song);
    var perChip = player.RenderPerChip(RenderSeconds, OutputSampleRate);
    var wavs = new byte[perChip.Length][];
    for (var c = 0; c < perChip.Length; ++c)
      wavs[c] = PcmCodec.ToWavBlob(ToPcmBytes(perChip[c]), channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1);
    return wavs;
  }

  /// <summary>Returns a copy of <paramref name="chips"/> with SID #1's model replaced (for a dual-render leg).</summary>
  private static IReadOnlyList<SidChipConfig> OverrideSid1(IReadOnlyList<SidChipConfig> chips, SidModel sid1) {
    var copy = chips.ToArray();
    copy[0] = copy[0] with { Model = sid1 };
    // "Like SID #1" chips followed SID #1's resolved model at parse time; re-inherit for this leg
    // any secondary chip whose model equals the original SID #1 resolution.
    var original = chips[0].Model;
    for (var i = 1; i < copy.Length; ++i)
      if (copy[i].Model == original)
        copy[i] = copy[i] with { Model = sid1 };
    return copy;
  }

  /// <summary>Renders one model leg and appends its per-chip WAV entries (mono/stereo/3SID naming).</summary>
  private static void RenderLeg(byte[] blob, IReadOnlyList<SidChipConfig> chips, double clockHz,
      double seconds, string? suffix, List<AudioPseudoArchive.Entry> sink) {
    var player = new PsidPlayer(blob, chips, clockHz);
    var perChip = player.RenderPerChip(seconds, OutputSampleRate);
    var names = ChannelNames(perChip.Length);
    for (var c = 0; c < perChip.Length; ++c) {
      var name = suffix is null ? $"{names[c]}.wav" : $"{names[c]}_{suffix}.wav";
      sink.Add(new(name, "Channel",
        PcmCodec.ToWavBlob(ToPcmBytes(perChip[c]), channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1),
        "sid"));
    }
  }

  /// <summary>The per-chip WAV base names for a given chip count (1→MONO, 2→LEFT/RIGHT, 3→+CENTER).</summary>
  private static string[] ChannelNames(int count) => count switch {
    1 => ["MONO"],
    2 => ["LEFT", "RIGHT"],
    _ => ["LEFT", "RIGHT", "CENTER"],
  };

  private static byte[] ToPcmBytes(short[] samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  /// <summary>Describes the resolved model set for metadata (single name, or a comma list for multi-SID).</summary>
  private static string DescribeModelSet(IReadOnlyList<SidChipConfig> chips)
    => string.Join(", ", chips.Select(c => c.Model == SidModel.Mos8580 ? "MOS8580" : "MOS6581"));

  /// <summary>Decodes bits 2-3 of the v2+ flags word into the SID clock standard.</summary>
  private static string DescribeClock(ushort flags) => (flags >> 2 & 0x03) switch {
    1 => "PAL",
    2 => "NTSC",
    3 => "PAL/NTSC (both)",
    _ => "unknown",
  };

  /// <summary>
  /// Decodes a 2-bit SID model field at <paramref name="shift"/> of the v2+ flags word (bits 4-5
  /// for SID #1, 6-7 for SID #2, 8-9 for SID #3). For a secondary chip the 00 case means
  /// "same as SID #1" rather than "unknown".
  /// </summary>
  private static string DescribeSidModel(ushort flags, int shift) => (flags >> shift & 0x03) switch {
    1 => "MOS6581",
    2 => "MOS8580",
    3 => "MOS6581/8580 (both)",
    _ => shift == 4 ? "unknown" : "same as SID1",
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
