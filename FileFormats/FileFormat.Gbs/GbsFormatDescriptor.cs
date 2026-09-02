#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.GameBoyApu;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Gbs;

/// <summary>
/// Surfaces a Game Boy Sound file (<c>.gbs</c>) as a metadata-rich pseudo-archive. GBS carries
/// a Game Boy CPU (LR35902/SM83) program that drives the DMG sound hardware. The program image
/// is surfaced verbatim as a Kind <c>Stream</c> blob, and the first song is emulated (SM83 core
/// + register-level Game Boy APU synthesis) and rendered to playable per-side <c>LEFT.wav</c> /
/// <c>RIGHT.wav</c> (44100 Hz, 16-bit), honouring the header's timer-driven or VBlank play rate.
/// <para>Layout: a 0x70-byte header (magic <c>GBS</c> + version 1, song counts, the
/// load/init/play vectors, stack pointer, timer modulo/control bytes, and three 32-byte
/// title/author/copyright strings) followed by the program loaded at <c>loadAddr</c>. The
/// program is surfaced as <c>program.bin</c>.</para>
/// Read-only; parsing degrades to header/program-only on malformed input or unsupported tunes.
/// </summary>
public sealed class GbsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Gbs";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Game Boy Sound";
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
public string DefaultExtension => ".gbs";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".gbs"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x47, 0x42, 0x53, 0x01], Confidence: 0.95), // "GBS" + version 1
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
public string Description => "Game Boy Sound (.gbs); full file + header metadata + LR35902 program image.";

  private const int HeaderSize = 0x70;

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
      new("FULL.gbs", "Container", blob),
    };

    if (blob.Length < HeaderSize)
      return entries;

    var version = blob[0x03];
    var numSongs = blob[0x04];
    var firstSong = blob[0x05];
    var loadAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x06));
    var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x08));
    var playAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x0A));
    var stackPtr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x0C));
    var timerModulo = blob[0x0E];
    var timerControl = blob[0x0F];
    var title = ReadFixed(blob, 0x10, 32);
    var author = ReadFixed(blob, 0x30, 32);
    var copyright = ReadFixed(blob, 0x50, 32);

    var sb = new StringBuilder();
    sb.AppendLine("[gbs]");
    sb.AppendLine($"version={version}");
    sb.AppendLine($"num_songs={numSongs}");
    sb.AppendLine($"first_song={firstSong}");
    sb.AppendLine($"load_addr=0x{loadAddr:X4}");
    sb.AppendLine($"init_addr=0x{initAddr:X4}");
    sb.AppendLine($"play_addr=0x{playAddr:X4}");
    sb.AppendLine($"stack_ptr=0x{stackPtr:X4}");
    sb.AppendLine($"timer_modulo=0x{timerModulo:X2}");
    sb.AppendLine($"timer_control=0x{timerControl:X2}");
    AppendField(sb, "title", title);
    AppendField(sb, "author", author);
    AppendField(sb, "copyright", copyright);

    // Render the first song to a playable stereo pair. The init A register is 0-based, so the
    // 1-based header firstSong maps to (firstSong - 1). Any failure (unsupported player path,
    // malformed program) degrades silently to the existing header/program-only behaviour.
    var song = firstSong < 1 ? 0 : firstSong - 1;
    var rendered = TryRender(blob, song);
    if (rendered is { } r) {
      sb.AppendLine($"rendered_duration={RenderSeconds:0.#}s");
      sb.AppendLine($"rendered_rate={r.FrameRateHz:0.##}Hz");
      sb.AppendLine($"rendered_channels=stereo");
      sb.AppendLine($"rendered_sample_rate={OutputSampleRate}");
    }

    // Surface every subtune as a lazily-rendered stereo TRACK_nn_LEFT/RIGHT.wav pair. Listing
    // reports the exact per-side WAV byte size without rendering; each pair renders on extraction.
    // The default-song LEFT/RIGHT above are preserved for back-compat.
    var trackCount = (int)numSongs;
    if (trackCount > 0 && rendered is not null) {
      sb.AppendLine($"total_tracks={trackCount}");
      if (trackCount > MaxSurfacedTracks)
        sb.AppendLine($"tracks_note=all {trackCount} subtunes surfaced (exceeds {MaxSurfacedTracks})");
    }

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));

    if (rendered is { } w) {
      entries.Add(new("LEFT.wav", "Channel",
        PcmCodec.ToWavBlob(w.Left, channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1), "gbs"));
      entries.Add(new("RIGHT.wav", "Channel",
        PcmCodec.ToWavBlob(w.Right, channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1), "gbs"));
    }

    if (trackCount > 0 && rendered is not null) {
      var width = Math.Max(2, trackCount.ToString().Length);
      var size = MonoWavSize(RenderSeconds);
      for (var s = 0; s < trackCount; ++s) {
        var trackSong = s;            // 0-based song index for the player
        var no = (s + 1).ToString().PadLeft(width, '0');
        // Render once per pair; cache the deinterleaved sides so LEFT triggers RIGHT cheaply.
        var lazyPair = new Lazy<(byte[] Left, byte[] Right)>(() => RenderTrackPair(blob, trackSong));
        entries.Add(AudioPseudoArchive.Entry.Lazy(
          $"TRACK_{no}_LEFT.wav", "Track", () => lazyPair.Value.Left, size, "render"));
        entries.Add(AudioPseudoArchive.Entry.Lazy(
          $"TRACK_{no}_RIGHT.wav", "Track", () => lazyPair.Value.Right, size, "render"));
      }
    }

    if (blob.Length > HeaderSize)
      entries.Add(new("program.bin", "Stream", blob[HeaderSize..]));

    return entries;
  }

  /// <summary>Above this count tracks are still all surfaced but a note records the overflow.</summary>
  private const int MaxSurfacedTracks = 64;

  /// <summary>The exact byte size of one mono 16-bit side WAV rendered for <paramref name="seconds"/>.</summary>
  private static long MonoWavSize(double seconds) => 44L + (long)(seconds * OutputSampleRate) * 2;

  /// <summary>Renders one 0-based subtune to a per-side LEFT/RIGHT WAV pair.</summary>
  private static (byte[] Left, byte[] Right) RenderTrackPair(byte[] blob, int song) {
    var player = new GbsPlayer(blob, song, OutputSampleRate);
    var stereo = player.Render(RenderSeconds);
    var frames = stereo.Length / 2;
    var left = new byte[frames * 2];
    var right = new byte[frames * 2];
    for (var f = 0; f < frames; ++f) {
      BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(f * 2), stereo[f * 2]);
      BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(f * 2), stereo[f * 2 + 1]);
    }
    return (
      PcmCodec.ToWavBlob(left, channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1),
      PcmCodec.ToWavBlob(right, channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1));
  }

  /// <summary>Output sample rate for the rendered LEFT/RIGHT WAVs.</summary>
  private const int OutputSampleRate = 44100;

  /// <summary>
  /// Maximum rendered duration. Bounded at 30 seconds so a long or non-terminating tune still
  /// yields a reasonable, quickly-produced preview.
  /// </summary>
  private const double RenderSeconds = 30.0;

  /// <summary>
  /// Renders the chosen song to per-side 16-bit mono LE PCM, or null on any failure.
  /// </summary>
  private static (byte[] Left, byte[] Right, double FrameRateHz)? TryRender(byte[] blob, int song) {
    try {
      var player = new GbsPlayer(blob, song, OutputSampleRate);
      var stereo = player.Render(RenderSeconds);
      var frames = stereo.Length / 2;
      var left = new byte[frames * 2];
      var right = new byte[frames * 2];
      for (var f = 0; f < frames; ++f) {
        BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(f * 2), stereo[f * 2]);
        BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(f * 2), stereo[f * 2 + 1]);
      }
      return (left, right, player.FrameRateHz);
    } catch {
      return null;
    }
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
