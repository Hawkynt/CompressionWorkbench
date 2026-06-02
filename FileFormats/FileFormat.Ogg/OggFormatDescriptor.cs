#pragma warning disable CS1591
using System.Text;
using Codec.Opus;
using Codec.Pcm;
using Codec.Vorbis;
using Compression.Registry;

namespace FileFormat.Ogg;

/// <summary>
/// Surfaces an OGG container as an archive of the full file + per-logical-stream
/// raw packet blobs + the Vorbis/Opus comment block. When the primary audio stream
/// decodes (Vorbis via <c>Codec.Vorbis</c>, Opus via <c>Codec.Opus</c>) the archive
/// also surfaces one mono <c>&lt;CHANNEL&gt;.wav</c> per channel; streams the decoder
/// can't handle fall back to the raw packet blobs only.
/// </summary>
public sealed class OggFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IFileInternalLayoutMap {
  public string Id => "Ogg";
  public string DisplayName => "OGG (Xiph container)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ogg";
  public IReadOnlyList<string> Extensions => [".ogg", ".oga", ".opus"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("OggS"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Ogg bitstream; per-stream packets + Vorbis/Opus comments.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: e.Kind == "Channel" ? "pcm" : "stored",
      IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateChunks(Stream file) => OggLayoutMap.Enumerate(file);

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.ogg", "Track", blob),
    };

    AddDecodedChannels(blob, entries);

    var parser = new OggPageParser();
    var pages = parser.Pages(blob);
    var serials = pages.Select(p => p.Serial).Distinct().ToArray();

    foreach (var serial in serials) {
      var packets = parser.StreamPackets(blob, serial).ToArray();
      entries.Add(($"stream_{serial:X8}/packets.bin",
        "Track", ConcatenateWithLengthPrefix(packets)));

      // Vorbis: packet 1 is comment packet starting with 0x03 "vorbis".
      // Opus: packet 1 is "OpusTags".
      if (packets.Length >= 2) {
        var p1 = packets[1];
        (string Tag, int Offset)? probe =
          p1.Length >= 7 && p1[0] == 0x03 && Encoding.ASCII.GetString(p1, 1, 6) == "vorbis" ? ("vorbis", 7) :
          p1.Length >= 8 && Encoding.ASCII.GetString(p1, 0, 8) == "OpusTags" ? ("opus", 8) : null;
        if (probe != null) {
          var parsed = new VorbisCommentReader().Read(p1.AsSpan(probe.Value.Offset));
          var commentText = new StringBuilder();
          commentText.AppendLine($"Vendor: {parsed.Vendor}");
          foreach (var (k, v) in parsed.Comments) commentText.AppendLine($"{k}={v}");
          entries.Add(($"stream_{serial:X8}/comments.txt",
            "Tag", Encoding.UTF8.GetBytes(commentText.ToString())));
        }
      }
    }
    return entries;
  }

  /// <summary>
  /// Detects the primary codec (Opus via the <c>OpusHead</c> identification packet,
  /// otherwise Vorbis), decodes the whole bitstream to interleaved 16-bit PCM, and
  /// adds one mono <c>&lt;CHANNEL&gt;.wav</c> per channel. Anything the decoders
  /// reject (Vorbis floor 0, Opus hybrid, multiplexed video, truncation) is skipped
  /// so only the raw packet blobs remain.
  /// </summary>
  private static void AddDecodedChannels(byte[] blob, List<(string Name, string Kind, byte[] Data)> entries) {
    try {
      var isOpus = IndexOf(blob, "OpusHead"u8) >= 0;
      int channels, sampleRate;
      byte[] pcm;

      using (var info = new MemoryStream(blob, writable: false))
      using (var src = new MemoryStream(blob, writable: false))
      using (var dst = new MemoryStream()) {
        if (isOpus) {
          var i = OpusCodec.ReadStreamInfo(info);
          channels = i.Channels;
          sampleRate = i.SampleRate > 0 ? i.SampleRate : 48000;
          OpusCodec.Decompress(src, dst);
        } else {
          var i = VorbisCodec.ReadStreamInfo(info);
          channels = i.Channels;
          sampleRate = i.SampleRate;
          VorbisCodec.Decompress(src, dst);
        }
        pcm = dst.ToArray();
      }

      if (channels < 1 || sampleRate <= 0 || pcm.Length == 0)
        return;

      if (channels == 1)
        entries.Add(("MONO.wav", "Channel", PcmCodec.ToWavBlob(pcm, 1, sampleRate, 16)));
      else
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, channels, sampleRate, 16))
          entries.Add(($"{name}.wav", "Channel", wav));
    } catch {
      // Undecodable / unsupported / not a single-audio-stream Ogg — packet blobs only.
    }
  }

  private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; ++i)
      if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
        return i;
    return -1;
  }

  // Raw packets are stored length-prefixed so downstream tools can reconstruct
  // packet boundaries without re-running the page parser.
  private static byte[] ConcatenateWithLengthPrefix(byte[][] packets) {
    using var ms = new MemoryStream();
    Span<byte> lenBuf = stackalloc byte[4];
    foreach (var p in packets) {
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)p.Length);
      ms.Write(lenBuf);
      ms.Write(p);
    }
    return ms.ToArray();
  }
}
