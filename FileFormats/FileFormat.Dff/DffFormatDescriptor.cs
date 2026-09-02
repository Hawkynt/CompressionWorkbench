#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Dsf;

namespace FileFormat.Dff;

/// <summary>
/// Exposes a Philips DSDIFF (<c>.dff</c>/<c>.dsdiff</c>) file as an archive of <c>FULL.dff</c>
/// plus, per channel, the raw 1-bit DSD bitstream (<c>&lt;NAME&gt;.dsd</c>) and a playable
/// decimated 16-bit mono PCM WAV at <c>sampleRate / 64</c> (<c>&lt;NAME&gt;.wav</c>), plus an
/// <c>metadata.ini</c> summary. DSD bits are MSB-first within each byte. DST-compressed streams
/// cannot be de-interleaved, so those files surface as <c>FULL.dff</c> + <c>metadata.ini</c> only.
/// PCM is produced by <see cref="DsdDecimator"/> (an inspection-grade approximation, not a
/// fidelity-preserving FIR decimator).
/// </summary>
public sealed class DffFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Dff";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "DSDIFF (.dff)";
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
  public string DefaultExtension => ".dff";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".dff", ".dsdiff"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("FRM8"u8.ToArray(), Confidence: 0.90),
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
  public string Description => "Philips DSDIFF (1-bit DSD); full file + per-channel DSD bitstreams + decimated PCM.";

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

  // ── IArchiveCreatable: passthrough FULL.dff, or assemble from per-channel .dsd streams ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.dff", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelStreams = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".dsd", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .Select(f => (Name: Path.GetFileNameWithoutExtension(f.Name), f.Data))
      .ToList();

    if (channelStreams.Count == 0)
      throw new InvalidOperationException("DSDIFF archive create needs either FULL.dff or one or more per-channel .dsd streams.");

    var len = channelStreams[0].Data.Length;
    if (channelStreams.Any(c => c.Data.Length != len))
      throw new InvalidOperationException("All per-channel .dsd streams must have equal length.");

    var sampleRate = options.GetOptionInt("rate", 2822400);
    var chnlIds = channelStreams.Select(c => ChannelIdFromName(c.Name, channelStreams.Count)).ToList();
    WriteDff(output, channelStreams.Select(c => c.Data).ToList(), chnlIds, sampleRate);
  }

  /// <summary>
  /// Writes a minimal uncompressed DSDIFF: <c>FRM8</c> form with <c>FVER</c>, a <c>PROP/SND&#160;</c>
  /// property block (<c>FS&#160;&#160;</c>, <c>CHNL</c>, <c>CMPR</c> = <c>DSD&#160;</c>) and a top-level
  /// <c>DSD&#160;</c> data chunk whose payload is the channels woven back together byte round-robin.
  /// All sizes are big-endian u64 and chunk bodies pad to an even boundary, so the stream round-trips
  /// bit-exact through <see cref="DffReader"/>.
  /// </summary>
  private static void WriteDff(Stream output, IReadOnlyList<byte[]> channels, IReadOnlyList<string> chnlIds, int sampleRate) {
    var numChannels = channels.Count;
    var bytesPerChannel = channels[0].Length;

    var fver = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(fver, 0x01050000);

    // PROP/SND  body: 'SND ' + FS + CHNL + CMPR.
    using var prop = new MemoryStream();
    prop.Write("SND "u8);

    var fs = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(fs, (uint)sampleRate);
    WriteLocalChunk(prop, "FS  ", fs);

    using var chnl = new MemoryStream();
    Span<byte> cnt = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(cnt, (ushort)numChannels);
    chnl.Write(cnt);
    foreach (var id in chnlIds)
      chnl.Write(Pad4(id));
    WriteLocalChunk(prop, "CHNL", chnl.ToArray());

    using var cmpr = new MemoryStream();
    cmpr.Write("DSD "u8);
    var cmprName = "not compressed"u8.ToArray();
    cmpr.WriteByte((byte)cmprName.Length); // pascal-string length
    cmpr.Write(cmprName);
    WriteLocalChunk(prop, "CMPR", cmpr.ToArray());

    var dsdPayload = WeaveByteRoundRobin(channels, bytesPerChannel);

    // Assemble FRM8 form.
    using var form = new MemoryStream();
    form.Write("DSD "u8); // form type
    WriteLocalChunk(form, "FVER", fver);
    WriteLocalChunk(form, "PROP", prop.ToArray());
    WriteLocalChunk(form, "DSD ", dsdPayload);

    var formBody = form.ToArray();
    Span<byte> head = stackalloc byte[12];
    "FRM8"u8.CopyTo(head);
    BinaryPrimitives.WriteUInt64BigEndian(head[4..], (ulong)formBody.Length);
    output.Write(head[..12]);
    output.Write(formBody);
  }

  private static byte[] WeaveByteRoundRobin(IReadOnlyList<byte[]> channels, int bytesPerChannel) {
    var numChannels = channels.Count;
    var payload = new byte[(long)bytesPerChannel * numChannels];
    for (var i = 0; i < bytesPerChannel; ++i)
      for (var c = 0; c < numChannels; ++c)
        payload[(long)i * numChannels + c] = channels[c][i];
    return payload;
  }

  private static void WriteLocalChunk(Stream s, string ckId, byte[] body) {
    Span<byte> head = stackalloc byte[12];
    Encoding.ASCII.GetBytes(ckId).CopyTo(head);
    BinaryPrimitives.WriteUInt64BigEndian(head[4..], (ulong)body.Length);
    s.Write(head[..12]);
    s.Write(body);
    if ((body.Length & 1) != 0)
      s.WriteByte(0); // pad to even
  }

  private static byte[] Pad4(string id) {
    var b = new byte[4];
    var src = Encoding.ASCII.GetBytes(id);
    Array.Copy(src, b, Math.Min(4, src.Length));
    for (var i = src.Length; i < 4; ++i) b[i] = (byte)' ';
    return b;
  }

  // ── Channel-ID ↔ canonical-name mapping ─────────────────────────────────────

  /// <summary>
  /// Maps a DSDIFF 4-char channel ID to a canonical <see cref="ChannelLayout"/> name. For
  /// exactly two channels stereo is surfaced as <c>LEFT</c>/<c>RIGHT</c>; unknown IDs degrade
  /// to indexed <c>CH_n</c> names.
  /// </summary>
  internal static string NameFromChannelId(string id, int channelIndex, int numChannels) {
    if (numChannels == 2) {
      var t = id.TrimEnd();
      if (t is "SLFT" or "MLFT") return "LEFT";
      if (t is "SRGT" or "MRGT") return "RIGHT";
    }

    return id.TrimEnd() switch {
      "SLFT" or "MLFT" => "FRONT_LEFT",
      "SRGT" or "MRGT" => "FRONT_RIGHT",
      "C" => "CENTER",
      "LFE" => "LFE",
      "LS" => "BACK_LEFT",
      "RS" => "BACK_RIGHT",
      _ => $"CH_{channelIndex}",
    };
  }

  /// <summary>Inverse mapping used when assembling a DFF from per-channel <c>.dsd</c> inputs.</summary>
  private static string ChannelIdFromName(string name, int numChannels) => name.ToUpperInvariant() switch {
    "LEFT" or "FRONT_LEFT" => "SLFT",
    "RIGHT" or "FRONT_RIGHT" => "SRGT",
    "CENTER" => "C   ",
    "LFE" => "LFE ",
    "BACK_LEFT" => "LS  ",
    "BACK_RIGHT" => "RS  ",
    _ => "C   ",
  };

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "DSDIFF archive accepts: FULL.dff, LEFT/RIGHT/CENTER/… .dsd (per-channel raw DSD)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";

    if (dir == "" && (name == "full.dff" || name.EndsWith(".dsd"))) { reason = null; return true; }
    reason = $"not a DSDIFF-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new DffReader().Read(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.dff", "Container", blob),
    };

    // 'DST '-compressed (or otherwise non-de-interleavable) → FULL + metadata only.
    if (parsed.ChannelDsd.Length == parsed.NumChannels && parsed.NumChannels > 0) {
      var pcmRate = parsed.SampleRate / DsdDecimator.DecimationFactor;
      for (var c = 0; c < parsed.NumChannels; ++c) {
        var id = c < parsed.ChannelIds.Count ? parsed.ChannelIds[c] : $"CH_{c}";
        var name = NameFromChannelId(id, c, parsed.NumChannels);
        entries.Add(new($"{name}.dsd", "Stream", parsed.ChannelDsd[c]));
        var pcm = DsdDecimator.DecimateToPcm16(parsed.ChannelDsd[c], lsbFirst: false);
        var wav = PcmCodec.ToWavBlob(pcm, channels: 1, pcmRate, bitsPerSample: 16, formatCode: 1);
        entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }
    }

    entries.Add(new("metadata.ini", "Tag", BuildMetadataIni(parsed)));

    return entries;
  }

  private static byte[] BuildMetadataIni(DffReader.ParsedDff parsed) {
    var sb = new StringBuilder();
    sb.Append("[dff]\n");
    sb.Append(CultureInfo.InvariantCulture, $"rate={parsed.SampleRate}\n");
    sb.Append(CultureInfo.InvariantCulture, $"channels={parsed.NumChannels}\n");
    sb.Append(CultureInfo.InvariantCulture, $"compression={parsed.Compression.TrimEnd()}\n");
    sb.Append(CultureInfo.InvariantCulture, $"channelIds={string.Join(',', parsed.ChannelIds.Select(i => i.TrimEnd()))}\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
  }
}
