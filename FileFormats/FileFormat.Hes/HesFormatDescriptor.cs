#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.HuC6280;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Hes;

/// <summary>
/// Surfaces a PC Engine (TurboGrafx-16) HES music file (<c>.hes</c>) as a metadata-rich
/// pseudo-archive. HES carries a HuC6280 program plus data that drive the PC Engine's integrated
/// 6-channel wavetable PSG. The loaded data blocks are surfaced verbatim as Kind <c>Stream</c>
/// blobs, and the start song is emulated (HuC6280 core + register-level PSG synthesis) and
/// rendered to playable per-side <c>LEFT.wav</c> / <c>RIGHT.wav</c> (44100 Hz, 16-bit). Any
/// per-subtune render is surfaced lazily as a <c>TRACK_nn_LEFT/RIGHT.wav</c> pair.
/// <para>Layout: a 0x10-byte header — magic <c>HESM</c>, u8 version, u8 firstSong, u16 initAddr
/// (request address), and an 8-byte initial MPR (memory-paging) table — followed by one or more
/// data blocks. Each data block begins with its own 0x10-byte block header: a <c>DATA</c> tag,
/// u32 length, u32 loadAddr, then padding to 0x10, after which <c>length</c> bytes of program
/// data follow. Blocks are surfaced as <c>blocks/NN_&lt;hex-loadaddr&gt;.bin</c>; if no
/// <c>DATA</c> block header is found the remainder after the file header is surfaced whole as
/// <c>program.bin</c>.</para>
/// <para>Interpretation note: the HES data-block layout is loosely specified across rippers; we
/// chase contiguous <c>DATA</c> blocks (length + loadAddr from the block header) and fall back to
/// a single <c>program.bin</c> when the structured walk yields nothing.</para>
/// Read-only; parsing degrades to FULL-only on malformed input.
/// </summary>
public sealed class HesFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Hes";
  public string DisplayName => "PC Engine HES";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".hes";
  public IReadOnlyList<string> Extensions => [".hes"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("HESM"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PC Engine HES music file; full file + header metadata + HuC6280 data blocks.";

  private const int HeaderSize = 0x10;
  private const int BlockHeaderSize = 0x10;

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
      new("FULL.hes", "Container", blob),
    };

    if (blob.Length < HeaderSize)
      return entries;

    var version = blob[0x04];
    var firstSong = blob[0x05];
    var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x06));

    var sb = new StringBuilder();
    sb.AppendLine("[hes]");
    sb.AppendLine($"version={version}");
    sb.AppendLine($"first_song={firstSong}");
    sb.AppendLine($"init_addr=0x{initAddr:X4}");
    var mpr = new string[8];
    for (var i = 0; i < 8; ++i)
      mpr[i] = $"0x{blob[0x08 + i]:X2}";
    sb.AppendLine($"initial_mpr={string.Join(' ', mpr)}");

    var blockCount = ExtractDataBlocks(blob, entries);
    if (blockCount == 0 && blob.Length > HeaderSize)
      entries.Add(new("program.bin", "Stream", blob[HeaderSize..]));

    sb.AppendLine($"data_blocks={blockCount}");

    // Render the start song to a playable stereo pair. HES init takes the song number in A; HES
    // rips store it 0-based in the header firstSong byte, so it is passed through unchanged. Any
    // failure (unsupported program, malformed data) degrades silently to the metadata/blocks-only
    // surface above.
    var rendered = TryRender(blob, firstSong);
    if (rendered is { } r) {
      sb.AppendLine($"rendered_duration={RenderSeconds:0.#}s");
      sb.AppendLine($"rendered_rate={r.FrameRateHz:0.##}Hz");
      sb.AppendLine("rendered_channels=stereo");
      sb.AppendLine($"rendered_sample_rate={OutputSampleRate}");
      sb.AppendLine($"rendered_song={firstSong}");
      // HES carries no reliable song-count field; only the start song's channels are surfaced.
      sb.AppendLine("song_count_note=HES header carries no song count; surfacing start song only");
      sb.AppendLine("rendered_status=ok");
    } else {
      sb.AppendLine("rendered_status=skipped (render unavailable for this tune)");
    }

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));

    if (rendered is { } w) {
      entries.Add(new("LEFT.wav", "Channel",
        PcmCodec.ToWavBlob(w.Left, channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1), "hes"));
      entries.Add(new("RIGHT.wav", "Channel",
        PcmCodec.ToWavBlob(w.Right, channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1), "hes"));

      // The start song is also surfaced as a lazily-rendered TRACK pair (Kind Track): listing
      // reports the exact per-side WAV byte size without rendering; each side renders on extract.
      var no = "01";
      var size = MonoWavSize(RenderSeconds);
      var lazyPair = new Lazy<(byte[] Left, byte[] Right)>(() => RenderTrackPair(blob, firstSong));
      entries.Add(AudioPseudoArchive.Entry.Lazy(
        $"TRACK_{no}_LEFT.wav", "Track", () => lazyPair.Value.Left, size, "render"));
      entries.Add(AudioPseudoArchive.Entry.Lazy(
        $"TRACK_{no}_RIGHT.wav", "Track", () => lazyPair.Value.Right, size, "render"));
    }

    return entries;
  }

  /// <summary>Output sample rate for the rendered LEFT/RIGHT WAVs.</summary>
  private const int OutputSampleRate = 44100;

  /// <summary>
  /// Maximum rendered duration. Bounded at 30 seconds so a long or non-terminating tune still
  /// yields a reasonable, quickly-produced preview.
  /// </summary>
  private const double RenderSeconds = 30.0;

  /// <summary>The exact byte size of one mono 16-bit side WAV rendered for <paramref name="seconds"/>.</summary>
  private static long MonoWavSize(double seconds) => 44L + (long)(seconds * OutputSampleRate) * 2;

  /// <summary>Renders the chosen song to per-side 16-bit mono LE PCM + frame rate, or null on failure.</summary>
  private static (byte[] Left, byte[] Right, double FrameRateHz)? TryRender(byte[] blob, int song) {
    try {
      var player = new HesPlayer(blob, song, OutputSampleRate);
      var stereo = player.RenderStereo(RenderSeconds);
      var (left, right) = Deinterleave(stereo);
      return (left, right, player.FrameRateHz);
    } catch {
      return null;
    }
  }

  /// <summary>Renders one song to a per-side LEFT/RIGHT WAV pair (used by the lazy Track entries).</summary>
  private static (byte[] Left, byte[] Right) RenderTrackPair(byte[] blob, int song) {
    var player = new HesPlayer(blob, song, OutputSampleRate);
    var stereo = player.RenderStereo(RenderSeconds);
    var (left, right) = Deinterleave(stereo);
    return (
      PcmCodec.ToWavBlob(left, channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1),
      PcmCodec.ToWavBlob(right, channels: 1, OutputSampleRate, bitsPerSample: 16, formatCode: 1));
  }

  /// <summary>Splits interleaved 16-bit stereo PCM into two per-side little-endian byte buffers.</summary>
  private static (byte[] Left, byte[] Right) Deinterleave(short[] stereo) {
    var frames = stereo.Length / 2;
    var left = new byte[frames * 2];
    var right = new byte[frames * 2];
    for (var f = 0; f < frames; ++f) {
      BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(f * 2), stereo[f * 2]);
      BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(f * 2), stereo[f * 2 + 1]);
    }
    return (left, right);
  }

  /// <summary>
  /// Walks contiguous <c>DATA</c> blocks starting after the file header. Each block header is
  /// <c>DATA</c> + u32 length + u32 loadAddr; <c>length</c> payload bytes follow at offset 0x10
  /// from the block start.
  /// </summary>
  private static int ExtractDataBlocks(byte[] blob, List<AudioPseudoArchive.Entry> entries) {
    var pos = HeaderSize;
    var count = 0;
    while (pos + BlockHeaderSize <= blob.Length) {
      if (!(blob[pos] == 'D' && blob[pos + 1] == 'A' && blob[pos + 2] == 'T' && blob[pos + 3] == 'A'))
        break;

      var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + 4));
      var loadAddr = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + 8));
      var payloadStart = pos + BlockHeaderSize;
      if (length < 0 || payloadStart + length > blob.Length)
        break;

      entries.Add(new($"blocks/{count:D2}_{loadAddr:X4}.bin", "Stream", blob[payloadStart..(payloadStart + length)]));
      ++count;
      pos = payloadStart + length;
    }
    return count;
  }
}
