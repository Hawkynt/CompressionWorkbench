#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Nes2a03;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Nsf;

/// <summary>
/// Surfaces an NES Sound Format file as a metadata-rich pseudo-archive. NSF carries a 6502
/// program that drives the NES APU (plus optional expansion sound chips); the program image is
/// surfaced verbatim as a Kind <c>Stream</c> blob alongside the parsed header, and — for base
/// 2A03 NESM tunes — including those using the Famicom expansion sound chips
/// (VRC6/VRC7/FDS/MMC5/N163/S5B) — the start song is emulated (6502 core + register-level APU
/// synthesis with the enabled expansion chips) and rendered to a playable <c>MONO.wav</c>
/// (44100 Hz, 16-bit, 30 s cap). Malformed input degrades to header/program-only.
/// <para>Two on-disk variants are handled:</para>
/// <list type="bullet">
///   <item><b>NESM</b> (magic <c>NESM\x1A</c>): a fixed 0x80-byte header followed by the 6502
///     program loaded at <c>loadAddr</c>. The header carries song counts, the load/init/play
///     vectors, NTSC/PAL speed words, an 8-byte bankswitch table, a PAL/NTSC flag byte and the
///     expansion-chip flag byte (VRC6/VRC7/FDS/MMC5/N163/S5B). The program is surfaced as
///     <c>program.bin</c>.</item>
///   <item><b>NSFE</b> (magic <c>NSFE</c>): a chunked container (4CC + u32 LE size). The
///     <c>INFO</c> chunk holds the load/init/play vectors and chip flags, <c>DATA</c> is the
///     program (surfaced as <c>program.bin</c>), <c>auth</c> carries NUL-separated
///     title/artist/copyright/ripper strings, and every other chunk is surfaced verbatim as
///     <c>metadata/&lt;id&gt;.bin</c>.</item>
/// </list>
/// Read-only; parsing degrades to FULL-only on malformed input.
/// </summary>
public sealed class NsfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Nsf";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "NES Sound Format";
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
public string DefaultExtension => ".nsf";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".nsf", ".nsfe"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("NESM\x1A"u8.ToArray(), Confidence: 0.95),
    new("NSFE"u8.ToArray(), Confidence: 0.95),
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
public string Description => "NES Sound Format (NESM/NSFE); full file + header metadata + 6502 program image.";

  private const int NesmHeaderSize = 0x80;

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

    var isNsfe = blob.Length >= 4 && blob[0] == 'N' && blob[1] == 'S' && blob[2] == 'F' && blob[3] == 'E';

    var entries = new List<AudioPseudoArchive.Entry> {
      new(isNsfe ? "FULL.nsfe" : "FULL.nsf", "Container", blob),
    };

    try {
      if (isNsfe)
        BuildNsfe(blob, entries);
      else
        BuildNesm(blob, entries);
    } catch {
      // Malformed input degrades to FULL-only.
    }

    return entries;
  }

  // ── classic NESM ───────────────────────────────────────────────────────────

  private static void BuildNesm(byte[] blob, List<AudioPseudoArchive.Entry> entries) {
    if (blob.Length < NesmHeaderSize)
      return;

    var version = blob[0x05];
    var totalSongs = blob[0x06];
    var startSong = blob[0x07];
    var loadAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x08));
    var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x0A));
    var playAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x0C));
    var name = ReadFixed(blob, 0x0E, 32);
    var artist = ReadFixed(blob, 0x2E, 32);
    var copyright = ReadFixed(blob, 0x4E, 32);
    var ntscSpeed = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x6E));
    var palSpeed = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x78));
    var palNtscFlags = blob[0x7A];
    var extraChips = blob[0x7B];

    var bankswitched = false;
    for (var i = 0; i < 8; ++i)
      if (blob[0x70 + i] != 0)
        bankswitched = true;

    var sb = new StringBuilder();
    sb.AppendLine("[nsf]");
    sb.AppendLine("variant=NESM");
    sb.AppendLine($"version={version}");
    sb.AppendLine($"total_songs={totalSongs}");
    sb.AppendLine($"start_song={startSong}");
    AppendField(sb, "name", name);
    AppendField(sb, "artist", artist);
    AppendField(sb, "copyright", copyright);
    sb.AppendLine($"load_addr=0x{loadAddr:X4}");
    sb.AppendLine($"init_addr=0x{initAddr:X4}");
    sb.AppendLine($"play_addr=0x{playAddr:X4}");
    sb.AppendLine($"ntsc_speed={ntscSpeed}");
    sb.AppendLine($"pal_speed={palSpeed}");
    sb.AppendLine($"region={DescribeRegion(palNtscFlags)}");
    sb.AppendLine($"bankswitched={(bankswitched ? "true" : "false")}");
    if (bankswitched)
      sb.AppendLine($"bankswitch={FormatBankswitch(blob, 0x70)}");
    sb.AppendLine($"expansion_chips={DescribeChips(extraChips)}");
    sb.AppendLine($"expansion_flags=0x{extraChips:X2}");

    // Render the start song to a playable mono WAV via the 6502 + 2A03 APU. Any failure
    // (expansion chip, runaway program, malformed data) degrades silently to header/program.
    var rendered = TryRenderNesm(blob);
    if (rendered is { } wav) {
      sb.AppendLine($"rendered_duration={RenderSeconds:0.#}s");
      sb.AppendLine($"rendered_sample_rate={OutputSampleRate}");
      sb.AppendLine($"rendered_song={startSong}");
      sb.AppendLine($"rendered_region={DescribeRegion(palNtscFlags)}");
      sb.AppendLine($"rendered_chips={(extraChips == 0 ? "2A03" : $"2A03, {DescribeChips(extraChips)}")}");
    }

    // Surface every subtune as a lazily-rendered TRACK_nn.wav. Listing reports the exact WAV
    // byte size (deterministic: 44 + rate*seconds*2) without rendering; each track renders only
    // when extracted. The default-song MONO.wav above is preserved for back-compat.
    var trackCount = (int)totalSongs;
    if (trackCount > 0 && rendered is not null) {
      sb.AppendLine($"total_tracks={trackCount}");
      if (trackCount > MaxSurfacedTracks)
        sb.AppendLine($"tracks_note=all {trackCount} subtunes surfaced (exceeds {MaxSurfacedTracks})");
    }

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));

    if (rendered is { } w)
      entries.Add(new("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(w, channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1), "nsf"));

    if (trackCount > 0 && rendered is not null) {
      var width = TrackNumberWidth(trackCount);
      for (var song = 1; song <= trackCount; ++song) {
        var s = song;
        entries.Add(AudioPseudoArchive.Entry.Lazy(
          $"TRACK_{s.ToString().PadLeft(width, '0')}.wav", "Track",
          () => RenderNesmTrackWav(blob, s), MonoWavSize(RenderSeconds), "render"));
      }
    }

    if (blob.Length > NesmHeaderSize)
      entries.Add(new("program.bin", "Stream", blob[NesmHeaderSize..]));
  }

  /// <summary>Above this count tracks are still all surfaced but a note records the overflow.</summary>
  private const int MaxSurfacedTracks = 64;

  /// <summary>Zero-pad width for 1-based track numbers given the total count.</summary>
  internal static int TrackNumberWidth(int count) => Math.Max(2, count.ToString().Length);

  /// <summary>The exact byte size of a mono 16-bit WAV rendered for <paramref name="seconds"/>.</summary>
  internal static long MonoWavSize(double seconds) => 44L + (long)(seconds * OutputSampleRate) * 2;

  /// <summary>Renders one 1-based subtune to a playable mono WAV blob.</summary>
  private static byte[] RenderNesmTrackWav(byte[] blob, int song) {
    var player = NsfPlayer.FromNesm(blob, song);
    var samples = player.Render(RenderSeconds, OutputSampleRate);
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return PcmCodec.ToWavBlob(pcm, channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1);
  }

  /// <summary>Output sample rate for the rendered MONO.wav.</summary>
  private const int OutputSampleRate = 44100;

  /// <summary>
  /// Maximum rendered duration. Bounded at 30 seconds so a long or non-terminating tune
  /// still yields a reasonable, quickly-produced preview.
  /// </summary>
  private const double RenderSeconds = 30.0;

  /// <summary>Renders the start song to 16-bit mono LE PCM, or null on any failure.</summary>
  private static byte[]? TryRenderNesm(byte[] blob) {
    try {
      var player = NsfPlayer.FromNesm(blob);
      var samples = player.Render(RenderSeconds, OutputSampleRate);
      var pcm = new byte[samples.Length * 2];
      for (var i = 0; i < samples.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
      return pcm;
    } catch {
      return null;
    }
  }

  private static string DescribeRegion(byte flags) => (flags & 0x03) switch {
    0 => "NTSC",
    1 => "PAL",
    _ => "NTSC/PAL (dual)",
  };

  private static string FormatBankswitch(byte[] blob, int offset) {
    var parts = new string[8];
    for (var i = 0; i < 8; ++i)
      parts[i] = $"0x{blob[offset + i]:X2}";
    return string.Join(' ', parts);
  }

  // ── extended NSFE ────────────────────────────────────────────────────────

  /// <summary>Structured INFO-chunk fields needed to drive rendering.</summary>
  private sealed class NsfeInfo {
    public ushort LoadAddr;
    public ushort InitAddr;
    public ushort PlayAddr;
    public byte Region;
    public byte Chips;
    public int TotalSongs = 1;
    public byte StartSong;
    public bool Present;
  }

  private static void BuildNsfe(byte[] blob, List<AudioPseudoArchive.Entry> entries) {
    var sb = new StringBuilder();
    sb.AppendLine("[nsf]");
    sb.AppendLine("variant=NSFE");

    var info = new NsfeInfo();
    byte[]? data = null;
    int[]? plst = null;             // playlist (0-based song order)
    int[]? times = null;            // per-song durations in ms (by absolute song index)
    string[]? labels = null;        // per-song track labels (by absolute song index)

    var pos = 4; // skip "NSFE"
    while (pos + 8 <= blob.Length) {
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos));
      var id = Encoding.ASCII.GetString(blob, pos + 4, 4);
      var dataStart = pos + 8;
      if (size < 0 || dataStart + size > blob.Length)
        break;
      var chunk = blob.AsSpan(dataStart, size);

      switch (id) {
        case "INFO":
          ParseInfoChunk(chunk, sb, info);
          break;
        case "DATA":
          data = chunk.ToArray();
          entries.Add(new("program.bin", "Stream", data));
          break;
        case "auth":
          ParseAuthChunk(chunk, sb);
          break;
        case "plst":
          plst = chunk.ToArray().Select(b => (int)b).ToArray();
          AppendBinaryChunk(id, chunk, entries);   // also surface verbatim
          break;
        case "time":
          times = ParseTimeChunk(chunk);
          AppendBinaryChunk(id, chunk, entries);   // also surface verbatim
          break;
        case "tlbl":
          labels = SplitNul(chunk).ToArray();
          AppendBinaryChunk(id, chunk, entries);   // also surface verbatim
          break;
        case "NEND":
          AppendBinaryChunk(id, chunk, entries);
          pos = dataStart + size;
          goto done;
        default:
          AppendBinaryChunk(id, chunk, entries);
          break;
      }

      pos = dataStart + size;
    }

  done:
    SurfaceNsfeTracks(info, data, plst, times, labels, sb, entries);
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
  }

  /// <summary>Maximum per-track render duration honoured from the NSFE <c>time</c> chunk.</summary>
  private const double NsfeMaxTrackSeconds = 300.0;

  /// <summary>
  /// Builds an in-memory NESM-equivalent from the parsed NSFE chunks and surfaces each playlist
  /// (or every song, when no <c>plst</c>) entry as a lazily-rendered TRACK_nn.wav. Per-song
  /// durations come from the <c>time</c> chunk (capped at five minutes), defaulting to the
  /// standard 30 s; labels from <c>tlbl</c> name the file when filename-safe, otherwise are
  /// recorded in metadata. Missing INFO/DATA or an expansion chip degrades to chunk-only output.
  /// </summary>
  private static void SurfaceNsfeTracks(NsfeInfo info, byte[]? data, int[]? plst, int[]? times,
      string[]? labels, StringBuilder sb, List<AudioPseudoArchive.Entry> entries) {
    if (!info.Present || data is null)
      return;

    // Playlist order drives track numbering when present; otherwise songs 0..total-1.
    var order = plst is { Length: > 0 } ? plst : Enumerable.Range(0, Math.Max(1, info.TotalSongs)).ToArray();
    if (order.Length == 0)
      return;

    var width = TrackNumberWidth(order.Length);
    sb.AppendLine($"total_tracks={order.Length}");
    if (order.Length > MaxSurfacedTracks)
      sb.AppendLine($"tracks_note=all {order.Length} subtunes surfaced (exceeds {MaxSurfacedTracks})");

    for (var i = 0; i < order.Length; ++i) {
      var songIndex = order[i];                 // 0-based absolute song index
      var seconds = SongSeconds(times, songIndex);
      var label = SongLabel(labels, songIndex);
      var trackNo = (i + 1).ToString().PadLeft(width, '0');

      var name = $"TRACK_{trackNo}.wav";
      if (label is { Length: > 0 }) {
        var safe = SanitizeLabel(label);
        if (safe.Length > 0)
          name = $"TRACK_{trackNo} {safe}.wav";
        else
          sb.AppendLine($"track_{trackNo}_label={label}");
      }

      var nesm = info;            // capture for closure
      var localData = data;
      var song1Based = songIndex + 1;
      var sec = seconds;
      entries.Add(AudioPseudoArchive.Entry.Lazy(
        name, "Track",
        () => RenderNsfeTrackWav(nesm, localData, song1Based, sec),
        MonoWavSize(sec), "render"));
    }
  }

  /// <summary>Per-song render duration: time-chunk ms (capped at five minutes) or the 30 s default.</summary>
  private static double SongSeconds(int[]? times, int songIndex) {
    if (times is null || songIndex < 0 || songIndex >= times.Length)
      return RenderSeconds;
    var ms = times[songIndex];
    if (ms <= 0)
      return RenderSeconds;
    return Math.Min(ms / 1000.0, NsfeMaxTrackSeconds);
  }

  private static string? SongLabel(string[]? labels, int songIndex)
    => labels is not null && songIndex >= 0 && songIndex < labels.Length ? labels[songIndex] : null;

  /// <summary>Renders an NSFE subtune by assembling an equivalent NESM image and reusing the player.</summary>
  private static byte[] RenderNsfeTrackWav(NsfeInfo info, byte[] data, int song1Based, double seconds) {
    var nesm = BuildEquivalentNesm(info, data);
    var player = NsfPlayer.FromNesm(nesm, song1Based);
    var samples = player.Render(seconds, OutputSampleRate);
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return PcmCodec.ToWavBlob(pcm, channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1);
  }

  /// <summary>Assembles a 0x80-byte NESM header + DATA program that <see cref="NsfPlayer"/> can drive.</summary>
  private static byte[] BuildEquivalentNesm(NsfeInfo info, byte[] data) {
    var nesm = new byte[NesmHeaderSize + data.Length];
    "NESM\x1A"u8.CopyTo(nesm);
    nesm[0x05] = 1;
    nesm[0x06] = (byte)Math.Clamp(info.TotalSongs, 1, 255);
    nesm[0x07] = (byte)(info.StartSong + 1);  // header start-song is 1-based
    BinaryPrimitives.WriteUInt16LittleEndian(nesm.AsSpan(0x08), info.LoadAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(nesm.AsSpan(0x0A), info.InitAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(nesm.AsSpan(0x0C), info.PlayAddr);
    nesm[0x7A] = info.Region;
    nesm[0x7B] = info.Chips;
    data.CopyTo(nesm, NesmHeaderSize);
    return nesm;
  }

  /// <summary>Parses the NSFE <c>time</c> chunk: a sequence of little-endian signed 32-bit ms values.</summary>
  private static int[] ParseTimeChunk(ReadOnlySpan<byte> chunk) {
    var n = chunk.Length / 4;
    var times = new int[n];
    for (var i = 0; i < n; ++i)
      times[i] = BinaryPrimitives.ReadInt32LittleEndian(chunk[(i * 4)..]);
    return times;
  }

  /// <summary>Strips characters that are unsafe in a filename; collapses runs of whitespace.</summary>
  private static string SanitizeLabel(string label) {
    var chars = label.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' ? c : ' ').ToArray();
    return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
  }

  private static void ParseInfoChunk(ReadOnlySpan<byte> chunk, StringBuilder sb, NsfeInfo info) {
    info.Present = true;
    if (chunk.Length >= 6) {
      info.LoadAddr = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
      info.InitAddr = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
      info.PlayAddr = BinaryPrimitives.ReadUInt16LittleEndian(chunk[4..]);
      sb.AppendLine($"load_addr=0x{info.LoadAddr:X4}");
      sb.AppendLine($"init_addr=0x{info.InitAddr:X4}");
      sb.AppendLine($"play_addr=0x{info.PlayAddr:X4}");
    }
    if (chunk.Length >= 7) {
      info.Region = chunk[6];
      sb.AppendLine($"region={DescribeRegion(chunk[6])}");
    }
    if (chunk.Length >= 8) {
      info.Chips = chunk[7];
      sb.AppendLine($"expansion_chips={DescribeChips(chunk[7])}");
      sb.AppendLine($"expansion_flags=0x{chunk[7]:X2}");
    }
    // NSFE INFO: song count is stored as the count itself (default 1 when absent).
    info.TotalSongs = chunk.Length >= 9 ? Math.Max(1, (int)chunk[8]) : 1;
    if (chunk.Length >= 9)
      sb.AppendLine($"total_songs={chunk[8]}");
    if (chunk.Length >= 10) {
      info.StartSong = chunk[9];
      sb.AppendLine($"start_song={chunk[9]}");
    }
  }

  private static void ParseAuthChunk(ReadOnlySpan<byte> chunk, StringBuilder sb) {
    var strings = SplitNul(chunk);
    string[] keys = ["name", "artist", "copyright", "ripper"];
    for (var i = 0; i < strings.Count && i < keys.Length; ++i)
      AppendField(sb, keys[i], strings[i]);
  }

  private static void AppendBinaryChunk(string id, ReadOnlySpan<byte> chunk, List<AudioPseudoArchive.Entry> entries) {
    var safe = new string([.. id.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-')]);
    if (safe.Length == 0)
      safe = "chunk";
    entries.Add(new($"metadata/{safe}.bin", "Stream", chunk.ToArray()));
  }

  private static List<string> SplitNul(ReadOnlySpan<byte> data) {
    var result = new List<string>();
    var start = 0;
    for (var i = 0; i < data.Length; ++i) {
      if (data[i] != 0)
        continue;
      result.Add(Encoding.UTF8.GetString(data[start..i]));
      start = i + 1;
    }
    if (start < data.Length)
      result.Add(Encoding.UTF8.GetString(data[start..]));
    return result;
  }

  // ── shared ─────────────────────────────────────────────────────────────────

  /// <summary>Decodes the NSF expansion-chip flag byte into a human-readable chip list.</summary>
  private static string DescribeChips(byte flags) {
    if (flags == 0)
      return "none";
    var chips = new List<string>();
    if ((flags & 0x01) != 0) chips.Add("VRC6");
    if ((flags & 0x02) != 0) chips.Add("VRC7");
    if ((flags & 0x04) != 0) chips.Add("FDS");
    if ((flags & 0x08) != 0) chips.Add("MMC5");
    if ((flags & 0x10) != 0) chips.Add("N163");
    if ((flags & 0x20) != 0) chips.Add("S5B");
    return chips.Count > 0 ? string.Join(", ", chips) : "none";
  }

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
