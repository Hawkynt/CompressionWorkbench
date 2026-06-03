#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.XaAdpcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Xa;

/// <summary>
/// Exposes a CD-ROM XA / PlayStation streaming-ADPCM audio file (<c>.xa</c>) as a
/// pseudo-archive of <c>FULL.xa</c> (the byte-exact container, Kind <c>Container</c>)
/// plus one decoded mono <c>MONO.wav</c> or stereo <c>LEFT.wav</c>/<c>RIGHT.wav</c>
/// (Kind <c>Channel</c>) at the coding-info sample rate, plus a <c>metadata.ini</c>
/// (Kind <c>Tag</c>) carrying rate, channel layout, file/channel ids and sector count.
/// <para>Two on-disk layouts are recognised:
/// <list type="bullet">
///   <item>RIFF/CDXA: <c>"RIFF" | u32 size | "CDXA"</c>, an <c>fmt </c> chunk and a
///     <c>data</c> chunk of raw 2352-byte CD sectors.</item>
///   <item>raw sectors: bare 2352-byte Mode-2 sectors (12-byte sync
///     <c>00 FF×10 00</c> + 3-byte address + mode + 8-byte subheader) or 2336-byte
///     Mode-2 sectors that begin directly with the 8-byte subheader.</item>
/// </list>
/// Only sectors whose XA submode marks them as AUDIO carry sound; the descriptor
/// concatenates every audio sector belonging to the FIRST (file#, channel#) stream it
/// encounters and notes any other streams in the metadata. Inputs the decoder can't
/// handle gracefully degrade to a FULL-only listing.</para>
/// </summary>
public sealed class XaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  public string Id => "Xa";
  public string DisplayName => "CD-XA audio (.xa)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".xa";
  public IReadOnlyList<string> Extensions => [".xa"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // RIFF/CDXA: "CDXA" form type at offset 8.
    new("CDXA"u8.ToArray(), Offset: 8, Confidence: 0.95),
    // Raw 2352-byte sectors: the 12-byte CD sync pattern 00 FF×10 00.
    new([0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00], Confidence: 0.6),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("xa-adpcm", "XA-ADPCM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "CD-XA / PSX streaming ADPCM (.xa); full file + decoded per-channel WAV.";

  // ── Sector geometry ──────────────────────────────────────────────────────────
  private const int RawSectorSize = 2352;
  private const int Mode2SectorSize = 2336;
  private const int SyncSize = 12;       // 00 FF×10 00
  private const int HeaderSize = 16;     // sync(12) + address(3) + mode(1)
  private const int SubHeaderSize = 8;   // file, channel, submode, codinginfo ×2
  private const int AudioDataSize = 2304; // 18 sound groups × 128
  private const int GroupsPerSector = AudioDataSize / XaAdpcmCodec.SoundGroupSize; // 18
  private const byte SubModeAudio = 0x04;
  private const byte SubModeEof = 0x80;

  private static readonly byte[] SyncPattern =
    [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveCreatable: assemble a RIFF/CDXA file from per-channel WAVs ─────────

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // Passthrough a provided FULL.xa verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.xa", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count is 0 or > 2)
      throw new InvalidOperationException("XA archive create needs either FULL.xa or one (mono) or two (stereo) per-channel WAVs.");

    var channels = channelBlobs.Select(c => new WavReader().Read(c.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.NumChannels != 1))
      throw new InvalidOperationException("XA create expects mono per-channel WAVs.");
    if (channels.Any(c => c.BitsPerSample != 16))
      throw new InvalidOperationException("XA create expects 16-bit PCM input.");
    if (channels.Any(c => c.SampleRate != first.SampleRate))
      throw new InvalidOperationException("All channel WAVs must share the sample rate.");

    var stereo = channels.Count == 2;
    short[] interleaved;
    if (stereo) {
      var leftPcm = LePcmToShorts(channels[0].InterleavedPcm);
      var rightPcm = LePcmToShorts(channels[1].InterleavedPcm);
      var frames = Math.Max(leftPcm.Length, rightPcm.Length);
      interleaved = new short[frames * 2];
      for (var i = 0; i < frames; ++i) {
        interleaved[i * 2] = i < leftPcm.Length ? leftPcm[i] : (short)0;
        interleaved[i * 2 + 1] = i < rightPcm.Length ? rightPcm[i] : (short)0;
      }
    } else {
      interleaved = LePcmToShorts(first.InterleavedPcm);
    }

    var adpcm = XaAdpcmCodec.Encode(interleaved, stereo);
    WriteRiffCdxa(output, adpcm, first.SampleRate, stereo);
  }

  /// <summary>
  /// Writes a RIFF/CDXA file: the audio is packed into 2352-byte Mode-2 audio sectors
  /// (full sync + address + mode 2 + XA subheader marking AUDIO, the last sector also
  /// marking EOF) so that <see cref="BuildEntries"/> reads it back identically.
  /// </summary>
  private static void WriteRiffCdxa(Stream output, byte[] adpcm, int sampleRate, bool stereo) {
    var groupsPerSector = GroupsPerSector;
    var totalGroups = adpcm.Length / XaAdpcmCodec.SoundGroupSize;
    var sectorCount = Math.Max(1, (totalGroups + groupsPerSector - 1) / groupsPerSector);

    var codingInfo = BuildCodingInfo(stereo, sampleRate);
    var sectors = new byte[sectorCount * RawSectorSize];
    for (var s = 0; s < sectorCount; ++s) {
      var sectorOff = s * RawSectorSize;
      SyncPattern.CopyTo(sectors.AsSpan(sectorOff));
      WriteAddress(sectors.AsSpan(sectorOff + SyncSize), s);
      sectors[sectorOff + 15] = 0x02; // mode 2

      var sub = sectorOff + HeaderSize;
      var submode = (byte)(SubModeAudio | (s == sectorCount - 1 ? SubModeEof : 0));
      // file=0, channel=0; subheader stored twice.
      sectors[sub + 0] = 0;            // file
      sectors[sub + 1] = 0;            // channel
      sectors[sub + 2] = submode;      // submode
      sectors[sub + 3] = codingInfo;   // coding info
      sectors[sub + 4] = 0;
      sectors[sub + 5] = 0;
      sectors[sub + 6] = submode;
      sectors[sub + 7] = codingInfo;

      var dataOff = sub + SubHeaderSize;
      var groupStart = s * groupsPerSector;
      var groupsThisSector = Math.Min(groupsPerSector, totalGroups - groupStart);
      if (groupsThisSector > 0)
        adpcm.AsSpan(groupStart * XaAdpcmCodec.SoundGroupSize,
                     groupsThisSector * XaAdpcmCodec.SoundGroupSize)
          .CopyTo(sectors.AsSpan(dataOff));
    }

    // RIFF/CDXA wrapper: "RIFF" size "CDXA" + "fmt " (16) + "data" sectors.
    const int fmtSize = 16;
    var dataSize = sectors.Length;
    var riffPayload = 4 /*CDXA*/ + (8 + fmtSize) + (8 + dataSize);

    Span<byte> head = stackalloc byte[12 + 8 + fmtSize + 8];
    "RIFF"u8.CopyTo(head);
    BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)riffPayload);
    "CDXA"u8.CopyTo(head[8..]);
    "fmt "u8.CopyTo(head[12..]);
    BinaryPrimitives.WriteUInt32LittleEndian(head[16..], fmtSize);
    // CDXA fmt body (16 bytes): owner id, attributes, file#, channel#, submode, codinginfo, etc.
    head[20..(20 + fmtSize)].Clear();
    head[26] = 0; // file
    head[27] = 0; // channel
    head[28] = SubModeAudio;
    head[29] = codingInfo;
    "data"u8.CopyTo(head[36..]);
    BinaryPrimitives.WriteUInt32LittleEndian(head[40..], (uint)dataSize);

    output.Write(head);
    output.Write(sectors);
  }

  private static void WriteAddress(Span<byte> dst, int sector) {
    // MSF address in BCD (minutes:seconds:frames, 75 frames/sec, +150 lead-in).
    var abs = sector + 150;
    var minutes = abs / (75 * 60);
    var seconds = abs / 75 % 60;
    var frames = abs % 75;
    dst[0] = ToBcd(minutes);
    dst[1] = ToBcd(seconds);
    dst[2] = ToBcd(frames);
  }

  private static byte ToBcd(int value) => (byte)((value / 10 << 4) | value % 10);

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "XA archive accepts: FULL.xa, MONO.wav, or LEFT/RIGHT.wav (per-channel, 16-bit)";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.xa" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not an XA-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── Archive-entry builder ───────────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.xa", "Container", blob),
    };

    XaStream? parsed = null;
    try {
      parsed = ParseXa(blob);
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException) {
      // graceful FULL-only fallback
    }

    if (parsed is { Groups.Length: > 0 } p && !p.EightBit) {
      var samples = XaAdpcmCodec.Decode(p.Groups, p.Stereo);
      if (p.Stereo) {
        var (left, right) = DeinterleaveStereo(samples);
        entries.Add(new("LEFT.wav", "Channel",
          PcmCodec.ToWavBlob(ShortsToLePcm(left), channels: 1, p.SampleRate, bitsPerSample: 16, formatCode: 1), "xa-adpcm"));
        entries.Add(new("RIGHT.wav", "Channel",
          PcmCodec.ToWavBlob(ShortsToLePcm(right), channels: 1, p.SampleRate, bitsPerSample: 16, formatCode: 1), "xa-adpcm"));
      } else {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(ShortsToLePcm(samples), channels: 1, p.SampleRate, bitsPerSample: 16, formatCode: 1), "xa-adpcm"));
      }

      var info = new StringBuilder();
      info.AppendLine($"sample_rate={p.SampleRate}");
      info.AppendLine($"stereo={(p.Stereo ? 1 : 0)}");
      info.AppendLine($"bits_per_sample={(p.EightBit ? 8 : 4)}");
      info.AppendLine($"file={p.File}");
      info.AppendLine($"channel={p.Channel}");
      info.AppendLine($"audio_sectors={p.SectorCount}");
      info.AppendLine($"other_streams={string.Join(",", p.OtherStreams)}");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
    } else if (parsed is { EightBit: true }) {
      // 8-bit decode is implemented (Decode8Bit) but the channel-archive surfaces only
      // 4-bit audio for the per-channel WAVs; 8-bit content degrades to FULL only.
      var info = new StringBuilder();
      info.AppendLine($"sample_rate={parsed.SampleRate}");
      info.AppendLine($"stereo={(parsed.Stereo ? 1 : 0)}");
      info.AppendLine("bits_per_sample=8");
      info.AppendLine("note=8-bit XA audio is surfaced as FULL.xa only");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
    }

    return entries;
  }

  // ── Parsing ───────────────────────────────────────────────────────────────

  private sealed record XaStream(
    byte[] Groups, bool Stereo, bool EightBit, int SampleRate, int File, int Channel,
    int SectorCount, IReadOnlyList<string> OtherStreams);

  private static XaStream ParseXa(byte[] blob) {
    var (sectorBytes, sectorSize, hasSync) = LocateSectors(blob);
    if (sectorBytes.Length < sectorSize)
      throw new InvalidDataException("XA: not enough data for one sector.");

    // The subheader sits after the 16-byte header in synced 2352 sectors, or at the
    // very start of bare 2336 Mode-2 sectors.
    var subHeaderOffset = hasSync ? HeaderSize : 0;
    var dataOffset = subHeaderOffset + SubHeaderSize;

    var sectorCount = sectorBytes.Length / sectorSize;
    var groups = new List<byte>();
    int firstFile = -1, firstChannel = -1;
    var stereo = false;
    var eightBit = false;
    var sampleRate = 37800;
    var audioSectors = 0;
    var otherStreams = new SortedSet<string>(StringComparer.Ordinal);

    for (var s = 0; s < sectorCount; ++s) {
      var sectorOff = s * sectorSize;
      var sub = sectorOff + subHeaderOffset;
      var submode = sectorBytes[sub + 2];
      if ((submode & SubModeAudio) == 0)
        continue; // not an audio sector

      var file = sectorBytes[sub + 0];
      var channel = sectorBytes[sub + 1];
      var coding = sectorBytes[sub + 3];

      if (firstFile < 0) {
        firstFile = file;
        firstChannel = channel;
        stereo = (coding & 0x03) == 1;
        sampleRate = ((coding >> 2) & 0x03) == 1 ? 18900 : 37800;
        eightBit = ((coding >> 4) & 0x03) == 1;
      } else if (file != firstFile || channel != firstChannel) {
        otherStreams.Add($"{file}:{channel}");
        continue;
      }

      ++audioSectors;
      var dataOff = sectorOff + dataOffset;
      var available = Math.Min(AudioDataSize, sectorBytes.Length - dataOff);
      var usable = available / XaAdpcmCodec.SoundGroupSize * XaAdpcmCodec.SoundGroupSize;
      for (var i = 0; i < usable; ++i)
        groups.Add(sectorBytes[dataOff + i]);
    }

    if (firstFile < 0)
      throw new InvalidDataException("XA: no audio sectors found.");

    return new XaStream(groups.ToArray(), stereo, eightBit, sampleRate, firstFile, firstChannel,
      audioSectors, otherStreams.ToArray());
  }

  /// <summary>
  /// Returns the raw sector blob, its sector size and whether sectors carry the 12-byte
  /// sync. RIFF/CDXA is unwrapped to its <c>data</c> chunk; otherwise the raw stream's
  /// geometry is inferred from the leading sync pattern (2352) or assumed 2336.
  /// </summary>
  private static (byte[] Sectors, int SectorSize, bool HasSync) LocateSectors(byte[] blob) {
    // RIFF/CDXA → unwrap the data chunk of 2352-byte sectors.
    if (blob.Length >= 12 &&
        blob[0] == 'R' && blob[1] == 'I' && blob[2] == 'F' && blob[3] == 'F' &&
        blob[8] == 'C' && blob[9] == 'D' && blob[10] == 'X' && blob[11] == 'A') {
      var data = FindRiffChunk(blob, "data");
      if (data == null)
        throw new InvalidDataException("RIFF/CDXA: missing data chunk.");
      return (data, RawSectorSize, HasSync: true);
    }

    // Raw sectors: a leading 12-byte sync pattern → 2352 synced sectors.
    if (blob.Length >= SyncSize && blob.AsSpan(0, SyncSize).SequenceEqual(SyncPattern))
      return (blob, RawSectorSize, HasSync: true);

    // Otherwise assume bare 2336-byte Mode-2 sectors (subheader-first).
    return (blob, Mode2SectorSize, HasSync: false);
  }

  private static byte[]? FindRiffChunk(byte[] blob, string id) {
    var pos = 12; // skip "RIFF" size "CDXA"
    var wanted = Encoding.ASCII.GetBytes(id);
    while (pos + 8 <= blob.Length) {
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + 4));
      var bodyStart = pos + 8;
      if (bodyStart + size > blob.Length)
        size = blob.Length - bodyStart; // tolerate a truncated/oversized declared size
      if (blob[pos] == wanted[0] && blob[pos + 1] == wanted[1] &&
          blob[pos + 2] == wanted[2] && blob[pos + 3] == wanted[3])
        return blob.AsSpan(bodyStart, size).ToArray();
      pos = bodyStart + size + (size & 1); // word align
    }
    return null;
  }

  // ── Coding-info helpers ─────────────────────────────────────────────────────

  private static byte BuildCodingInfo(bool stereo, int sampleRate) {
    byte coding = 0;
    if (stereo) coding |= 0x01;                  // bits 0-1: stereo
    if (sampleRate <= 18900) coding |= 0x04;     // bits 2-3: rate (1 = 18900)
    // bits 4-5 stay 0 → 4-bit samples.
    return coding;
  }

  // ── Sample plumbing ─────────────────────────────────────────────────────────

  private static (short[] Left, short[] Right) DeinterleaveStereo(short[] interleaved) {
    var frames = interleaved.Length / 2;
    var left = new short[frames];
    var right = new short[frames];
    for (var i = 0; i < frames; ++i) {
      left[i] = interleaved[i * 2];
      right[i] = interleaved[i * 2 + 1];
    }
    return (left, right);
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static short[] LePcmToShorts(byte[] pcm) {
    var samples = new short[pcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
    return samples;
  }
}
