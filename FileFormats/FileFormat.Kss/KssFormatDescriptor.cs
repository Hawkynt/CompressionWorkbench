#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Ay8910;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Kss;

/// <summary>
/// Surfaces a KSS music file (<c>.kss</c>) as a metadata-rich pseudo-archive. KSS carries a Z80
/// program plus sound-chip data for MSX/SG-1000/Master System hardware; there is no audio to
/// decode, so the data image is surfaced verbatim as a Kind <c>Stream</c> blob.
/// <para>Two magic variants exist. The classic <c>KSCC</c> header is 0x10 bytes: u16 loadAddr,
/// u16 dataLen, u16 initAddr, u16 playAddr, u8 startBank, u8 extraBanks, u8 reserved/extraHeader,
/// u8 deviceFlags. The <c>KSSX</c> variant uses the same 0x10-byte core but the
/// <c>reserved/extraHeader</c> byte (offset 0x0E) declares an extra header length; when it is
/// 0x10 a second 0x10-byte block follows at 0x10 carrying chip-extra flags and the
/// firstSong/songCount words. The Z80 data begins immediately after the (combined) header. The
/// data is surfaced as <c>program.bin</c>.</para>
/// <para>Interpretation note (KSSX extension is sparsely documented): we treat offset 0x0E as
/// the extra-header length. When it is non-zero and the bytes are present, the extension block
/// is parsed for the deviceFlags-extra byte plus the u16 firstSong / u16 songCount words and the
/// payload offset advances past it; otherwise we fall back to the plain 0x10-byte layout.</para>
/// Read-only; parsing degrades to FULL-only on malformed input.
/// </summary>
public sealed class KssFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Kss";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "KSS (MSX/SMS music)";
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
  public string DefaultExtension => ".kss";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".kss"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("KSCC"u8.ToArray(), Confidence: 0.95),
    new("KSSX"u8.ToArray(), Confidence: 0.95),
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
  public string Description => "KSS music file (KSCC/KSSX); full file + header metadata + Z80 data image.";

  private const int CoreHeaderSize = 0x10;

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
      new("FULL.kss", "Container", blob),
    };

    if (blob.Length < CoreHeaderSize)
      return entries;

    var isKssx = blob[0] == 'K' && blob[1] == 'S' && blob[2] == 'S' && blob[3] == 'X';

    var loadAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x04));
    var dataLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x06));
    var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x08));
    var playAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x0A));
    var startBank = blob[0x0C];
    var extraBanks = blob[0x0D];
    var extraHeaderLen = blob[0x0E];
    var deviceFlags = blob[0x0F];

    var sb = new StringBuilder();
    sb.AppendLine("[kss]");
    sb.AppendLine($"variant={(isKssx ? "KSSX" : "KSCC")}");
    sb.AppendLine($"load_addr=0x{loadAddr:X4}");
    sb.AppendLine($"data_len=0x{dataLen:X4}");
    sb.AppendLine($"init_addr=0x{initAddr:X4}");
    sb.AppendLine($"play_addr=0x{playAddr:X4}");
    sb.AppendLine($"start_bank=0x{startBank:X2}");
    sb.AppendLine($"extra_banks=0x{extraBanks:X2}");
    sb.AppendLine($"extra_header_len=0x{extraHeaderLen:X2}");
    sb.AppendLine($"device_flags=0x{deviceFlags:X2}");
    sb.AppendLine($"devices={DescribeDevices(deviceFlags)}");

    var payloadOffset = CoreHeaderSize;

    // Only KSSX extended headers declare a song count; a plain KSCC tune has no subtune table and
    // therefore gets NO per-track list (just the default LEFT/RIGHT render below).
    var songCount = 0;
    var firstSong = 0;

    // KSSX extension block: present when offset 0x0E declares a non-zero extra-header length and
    // the bytes are actually present in the file.
    if (isKssx && extraHeaderLen > 0 && blob.Length >= CoreHeaderSize + extraHeaderLen) {
      var ext = blob.AsSpan(CoreHeaderSize, extraHeaderLen);
      if (ext.Length >= 1)
        sb.AppendLine($"extra_device_flags=0x{ext[0]:X2}");
      if (ext.Length >= 3) {
        firstSong = BinaryPrimitives.ReadUInt16LittleEndian(ext[1..]);
        sb.AppendLine($"first_song={firstSong}");
      }
      if (ext.Length >= 5) {
        songCount = BinaryPrimitives.ReadUInt16LittleEndian(ext[3..]);
        sb.AppendLine($"song_count={songCount}");
      }
      payloadOffset = CoreHeaderSize + extraHeaderLen;
    }

    var renderable = AddRenderedChannels(blob, entries, sb, deviceFlags);
    var trackEntries = BuildTrackEntries(blob, songCount, renderable, sb);

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
    entries.AddRange(trackEntries);

    if (blob.Length > payloadOffset)
      entries.Add(new("program.bin", "Stream", blob[payloadOffset..]));

    return entries;
  }

  /// <summary>Above this count tracks are still all surfaced but a note records the overflow.</summary>
  private const int MaxSurfacedTracks = 64;

  private const double RenderSeconds = 30.0;

  /// <summary>The exact byte size of one mono 16-bit side WAV rendered for the 30 s cap.</summary>
  private static long MonoWavSize() => 44L + (long)(RenderSeconds * Ay8910Chip.OutputSampleRate) * 2;

  /// <summary>
  /// Builds lazily-rendered stereo TRACK_nn_LEFT/RIGHT.wav pairs for every subtune (1-based,
  /// zero-padded), driven only for KSSX tunes that declare a song count. Plain KSCC tunes pass
  /// <paramref name="songCount"/> 0 and get no track list.
  /// </summary>
  private static List<AudioPseudoArchive.Entry> BuildTrackEntries(byte[] blob, int songCount, bool renderable, StringBuilder sb) {
    var result = new List<AudioPseudoArchive.Entry>();
    if (!renderable || songCount <= 0)
      return result;

    sb.AppendLine($"total_tracks={songCount}");
    if (songCount > MaxSurfacedTracks)
      sb.AppendLine($"tracks_note=all {songCount} subtunes surfaced (exceeds {MaxSurfacedTracks})");

    var width = Math.Max(2, songCount.ToString().Length);
    var size = MonoWavSize();
    for (var s = 0; s < songCount; ++s) {
      var song = s;                 // 0-based song index
      var no = (s + 1).ToString().PadLeft(width, '0');
      var lazyPair = new Lazy<(byte[] Left, byte[] Right)>(() => RenderTrackPair(blob, song));
      result.Add(AudioPseudoArchive.Entry.Lazy(
        $"TRACK_{no}_LEFT.wav", "Track", () => lazyPair.Value.Left, size, "render"));
      result.Add(AudioPseudoArchive.Entry.Lazy(
        $"TRACK_{no}_RIGHT.wav", "Track", () => lazyPair.Value.Right, size, "render"));
    }
    return result;
  }

  /// <summary>Renders one 0-based subtune to a per-side LEFT/RIGHT WAV pair.</summary>
  private static (byte[] Left, byte[] Right) RenderTrackPair(byte[] blob, int song) {
    var player = new KssPlayer(blob, songIndex: song);
    var stereo = player.Render(RenderSeconds);
    var (left, right) = DeinterleaveStereo(stereo);
    return (
      PcmCodec.ToWavBlob(left, 1, Ay8910Chip.OutputSampleRate, 16),
      PcmCodec.ToWavBlob(right, 1, Ay8910Chip.OutputSampleRate, 16));
  }

  /// <summary>
  /// Plays the first song through the Z80 + MSX PSG player and surfaces rendered
  /// <c>LEFT.wav</c> / <c>RIGHT.wav</c> (Kind <c>Channel</c>, 44.1 kHz, 30 s cap). When the
  /// header enables a chip beyond the PSG (FMPAC/SCC/MSX-AUDIO), only the PSG voices are
  /// rendered and a note records that. Failure degrades silently to FULL + metadata only.
  /// </summary>
  private static bool AddRenderedChannels(byte[] blob, List<AudioPseudoArchive.Entry> entries, StringBuilder sb, byte deviceFlags) {
    try {
      var player = new KssPlayer(blob, songIndex: 0);
      var stereo = player.Render(RenderSeconds);
      var (left, right) = DeinterleaveStereo(stereo);
      entries.Add(new("LEFT.wav", "Channel", PcmCodec.ToWavBlob(left, 1, Ay8910Chip.OutputSampleRate, 16), "pcm"));
      entries.Add(new("RIGHT.wav", "Channel", PcmCodec.ToWavBlob(right, 1, Ay8910Chip.OutputSampleRate, 16), "pcm"));
      sb.AppendLine("rendered=LEFT.wav,RIGHT.wav");
      sb.AppendLine("rendered_seconds=30");
      sb.AppendLine("rendered_rate=44100");
      sb.AppendLine("rendered_chip=PSG (AY-3-8910 compatible)");
      if (deviceFlags != 0)
        sb.AppendLine("rendered_note=PSG voices only; extra devices (FMPAC/SCC/MSX-AUDIO) not synthesised");
      return true;
    } catch {
      // Undecodable — FULL + metadata only.
      return false;
    }
  }

  private static (byte[] Left, byte[] Right) DeinterleaveStereo(short[] stereo) {
    var frames = stereo.Length / 2;
    var left = new byte[frames * 2];
    var right = new byte[frames * 2];
    for (var f = 0; f < frames; ++f) {
      BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(f * 2), stereo[f * 2]);
      BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(f * 2), stereo[f * 2 + 1]);
    }
    return (left, right);
  }

  /// <summary>Decodes the KSS device-flags byte into the enabled sound chips.</summary>
  private static string DescribeDevices(byte flags) {
    if (flags == 0)
      return "PSG only";
    var devices = new List<string> { "PSG" };
    if ((flags & 0x01) != 0) devices.Add("FMPAC");
    if ((flags & 0x02) != 0) devices.Add("SCC");
    if ((flags & 0x04) != 0) devices.Add("MSX-MUSIC (FM)");
    if ((flags & 0x08) != 0) devices.Add("MSX-AUDIO");
    return string.Join(", ", devices);
  }
}
