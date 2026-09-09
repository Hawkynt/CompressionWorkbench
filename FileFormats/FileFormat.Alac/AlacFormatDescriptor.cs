#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Alac;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Mp4;
using FileFormat.Wav;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Alac;

/// <summary>
/// Apple Lossless audio surface for M4A/MP4 and CAF. The descriptor can decode to
/// canonical PCM, encode every ALAC source shape defined by Apple (16/20/24/32-bit,
/// 1..8 channels), preserve encoded packets while remuxing, and expose the traditional
/// archive view of container/cookie/track/channel payloads.
/// </summary>
public sealed class AlacFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveInMemoryExtract,
  IArchiveCreatable,
  IArchiveWriteConstraints,
  IFormatOptionsSchema,
  IAudioContainerFormat,
  IAudioPcmSource,
  IAudioPcmTarget,
  IAudioDemuxSource,
  IAudioMuxTarget {

  private const int DefaultFrameLength = 4096;
  private const int MaxFrameLength = 16_384;
  private static readonly string[] EncodeCodecs = ["alac"];
  private static readonly uint[] ChannelLayoutTags = [
    (100u << 16) | 1,
    (101u << 16) | 2,
    (113u << 16) | 3,
    (116u << 16) | 4,
    (120u << 16) | 5,
    (124u << 16) | 6,
    (142u << 16) | 7,
    (127u << 16) | 8,
  ];

  public string Id => "Alac";
  public string DisplayName => "ALAC (Apple Lossless)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".m4a";
  public IReadOnlyList<string> Extensions => [".m4a", ".alac", ".caf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored"), new("alac", "ALAC")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Apple Lossless in M4A/MP4 or CAF; full PCM encode, decode and packet-preserving remux.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Container", "Container", FormatOptionKind.Enum, "M4A", ["M4A", "CAF"],
      "M4A uses a QuickTime sound-description v2 ALAC sample entry; CAF uses desc/kuki/pakt/data chunks."),
    new("FrameLength", "ALAC frames per packet", FormatOptionKind.Integer, "4096", null,
      "Any value from 1 through 16384 is codec-valid. 4096 is Apple's compatibility default."),
  ];

  public IReadOnlyList<string> SupportedEncodeCodecs => EncodeCodecs;
  public IReadOnlyList<string> SupportedMuxCodecs => EncodeCodecs;

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "accepts: FULL.m4a/FULL.caf passthrough, ALAC magic cookie + raw track, or 1..8 matching mono PCM WAV channels";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) {
      reason = "ALAC is a single audio stream and does not accept directories";
      return false;
    }
    var name = input.ArchiveName;
    if (name.Equals("FULL.m4a", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("FULL.caf", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("alac_magic_cookie.bin", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_alac.bin", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }
    reason = "ALAC creation accepts a full container, its cookie/raw-track pair, or mono PCM WAV channels";
    return false;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((entry, index) => new ArchiveEntryInfo(
      Index: index,
      Name: entry.Name,
      OriginalSize: entry.Data.Length,
      CompressedSize: entry.Data.Length,
      Method: entry.Kind switch { "Track" => "alac", "Channel" => "pcm", _ => "stored" },
      IsDirectory: false,
      IsEncrypted: false,
      LastModified: null,
      Kind: entry.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var entry in BuildEntries(stream)) {
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files))
        continue;
      WriteFile(outputDir, entry.Name, entry.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var entry in BuildEntries(input)) {
      if (!entry.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
        continue;
      output.Write(entry.Data);
      return;
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var files = inputs.Where(static input => !input.IsDirectory).ToList();
    var full = files.FirstOrDefault(file =>
      file.ArchiveName.Equals("FULL.m4a", StringComparison.OrdinalIgnoreCase) ||
      file.ArchiveName.Equals("FULL.caf", StringComparison.OrdinalIgnoreCase));
    if (full != null) {
      output.Write(full.ReadContent());
      return;
    }

    var cookieInput = files.FirstOrDefault(file =>
      file.ArchiveName.Equals("alac_magic_cookie.bin", StringComparison.OrdinalIgnoreCase));
    var tracks = files.Where(file => file.ArchiveName.EndsWith("_alac.bin", StringComparison.OrdinalIgnoreCase)).ToList();
    if (cookieInput != null || tracks.Count > 0) {
      if (cookieInput == null || tracks.Count != 1)
        throw new InvalidOperationException("Raw ALAC remux creation requires exactly one alac_magic_cookie.bin and one *_alac.bin track.");
      var cookie = AlacCookie.Parse(cookieInput.ReadContent());
      var packets = SplitRawFrames(tracks[0].ReadContent(), cookie);
      var encoded = new AudioEncodedStream(
        new AudioStreamFormat("alac", checked((int)cookie.SampleRate), cookie.NumChannels, cookie.BitDepth,
          new Dictionary<string, string> { ["frame-length"] = cookie.FrameLength.ToString(CultureInfo.InvariantCulture) }),
        packets,
        cookie.Write());
      Mux(output, encoded, options);
      return;
    }

    var wavInputs = files.Where(file => file.ArchiveName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)).ToList();
    if (wavInputs is { Count: >= 1 and <= 8 } && wavInputs.Count == files.Count) {
      var wavs = wavInputs.Select(file => new WavReader().ReadCanonicalPcm(file.ReadContent())).ToList();
      var first = wavs[0];
      if (wavs.Any(wav => wav.NumChannels != 1 || wav.SampleRate != first.SampleRate ||
                          wav.BitsPerSample != first.BitsPerSample || wav.FormatCode != first.FormatCode))
        throw new InvalidOperationException("All ALAC source WAVs must be mono and share sample rate, bit depth and PCM representation.");
      if (first.FormatCode != 1)
        throw new NotSupportedException("ALAC encoding accepts signed integer PCM WAV input, not IEEE float or compressed WAV data.");
      if (first.BitsPerSample is not (16 or 20 or 24 or 32))
        throw new NotSupportedException("ALAC supports 16, 20, 24 or 32-bit PCM input.");
      var pcm = InterleaveChannels(wavs.Select(static wav => wav.InterleavedPcm).ToList(), first.BitsPerSample);
      var buffer = new AudioPcmBuffer(
        new AudioPcmFormat(first.SampleRate, wavs.Count, first.BitsPerSample, AudioPcmEncoding.SignedInteger), pcm);
      EncodePcm(output, buffer, "alac", options);
      return;
    }

    throw new InvalidOperationException("ALAC creation needs FULL.m4a/FULL.caf, a cookie/raw-track pair, or 1..8 mono PCM WAV channels.");
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    if (!TryDemux(input, out var encoded) || encoded == null)
      throw new InvalidDataException("Input does not contain a supported ALAC stream.");
    if (encoded.CodecPrivateData == null)
      throw new InvalidDataException("ALAC stream has no magic cookie.");

    var cookie = AlacCookie.Parse(encoded.CodecPrivateData);
    var compressed = ConcatPackets(encoded.Packets);
    var pcm = AlacCodec.Decode(compressed, cookie);
    return new AudioPcmBuffer(
      new AudioPcmFormat(checked((int)cookie.SampleRate), cookie.NumChannels, cookie.BitDepth, AudioPcmEncoding.SignedInteger),
      pcm);
  }

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);
    ArgumentNullException.ThrowIfNull(options);
    if (!codecId.Equals("alac", StringComparison.OrdinalIgnoreCase)) {
      reason = $"ALAC descriptor cannot encode codec '{codecId}'";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger) {
      reason = "ALAC requires signed integer PCM";
      return false;
    }
    if (format.Channels is < 1 or > 8) {
      reason = "ALAC supports one through eight channels";
      return false;
    }
    if (format.BitsPerSample is not (16 or 20 or 24 or 32)) {
      reason = "ALAC supports 16, 20, 24 or 32-bit PCM";
      return false;
    }
    if (format.SampleRate <= 0) {
      reason = "ALAC sample rate must be positive";
      return false;
    }
    if (!TryGetContainer(options, out _, out reason))
      return false;
    var frameLength = options.GetOptionInt("FrameLength", DefaultFrameLength);
    if (frameLength is < 1 or > MaxFrameLength) {
      reason = "ALAC FrameLength must be between 1 and 16384 sample frames";
      return false;
    }
    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(pcm);
    ArgumentNullException.ThrowIfNull(options);
    if (!CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);
    if (pcm.InterleavedData.LongLength % pcm.Format.BytesPerFrame != 0)
      throw new ArgumentException("PCM byte count is not a whole number of sample frames.", nameof(pcm));

    var frameLength = options.GetOptionInt("FrameLength", DefaultFrameLength);
    var (frames, cookie) = AlacEncoder.Encode(
      pcm.InterleavedData,
      pcm.Format.Channels,
      pcm.Format.SampleRate,
      pcm.Format.BitsPerSample,
      frameLength);
    var packets = SplitEncodedFrames(frames, cookie, pcm.FrameCount);
    var stream = new AudioEncodedStream(
      new AudioStreamFormat("alac", pcm.Format.SampleRate, pcm.Format.Channels, pcm.Format.BitsPerSample,
        new Dictionary<string, string> { ["frame-length"] = frameLength.ToString(CultureInfo.InvariantCulture) }),
      packets,
      cookie.Write());
    Mux(output, stream, options);
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();
    return TryDemux(data, out stream, out _);
  }

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!stream.CodecId.Equals("alac", StringComparison.OrdinalIgnoreCase)) {
      reason = $"ALAC descriptor cannot mux codec '{stream.CodecId}'";
      return false;
    }
    if (stream.SampleRate <= 0 || stream.Channels is < 1 or > 8 || stream.BitsPerSample is not (16 or 20 or 24 or 32)) {
      reason = "ALAC muxing requires a positive sample rate, 1..8 channels and 16/20/24/32-bit source depth";
      return false;
    }
    if (!TryGetContainer(options, out _, out reason))
      return false;
    reason = null;
    return true;
  }

  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);
    if (stream.CodecPrivateData == null)
      throw new ArgumentException("ALAC muxing requires the codec magic cookie.", nameof(stream));
    if (stream.Packets.Count == 0)
      throw new ArgumentException("ALAC muxing requires at least one packet.", nameof(stream));

    _ = AlacCookie.Parse(stream.CodecPrivateData);
    TryGetContainer(options, out var container, out _);
    if (container == AlacContainer.M4a) {
      var mp4 = new Mp4FormatDescriptor();
      if (!mp4.CanMux(stream.Format, options, out reason))
        throw new NotSupportedException(reason);
      mp4.Mux(output, stream, options);
      return;
    }

    WriteCaf(output, stream);
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var file = ms.ToArray();
    var isCaf = file.AsSpan().StartsWith("caff"u8);
    var entries = new List<(string, string, byte[])> {
      (isCaf ? "FULL.caf" : "FULL.m4a", "Container", file),
    };

    if (!TryDemux(file, out var encoded, out _) || encoded?.CodecPrivateData == null)
      return entries;

    var cookie = AlacCookie.Parse(encoded.CodecPrivateData);
    entries.Add(("alac_magic_cookie.bin", "CodecConfig", cookie.Write()));
    entries.Add(("metadata.ini", "Metadata", Encoding.UTF8.GetBytes(BuildMetadata(cookie))));
    var compressed = ConcatPackets(encoded.Packets);
    entries.Add(("track_00_alac.bin", "Track", compressed));

    try {
      var pcm = AlacCodec.Decode(compressed, cookie);
      if (pcm.Length > 0)
        foreach (var (name, wav) in SplitPcmToWavs(pcm, cookie.NumChannels, checked((int)cookie.SampleRate), cookie.BitDepth))
          entries.Add(($"{name}.wav", "Channel", wav));
    } catch (Exception) {
      // Archive listing remains useful even for malformed or unsupported packet payloads.
    }
    return entries;
  }

  private static bool TryDemux(byte[] file, out AudioEncodedStream? stream, out AlacContainer container) {
    if (file.AsSpan().StartsWith("caff"u8)) {
      container = AlacContainer.Caf;
      return TryDemuxCaf(file, out stream);
    }
    container = AlacContainer.M4a;
    return TryDemuxM4a(file, out stream);
  }

  private static bool TryDemuxM4a(byte[] file, out AudioEncodedStream? stream) {
    stream = null;
    try {
      var boxes = new BoxParser().Parse(file);
      var moov = BoxParser.Find(boxes, "moov");
      if (moov?.Children == null)
        return false;

      foreach (var trak in moov.Children.Where(box => box.Type == "trak")) {
        var mdia = trak.Children?.FirstOrDefault(box => box.Type == "mdia");
        var minf = mdia?.Children?.FirstOrDefault(box => box.Type == "minf");
        var stbl = minf?.Children?.FirstOrDefault(box => box.Type == "stbl");
        if (stbl?.Children == null)
          continue;
        var stsd = stbl.Children.FirstOrDefault(box => box.Type == "stsd");
        if (stsd == null || !TryFindAlacSampleEntry(file, stsd, out var entryOffset, out var entrySize))
          continue;
        var rawCookie = ExtractAlacCookie(file, entryOffset, entrySize);
        if (rawCookie.Length == 0)
          continue;
        var cookie = AlacCookie.Parse(rawCookie);

        var stsz = stbl.Children.FirstOrDefault(box => box.Type == "stsz");
        var stsc = stbl.Children.FirstOrDefault(box => box.Type == "stsc");
        var stco = stbl.Children.FirstOrDefault(box => box.Type == "stco");
        var co64 = stbl.Children.FirstOrDefault(box => box.Type == "co64");
        var stts = stbl.Children.FirstOrDefault(box => box.Type == "stts");
        if (stsz == null || stsc == null || (stco == null && co64 == null))
          continue;

        var packetData = ExtractPackets(file, stsz, stsc, stco, co64);
        var durations = stts == null ? [] : ReadSampleDurations(file, stts);
        var packets = new List<AudioPacket>(packetData.Count);
        for (var i = 0; i < packetData.Count; ++i)
          packets.Add(new AudioPacket(packetData[i], i < durations.Count ? durations[i] : cookie.FrameLength));

        stream = new AudioEncodedStream(
          new AudioStreamFormat("alac", checked((int)cookie.SampleRate), cookie.NumChannels, cookie.BitDepth,
            new Dictionary<string, string> { ["frame-length"] = cookie.FrameLength.ToString(CultureInfo.InvariantCulture) }),
          packets,
          cookie.Write());
        return true;
      }
      return false;
    } catch (Exception) {
      return false;
    }
  }

  private static bool TryDemuxCaf(byte[] file, out AudioEncodedStream? stream) {
    stream = null;
    try {
      if (file.Length < 8 || !file.AsSpan(0, 4).SequenceEqual("caff"u8) || BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4)) != 1)
        return false;

      CafDescription? description = null;
      byte[]? cookieBytes = null;
      byte[]? audioData = null;
      CafPacketTable? packetTable = null;
      var pos = 8;
      while (pos + 12 <= file.Length) {
        var type = Encoding.ASCII.GetString(file, pos, 4);
        var size = BinaryPrimitives.ReadInt64BigEndian(file.AsSpan(pos + 4, 8));
        var body = pos + 12;
        var bodyLength = size == -1 ? file.Length - body : checked((int)size);
        if (size < -1 || bodyLength < 0 || body + (long)bodyLength > file.Length)
          return false;
        var span = file.AsSpan(body, bodyLength);
        switch (type) {
          case "desc": description = ParseCafDescription(span); break;
          case "kuki": cookieBytes = span.ToArray(); break;
          case "pakt": packetTable = ParseCafPacketTable(span); break;
          case "data":
            if (span.Length < 4) return false;
            audioData = span[4..].ToArray();
            break;
        }
        pos = body + bodyLength;
        if (size == -1)
          break;
      }

      if (description == null || !description.FormatId.Equals("alac", StringComparison.Ordinal) ||
          cookieBytes == null || audioData == null)
        return false;
      var desc = description;
      var cookie = AlacCookie.Parse(cookieBytes);
      if (desc.SampleRate <= 0 || desc.Channels is < 1 or > 8)
        return false;

      var sizes = packetTable?.PacketSizes;
      if ((sizes == null || sizes.Count == 0) && desc.BytesPerPacket > 0) {
        sizes = [];
        for (var offset = 0; offset + desc.BytesPerPacket <= audioData.Length; offset += desc.BytesPerPacket)
          sizes.Add(desc.BytesPerPacket);
      }
      if (sizes == null || sizes.Count == 0)
        return false;

      var packets = new List<AudioPacket>(sizes.Count);
      var dataOffset = 0;
      var validFrames = packetTable?.ValidFrames ?? (long)sizes.Count * cookie.FrameLength;
      for (var i = 0; i < sizes.Count; ++i) {
        var size = sizes[i];
        if (size < 0 || dataOffset + (long)size > audioData.Length)
          return false;
        var duration = (long)cookie.FrameLength;
        if (i == sizes.Count - 1) {
          var tail = validFrames - (long)cookie.FrameLength * (sizes.Count - 1);
          if (tail > 0 && tail <= cookie.FrameLength)
            duration = tail;
        }
        packets.Add(new AudioPacket(audioData.AsSpan(dataOffset, size).ToArray(), duration));
        dataOffset += size;
      }
      if (dataOffset != audioData.Length)
        return false;

      stream = new AudioEncodedStream(
        new AudioStreamFormat("alac", checked((int)cookie.SampleRate), cookie.NumChannels, cookie.BitDepth,
          new Dictionary<string, string> { ["frame-length"] = cookie.FrameLength.ToString(CultureInfo.InvariantCulture) }),
        packets,
        cookie.Write());
      return true;
    } catch (Exception) {
      return false;
    }
  }

  private static void WriteCaf(Stream output, AudioEncodedStream stream) {
    var cookie = AlacCookie.Parse(stream.CodecPrivateData!);
    if (cookie.NumChannels != stream.Format.Channels || cookie.SampleRate != (uint)stream.Format.SampleRate || cookie.BitDepth != stream.Format.BitsPerSample)
      throw new InvalidDataException("ALAC stream format disagrees with its magic cookie.");

    output.Write("caff"u8);
    WriteUInt16(output, 1);
    WriteUInt16(output, 0);

    using (var desc = new MemoryStream()) {
      WriteDouble(desc, stream.Format.SampleRate);
      desc.Write("alac"u8);
      WriteUInt32(desc, SourceDepthFlag(stream.Format.BitsPerSample));
      WriteUInt32(desc, 0);
      WriteUInt32(desc, cookie.FrameLength);
      WriteUInt32(desc, checked((uint)stream.Format.Channels));
      WriteUInt32(desc, 0);
      WriteCafChunk(output, "desc", desc.ToArray());
    }

    WriteCafChunk(output, "kuki", BuildCafCookie(cookie));
    WriteCafChunk(output, "chan", BuildCafChannelLayout(stream.Format.Channels));

    var validFrames = stream.Packets.Sum(packet => packet.DurationSamples > 0 ? packet.DurationSamples : cookie.FrameLength);
    var remainder = checked((int)Math.Max(0, (long)cookie.FrameLength * stream.Packets.Count - validFrames));
    using (var pakt = new MemoryStream()) {
      WriteInt64(pakt, stream.Packets.Count);
      WriteInt64(pakt, validFrames);
      WriteInt32(pakt, 0);
      WriteInt32(pakt, remainder);
      foreach (var packet in stream.Packets)
        WriteVariableLengthInteger(pakt, checked((uint)packet.Data.Length));
      WriteCafChunk(output, "pakt", pakt.ToArray());
    }

    using var data = new MemoryStream();
    WriteUInt32(data, 0);
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        throw new InvalidDataException("CAF ALAC media cannot contain out-of-band header packets.");
      data.Write(packet.Data);
    }
    WriteCafChunk(output, "data", data.ToArray());
  }

  private static byte[] BuildCafCookie(AlacCookie cookie) {
    using var body = new MemoryStream();
    body.Write(cookie.Write());
    WriteUInt32(body, 24);
    body.Write("chan"u8);
    WriteUInt32(body, 0);
    WriteUInt32(body, ChannelLayoutTags[cookie.NumChannels - 1]);
    WriteUInt32(body, 0);
    WriteUInt32(body, 0);
    return body.ToArray();
  }

  private static byte[] BuildCafChannelLayout(int channels) {
    using var body = new MemoryStream();
    WriteUInt32(body, ChannelLayoutTags[channels - 1]);
    WriteUInt32(body, 0);
    WriteUInt32(body, 0);
    return body.ToArray();
  }

  private static CafDescription ParseCafDescription(ReadOnlySpan<byte> body) {
    if (body.Length != 32)
      throw new InvalidDataException("CAF desc chunk must be exactly 32 bytes.");
    var rate = BinaryPrimitives.ReadDoubleBigEndian(body);
    var format = Encoding.ASCII.GetString(body.Slice(8, 4));
    var flags = BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
    var bytesPerPacket = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[16..]));
    var framesPerPacket = BinaryPrimitives.ReadUInt32BigEndian(body[20..]);
    var channels = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[24..]));
    var bits = BinaryPrimitives.ReadUInt32BigEndian(body[28..]);
    return new CafDescription(rate, format, flags, bytesPerPacket, framesPerPacket, channels, bits);
  }

  private static CafPacketTable ParseCafPacketTable(ReadOnlySpan<byte> body) {
    if (body.Length < 24)
      throw new InvalidDataException("CAF pakt chunk is shorter than its 24-byte header.");
    var count = BinaryPrimitives.ReadInt64BigEndian(body);
    var valid = BinaryPrimitives.ReadInt64BigEndian(body[8..]);
    var priming = BinaryPrimitives.ReadInt32BigEndian(body[16..]);
    var remainder = BinaryPrimitives.ReadInt32BigEndian(body[20..]);
    if (count < 0 || count > int.MaxValue || valid < 0 || priming < 0 || remainder < 0)
      throw new InvalidDataException("CAF pakt header contains invalid negative or oversized values.");

    var sizes = new List<int>(checked((int)count));
    var pos = 24;
    for (var i = 0; i < count; ++i)
      sizes.Add(checked((int)ReadVariableLengthInteger(body, ref pos)));
    return new CafPacketTable(valid, sizes);
  }

  private static uint ReadVariableLengthInteger(ReadOnlySpan<byte> body, ref int pos) {
    uint value = 0;
    for (var i = 0; i < 5; ++i) {
      if (pos >= body.Length)
        throw new InvalidDataException("CAF packet table ends inside a variable-length integer.");
      var b = body[pos++];
      if (value > uint.MaxValue >> 7)
        throw new InvalidDataException("CAF packet size variable-length integer overflows UInt32.");
      value = (value << 7) | (uint)(b & 0x7F);
      if ((b & 0x80) == 0)
        return value;
    }
    throw new InvalidDataException("CAF packet size variable-length integer is longer than five bytes.");
  }

  private static void WriteVariableLengthInteger(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[5];
    var count = 0;
    do {
      bytes[count++] = (byte)(value & 0x7F);
      value >>= 7;
    } while (value != 0);
    for (var i = count - 1; i >= 0; --i)
      output.WriteByte((byte)(bytes[i] | (i == 0 ? 0 : 0x80)));
  }

  private static uint SourceDepthFlag(int bitsPerSample) => bitsPerSample switch {
    16 => 1,
    20 => 2,
    24 => 3,
    32 => 4,
    _ => throw new ArgumentOutOfRangeException(nameof(bitsPerSample)),
  };

  private static bool TryFindAlacSampleEntry(byte[] file, BoxParser.Box stsd, out int entryOffset, out int entrySize) {
    entryOffset = 0;
    entrySize = 0;
    var pos = checked((int)stsd.BodyOffset + 8);
    var end = checked((int)(stsd.BodyOffset + stsd.BodyLength));
    while (pos + 8 <= end) {
      var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos)));
      if (size < 8 || pos + (long)size > end)
        break;
      if (file.AsSpan(pos + 4, 4).SequenceEqual("alac"u8)) {
        entryOffset = pos;
        entrySize = size;
        return true;
      }
      pos += size;
    }
    return false;
  }

  private static byte[] ExtractAlacCookie(byte[] file, int sampleEntryOffset, int sampleEntrySize) {
    var entryEnd = sampleEntryOffset + sampleEntrySize;
    if (sampleEntryOffset + 18 > entryEnd)
      return [];
    var version = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(sampleEntryOffset + 16, 2));
    var pos = sampleEntryOffset + (version switch { 2 => 72, 1 => 52, _ => 36 });
    while (pos + 8 <= entryEnd) {
      var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos)));
      if (size < 8 || pos + (long)size > entryEnd)
        return [];
      if (file.AsSpan(pos + 4, 4).SequenceEqual("alac"u8))
        return file.AsSpan(pos + 8, size - 8).ToArray();
      pos += size;
    }
    return [];
  }

  private static List<byte[]> ExtractPackets(
      byte[] file, BoxParser.Box stsz, BoxParser.Box stsc, BoxParser.Box? stco, BoxParser.Box? co64) {
    var sampleSizes = ReadSampleSizes(file, stsz);
    var chunkOffsets = stco != null ? ReadChunkOffsets32(file, stco) : ReadChunkOffsets64(file, co64!);
    var samplesPerChunk = ReadSampleToChunk(file, stsc, chunkOffsets.Count);
    var packets = new List<byte[]>(sampleSizes.Count);
    var sampleIndex = 0;
    for (var chunk = 0; chunk < chunkOffsets.Count; ++chunk) {
      var count = samplesPerChunk[chunk];
      var offset = chunkOffsets[chunk];
      for (var sample = 0; sample < count && sampleIndex < sampleSizes.Count; ++sample, ++sampleIndex) {
        var size = sampleSizes[sampleIndex];
        if (size < 0 || offset < 0 || offset + size > file.Length)
          throw new InvalidDataException("MP4 ALAC sample table points outside the file.");
        packets.Add(file.AsSpan(checked((int)offset), size).ToArray());
        offset += size;
      }
    }
    if (packets.Count != sampleSizes.Count)
      throw new InvalidDataException("MP4 ALAC chunk map does not account for every sample.");
    return packets;
  }

  private static List<int> ReadSampleSizes(byte[] file, BoxParser.Box stsz) {
    var body = file.AsSpan(checked((int)stsz.BodyOffset), checked((int)stsz.BodyLength));
    if (body.Length < 12)
      throw new InvalidDataException("MP4 stsz box is truncated.");
    var fixedSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
    var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[8..]));
    var sizes = new List<int>(count);
    if (fixedSize != 0) {
      for (var i = 0; i < count; ++i) sizes.Add(fixedSize);
      return sizes;
    }
    if (12L + count * 4L > body.Length)
      throw new InvalidDataException("MP4 stsz sample-size table is truncated.");
    for (var i = 0; i < count; ++i)
      sizes.Add(checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[(12 + 4 * i)..])));
    return sizes;
  }

  private static List<long> ReadChunkOffsets32(byte[] file, BoxParser.Box stco) {
    var body = file.AsSpan(checked((int)stco.BodyOffset), checked((int)stco.BodyLength));
    if (body.Length < 8) throw new InvalidDataException("MP4 stco box is truncated.");
    var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
    if (8L + count * 4L > body.Length) throw new InvalidDataException("MP4 stco table is truncated.");
    var result = new List<long>(count);
    for (var i = 0; i < count; ++i)
      result.Add(BinaryPrimitives.ReadUInt32BigEndian(body[(8 + 4 * i)..]));
    return result;
  }

  private static List<long> ReadChunkOffsets64(byte[] file, BoxParser.Box co64) {
    var body = file.AsSpan(checked((int)co64.BodyOffset), checked((int)co64.BodyLength));
    if (body.Length < 8) throw new InvalidDataException("MP4 co64 box is truncated.");
    var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
    if (8L + count * 8L > body.Length) throw new InvalidDataException("MP4 co64 table is truncated.");
    var result = new List<long>(count);
    for (var i = 0; i < count; ++i) {
      var offset = BinaryPrimitives.ReadUInt64BigEndian(body[(8 + 8 * i)..]);
      if (offset > long.MaxValue) throw new InvalidDataException("MP4 co64 offset exceeds Int64.");
      result.Add((long)offset);
    }
    return result;
  }

  private static List<int> ReadSampleToChunk(byte[] file, BoxParser.Box stsc, int chunkCount) {
    var body = file.AsSpan(checked((int)stsc.BodyOffset), checked((int)stsc.BodyLength));
    if (body.Length < 8) throw new InvalidDataException("MP4 stsc box is truncated.");
    var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
    if (8L + count * 12L > body.Length) throw new InvalidDataException("MP4 stsc table is truncated.");
    var records = new List<(int FirstChunk, int SamplesPerChunk)>(count);
    for (var i = 0; i < count; ++i) {
      var first = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[(8 + 12 * i)..]));
      var samples = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[(12 + 12 * i)..]));
      records.Add((first, samples));
    }
    var result = new List<int>(chunkCount);
    for (var chunk = 1; chunk <= chunkCount; ++chunk) {
      var samples = 0;
      for (var i = 0; i < records.Count; ++i)
        if (records[i].FirstChunk <= chunk && (i + 1 == records.Count || records[i + 1].FirstChunk > chunk))
          samples = records[i].SamplesPerChunk;
      result.Add(samples);
    }
    return result;
  }

  private static List<long> ReadSampleDurations(byte[] file, BoxParser.Box stts) {
    var body = file.AsSpan(checked((int)stts.BodyOffset), checked((int)stts.BodyLength));
    if (body.Length < 8) throw new InvalidDataException("MP4 stts box is truncated.");
    var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
    if (8L + count * 8L > body.Length) throw new InvalidDataException("MP4 stts table is truncated.");
    var result = new List<long>();
    for (var i = 0; i < count; ++i) {
      var repetitions = BinaryPrimitives.ReadUInt32BigEndian(body[(8 + i * 8)..]);
      var duration = BinaryPrimitives.ReadUInt32BigEndian(body[(12 + i * 8)..]);
      if (repetitions > int.MaxValue || result.Count + (long)repetitions > int.MaxValue)
        throw new InvalidDataException("MP4 stts expands beyond supported in-memory sample count.");
      for (var j = 0; j < repetitions; ++j)
        result.Add(duration);
    }
    return result;
  }

  private static List<AudioPacket> SplitEncodedFrames(byte[] frames, AlacCookie cookie, long totalFrames) {
    var packets = new List<AudioPacket>();
    var offset = 0;
    long consumedFrames = 0;
    while (offset < frames.Length) {
      var length = AlacCodec.FrameByteLength(frames.AsSpan(offset), cookie);
      if (length <= 0 || offset + (long)length > frames.Length)
        throw new InvalidDataException("ALAC encoder produced an invalid frame boundary.");
      var duration = Math.Min((long)cookie.FrameLength, totalFrames - consumedFrames);
      if (duration <= 0)
        throw new InvalidDataException("ALAC encoded more packets than the source PCM contains.");
      packets.Add(new AudioPacket(frames.AsSpan(offset, length).ToArray(), duration));
      consumedFrames += duration;
      offset += length;
    }
    if (consumedFrames != totalFrames)
      throw new InvalidDataException("ALAC packet durations do not cover the source PCM exactly.");
    return packets;
  }

  private static List<AudioPacket> SplitRawFrames(byte[] frames, AlacCookie cookie) {
    var packets = new List<AudioPacket>();
    var offset = 0;
    while (offset < frames.Length) {
      var length = AlacCodec.FrameByteLength(frames.AsSpan(offset), cookie);
      if (length <= 0 || offset + (long)length > frames.Length)
        throw new InvalidDataException("Raw ALAC track contains an invalid frame boundary.");
      var packet = frames.AsSpan(offset, length).ToArray();
      var decoded = AlacCodec.Decode(packet, cookie);
      var bytesPerFrame = checked(((cookie.BitDepth + 7) / 8) * cookie.NumChannels);
      var duration = bytesPerFrame == 0 ? 0 : decoded.Length / bytesPerFrame;
      packets.Add(new AudioPacket(packet, duration));
      offset += length;
    }
    return packets;
  }

  private static byte[] ConcatPackets(IReadOnlyList<AudioPacket> packets) {
    var total = checked((int)packets.Sum(static packet => (long)packet.Data.Length));
    var result = new byte[total];
    var offset = 0;
    foreach (var packet in packets) {
      packet.Data.CopyTo(result, offset);
      offset += packet.Data.Length;
    }
    return result;
  }

  private static IReadOnlyList<(string Name, byte[] Wav)> SplitPcmToWavs(
      byte[] pcm, int channels, int sampleRate, int bitsPerSample) {
    var bytesPerSample = (bitsPerSample + 7) / 8;
    var frameBytes = checked(bytesPerSample * channels);
    if (pcm.Length % frameBytes != 0)
      throw new InvalidDataException("Decoded ALAC PCM is not aligned to whole sample frames.");
    var frameCount = pcm.Length / frameBytes;
    var names = PcmCodec.LayoutNames(channels);
    var result = new List<(string, byte[])>(channels);
    for (var channel = 0; channel < channels; ++channel) {
      var mono = new byte[checked(frameCount * bytesPerSample)];
      for (var frame = 0; frame < frameCount; ++frame)
        Buffer.BlockCopy(pcm, frame * frameBytes + channel * bytesPerSample, mono, frame * bytesPerSample, bytesPerSample);
      result.Add((names[channel], BuildPcmWav(mono, 1, sampleRate, bitsPerSample)));
    }
    return result;
  }

  private static byte[] InterleaveChannels(IReadOnlyList<byte[]> channels, int bitsPerSample) {
    var bytesPerSample = (bitsPerSample + 7) / 8;
    var length = channels[0].Length;
    if (channels.Any(channel => channel.Length != length) || length % bytesPerSample != 0)
      throw new InvalidOperationException("ALAC source channels must have equal, sample-aligned PCM lengths.");
    var frames = length / bytesPerSample;
    var result = new byte[checked(frames * channels.Count * bytesPerSample)];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels.Count; ++channel)
        Buffer.BlockCopy(channels[channel], frame * bytesPerSample, result,
          (frame * channels.Count + channel) * bytesPerSample, bytesPerSample);
    return result;
  }

  private static byte[] BuildPcmWav(byte[] pcm, int channels, int sampleRate, int bitsPerSample) {
    var bytesPerSample = (bitsPerSample + 7) / 8;
    var blockAlign = checked((ushort)(channels * bytesPerSample));
    var byteRate = checked((uint)(sampleRate * (long)blockAlign));
    var result = new byte[checked(44 + pcm.Length)];
    var span = result.AsSpan();
    "RIFF"u8.CopyTo(span);
    BinaryPrimitives.WriteUInt32LittleEndian(span[4..], checked((uint)(36 + pcm.Length)));
    "WAVEfmt "u8.CopyTo(span[8..]);
    BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
    BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);
    BinaryPrimitives.WriteUInt16LittleEndian(span[22..], checked((ushort)channels));
    BinaryPrimitives.WriteUInt32LittleEndian(span[24..], checked((uint)sampleRate));
    BinaryPrimitives.WriteUInt32LittleEndian(span[28..], byteRate);
    BinaryPrimitives.WriteUInt16LittleEndian(span[32..], blockAlign);
    BinaryPrimitives.WriteUInt16LittleEndian(span[34..], checked((ushort)bitsPerSample));
    "data"u8.CopyTo(span[36..]);
    BinaryPrimitives.WriteUInt32LittleEndian(span[40..], checked((uint)pcm.Length));
    pcm.CopyTo(span[44..]);
    return result;
  }

  private static string BuildMetadata(AlacCookie cookie) {
    var builder = new StringBuilder();
    builder.AppendLine("[alac]");
    builder.Append("frame_length=").AppendLine(cookie.FrameLength.ToString(CultureInfo.InvariantCulture));
    builder.Append("bit_depth=").AppendLine(cookie.BitDepth.ToString(CultureInfo.InvariantCulture));
    builder.Append("channels=").AppendLine(cookie.NumChannels.ToString(CultureInfo.InvariantCulture));
    builder.Append("sample_rate=").AppendLine(cookie.SampleRate.ToString(CultureInfo.InvariantCulture));
    builder.Append("max_frame_bytes=").AppendLine(cookie.MaxFrameBytes.ToString(CultureInfo.InvariantCulture));
    builder.Append("average_bitrate=").AppendLine(cookie.AvgBitRate.ToString(CultureInfo.InvariantCulture));
    return builder.ToString();
  }

  private static bool TryGetContainer(FormatCreateOptions options, out AlacContainer container, out string? reason) {
    var value = options.GetOption("Container", "M4A");
    if (value.Equals("M4A", StringComparison.OrdinalIgnoreCase) || value.Equals("MP4", StringComparison.OrdinalIgnoreCase)) {
      container = AlacContainer.M4a;
      reason = null;
      return true;
    }
    if (value.Equals("CAF", StringComparison.OrdinalIgnoreCase)) {
      container = AlacContainer.Caf;
      reason = null;
      return true;
    }
    container = default;
    reason = $"unknown ALAC container '{value}'; expected M4A or CAF";
    return false;
  }

  private static void WriteCafChunk(Stream output, string type, byte[] body) {
    output.Write(Encoding.ASCII.GetBytes(type));
    WriteInt64(output, body.LongLength);
    output.Write(body);
  }

  private static void WriteUInt16(Stream output, ushort value) {
    Span<byte> buffer = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
    output.Write(buffer);
  }

  private static void WriteUInt32(Stream output, uint value) {
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
    output.Write(buffer);
  }

  private static void WriteInt32(Stream output, int value) {
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(buffer, value);
    output.Write(buffer);
  }

  private static void WriteInt64(Stream output, long value) {
    Span<byte> buffer = stackalloc byte[8];
    BinaryPrimitives.WriteInt64BigEndian(buffer, value);
    output.Write(buffer);
  }

  private static void WriteDouble(Stream output, double value) {
    Span<byte> buffer = stackalloc byte[8];
    BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
    output.Write(buffer);
  }

  private enum AlacContainer { M4a, Caf }
  private sealed record CafDescription(
    double SampleRate, string FormatId, uint FormatFlags, int BytesPerPacket,
    uint FramesPerPacket, int Channels, uint BitsPerChannel);
  private sealed record CafPacketTable(long ValidFrames, List<int> PacketSizes);
}
