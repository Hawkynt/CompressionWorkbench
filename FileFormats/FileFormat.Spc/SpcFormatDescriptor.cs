#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Brr;
using Codec.Pcm;
using Codec.Spc700;
using Compression.Registry;

namespace FileFormat.Spc;

/// <summary>
/// Exposes an SPC700 sound-file save-state (<c>.spc</c>) as a pseudo-archive: <c>FULL.spc</c>
/// (the byte-exact save state), the ID666 tag block as <c>metadata.ini</c>, one decoded mono
/// WAV per extractable BRR sample (<c>samples/NN.wav</c>, 32000 Hz), and — when the tune can be
/// emulated — the rendered stereo song as <c>LEFT.wav</c> / <c>RIGHT.wav</c> (32000 Hz). The
/// render boots the SPC700 CPU and S-DSP from the snapshot (see <c>Codec.Spc700</c>).
/// <para>An SPC file is a 0x10180-byte snapshot of the SNES audio subsystem: a 33-byte
/// signature, an ID666 tag block at <c>0x2E</c>, the 64&#160;KB APU RAM (ARAM) at
/// <c>0x100</c>, and the 128 S-DSP registers at <c>0x10100</c>. Samples are located via the
/// S-DSP <c>DIR</c> register (<c>0x5D</c>): the sample directory lives at
/// <c>ARAM[DIR &#215; 0x100]</c> as up to 256 four-byte entries (u16 LE start address, u16 LE
/// loop address). Each referenced BRR chain is walked to its end-flagged block and decoded.</para>
/// Read-only — reconstructing a playable save state requires a full APU snapshot.
/// </summary>
public sealed class SpcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Spc";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "SNES SPC700";
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
public string DefaultExtension => ".spc";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".spc"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SNES-SPC700 Sound File Data"u8.ToArray(), Confidence: 0.95),
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
public string Description => "SNES SPC700 save state; full file + ID666 tags + decoded BRR samples.";

  private const int HasId666Offset = 0x23;
  private const int Id666Offset = 0x2E;
  private const int AramOffset = 0x100;
  private const int AramSize = 0x10000;
  private const int DspOffset = 0x10100;
  private const int DspSize = 128;
  private const int DirRegister = 0x5D;
  private const int SampleRate = 32000;

  // Sanity bounds for accepting a decoded BRR sample run.
  private const int MinDecodedSamples = 16;
  private const int MaxBrrBytes = AramSize; // a chain can't exceed ARAM.
  private const int MaxInvalidRun = 4;      // tolerate a few bad directory slots, then stop.

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

  // ── parsing ────────────────────────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.spc", "Container", blob),
    };

    // Render the tune to stereo (best-effort; failure leaves the rest of the archive intact).
    var renderInfo = RenderChannels(blob, entries);

    // ID666 tags (best-effort; absent or unparsable → skip).
    var ini = BuildMetadataIni(blob);
    if (renderInfo is { } info)
      ini += $"rendered_seconds={info.Seconds}\nrendered_source={(info.FromTag ? "id666" : "default")}\n";
    if (ini.Length > 0)
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini)));

    // BRR samples via the DSP DIR register and ARAM directory.
    if (blob.Length >= DspOffset + DspSize) {
      var aram = blob.AsSpan(AramOffset, AramSize);
      var dir = blob[DspOffset + DirRegister];
      ExtractSamples(aram, dir, entries);
    }

    return entries;
  }

  /// <summary>
  /// Boots the SPC700 + S-DSP from the snapshot and surfaces the rendered song as
  /// <c>LEFT.wav</c> / <c>RIGHT.wav</c>. Any failure (short blob, emulation error) leaves the
  /// pseudo-archive at its sample-only behaviour and returns <see langword="null"/>.
  /// </summary>
  private static (int Seconds, bool FromTag)? RenderChannels(byte[] blob, List<AudioPseudoArchive.Entry> entries) {
    if (blob.Length < DspOffset + DspSize)
      return null;
    try {
      var player = new SpcPlayer(blob);
      var (left, right) = player.RenderStereoChannels();
      entries.Add(new("LEFT.wav", "Channel",
        PcmCodec.ToWavBlob(left, channels: 1, SampleRate, bitsPerSample: 16, formatCode: 1), "spc700"));
      entries.Add(new("RIGHT.wav", "Channel",
        PcmCodec.ToWavBlob(right, channels: 1, SampleRate, bitsPerSample: 16, formatCode: 1), "spc700"));
      return (player.DurationSeconds, player.DurationFromTag);
    } catch {
      return null;
    }
  }

  /// <summary>
  /// Walks the sample directory at <c>ARAM[dir * 0x100]</c>. Directory slots are contiguous,
  /// so the walk stops once a run of invalid entries (out-of-range / null / undecodable BRR)
  /// reaches <see cref="MaxInvalidRun"/>, which marks the end of the real table.
  /// </summary>
  private static void ExtractSamples(ReadOnlySpan<byte> aram, byte dir, List<AudioPseudoArchive.Entry> entries) {
    var dirBase = dir * 0x100;
    if (dirBase < 0 || dirBase + 4 > aram.Length)
      return;

    var emitted = 0;
    var invalidRun = 0;
    for (var i = 0; i < 256; ++i) {
      var entryOffset = dirBase + i * 4;
      if (entryOffset + 4 > aram.Length)
        break;

      var start = BinaryPrimitives.ReadUInt16LittleEndian(aram.Slice(entryOffset, 2));
      if (!TryDecodeChain(aram, start, out var samples)) {
        if (++invalidRun >= MaxInvalidRun)
          break;
        continue;
      }

      invalidRun = 0;
      var pcm = ShortsToLePcm(samples);
      entries.Add(new($"samples/{emitted:D2}.wav", "Sample",
        PcmCodec.ToWavBlob(pcm, channels: 1, SampleRate, bitsPerSample: 16, formatCode: 1), "brr"));
      ++emitted;
    }
  }

  /// <summary>
  /// Validates and decodes a BRR chain starting at <paramref name="start"/> inside ARAM. The
  /// chain must begin at a real address (not 0, not 0xFFFF), terminate on an end-flagged block
  /// within ARAM, fit inside <see cref="MaxBrrBytes"/>, and yield at least
  /// <see cref="MinDecodedSamples"/> samples.
  /// </summary>
  private static bool TryDecodeChain(ReadOnlySpan<byte> aram, int start, out short[] samples) {
    samples = [];
    if (start is 0 or 0xFFFF || start + BrrCodec.BlockSize > aram.Length)
      return false;

    // Find the end-flagged terminating block without overrunning ARAM or the size budget.
    var pos = start;
    var ended = false;
    while (pos + BrrCodec.BlockSize <= aram.Length && pos - start < MaxBrrBytes) {
      var header = aram[pos];
      pos += BrrCodec.BlockSize;
      if ((header & 0x01) != 0) {
        ended = true;
        break;
      }
    }
    if (!ended)
      return false;

    var chainLength = pos - start;
    var decoded = BrrCodec.Decode(aram.Slice(start, chainLength));
    if (decoded.Length < MinDecodedSamples)
      return false;

    samples = decoded;
    return true;
  }

  // ── ID666 ────────────────────────────────────────────────────────────────

  private static string BuildMetadataIni(byte[] blob) {
    if (blob.Length < AramOffset)
      return string.Empty;

    // hasId666: 0x1A = yes, 0x1B = no. Anything else: assume text-format tags present.
    var marker = blob[HasId666Offset];
    if (marker == 0x1B)
      return string.Empty;

    var sb = new StringBuilder();
    AppendField(sb, "song_title", blob, Id666Offset + 0x00, 32);
    AppendField(sb, "game_title", blob, Id666Offset + 0x20, 32);
    AppendField(sb, "dumper", blob, Id666Offset + 0x40, 16);
    AppendField(sb, "comments", blob, Id666Offset + 0x50, 32);
    AppendField(sb, "dump_date", blob, Id666Offset + 0x70, 11);
    AppendField(sb, "artist", blob, 0xB1, 32);
    return sb.ToString();
  }

  private static void AppendField(StringBuilder sb, string key, byte[] blob, int offset, int length) {
    if (offset + length > blob.Length)
      return;
    var raw = blob.AsSpan(offset, length);
    var end = raw.IndexOf((byte)0);
    if (end < 0)
      end = raw.Length;
    var value = Encoding.Latin1.GetString(raw[..end]).Trim();
    if (value.Length > 0)
      sb.AppendLine($"{key}={value}");
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
