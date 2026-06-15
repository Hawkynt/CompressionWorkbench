#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Txw;

/// <summary>
/// Exposes a Yamaha TX16W wave file (<c>.txw</c>) as a pseudo-archive: <c>FULL.txw</c>
/// (the byte-exact wave) plus the decoded mono channel (<c>MONO.wav</c>) and a
/// <c>metadata.ini</c> summary. TX16W stores 12-bit samples packed three bytes per two
/// samples; they are unpacked, sign-extended and shifted to 16-bit PCM. The header rate
/// code selects the playback rate (33333 / 50000 / 16667, default 16949). Create
/// re-packs a single mono WAV back to 12-bit (lossy: the low 4 bits are truncated).
/// </summary>
public sealed class TxwFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  public string Id => "Txw";
  public string DisplayName => "TXW (Yamaha TX16W)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".txw";
  public IReadOnlyList<string> Extensions => [".txw"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // "LM8953" followed by two NUL bytes is the TX16W wave signature.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x4C, 0x4D, 0x38, 0x39, 0x35, 0x33, 0x00, 0x00], Offset: 0, Confidence: 0.9),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Yamaha TX16W wave; full file + decoded 12-bit mono WAV.";

  private const int HeaderSize = 32;
  private const int RateCodeOffset = 26;

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── parsing ────────────────────────────────────────────────────────────────

  public static int RateFromCode(byte code) => (code & 0x07) switch {
    1 => 33333,
    2 => 50000,
    3 => 16667,
    _ => 16949,
  };

  public static byte CodeFromRate(int rate) => rate switch {
    33333 => 1,
    50000 => 2,
    16667 => 3,
    _ => 0,
  };

  /// <summary>Unpacks 12-bit packed TXW data (3 bytes → 2 samples) into 16-bit signed LE PCM.</summary>
  public static byte[] Decode12Bit(ReadOnlySpan<byte> packed) {
    var pairs = packed.Length / 3;
    var pcm = new byte[pairs * 2 * 2];
    var o = 0;
    for (var i = 0; i < pairs; ++i) {
      var b0 = packed[i * 3];
      var b1 = packed[i * 3 + 1];
      var b2 = packed[i * 3 + 2];
      var s1 = (b0 << 4) | (b1 >> 4);          // 12-bit
      var s2 = ((b1 & 0x0F) << 8) | b2;        // 12-bit
      WriteSample(pcm, ref o, s1);
      WriteSample(pcm, ref o, s2);
    }
    return pcm;

    static void WriteSample(byte[] buf, ref int offset, int v12) {
      // Sign-extend from 12 bits, then shift up to 16-bit.
      if ((v12 & 0x800) != 0) v12 |= unchecked((int)0xFFFFF000);
      var s16 = (short)(v12 << 4);
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(offset), s16);
      offset += 2;
    }
  }

  /// <summary>Packs 16-bit signed LE PCM into 12-bit TXW data (lossy: low 4 bits dropped).</summary>
  public static byte[] Encode12Bit(ReadOnlySpan<byte> pcm16) {
    var samples = pcm16.Length / 2;
    var pairs = (samples + 1) / 2;
    var packed = new byte[pairs * 3];
    for (var i = 0; i < pairs; ++i) {
      var s1 = ReadS12(pcm16, i * 2);
      var s2 = (i * 2 + 1) < samples ? ReadS12(pcm16, i * 2 + 1) : 0;
      packed[i * 3] = (byte)((s1 >> 4) & 0xFF);
      packed[i * 3 + 1] = (byte)(((s1 & 0x0F) << 4) | ((s2 >> 8) & 0x0F));
      packed[i * 3 + 2] = (byte)(s2 & 0xFF);
    }
    return packed;

    static int ReadS12(ReadOnlySpan<byte> buf, int index) {
      var s16 = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(index * 2, 2));
      return (s16 >> 4) & 0x0FFF;  // 12-bit two's-complement field
    }
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.txw", "Container", blob),
    };
    if (blob.Length < HeaderSize)
      return entries;

    var rate = RateFromCode(blob[RateCodeOffset]);
    var packed = blob.AsSpan(HeaderSize);
    var pcm = Decode12Bit(packed);
    if (pcm.Length > 0)
      entries.Add(new("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: rate, bitsPerSample: 16)));

    var info = new StringBuilder();
    info.AppendLine($"sample_rate={rate}");
    info.AppendLine($"rate_code={blob[RateCodeOffset] & 0x07}");
    info.AppendLine($"sample_count={pcm.Length / 2}");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  // ── IArchiveCreatable: pack a single mono WAV back to a 12-bit TX16W wave ──

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.txw", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wavInput = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wavInput.Data == null)
      throw new InvalidOperationException("TXW archive create needs FULL.txw or one mono WAV.");

    var wav = new WavReader().Read(wavInput.Data);
    if (wav.NumChannels != 1)
      throw new InvalidOperationException("TXW assembly accepts a single mono WAV.");

    var pcm16 = wav.BitsPerSample switch {
      16 => wav.InterleavedPcm,
      8 => Eight2Sixteen(wav.InterleavedPcm),
      _ => throw new InvalidOperationException("TXW assembly accepts 8-bit or 16-bit mono WAVs."),
    };

    var packed = Encode12Bit(pcm16);
    var header = new byte[HeaderSize];
    "LM8953"u8.CopyTo(header);                 // magic + trailing zeros
    header[RateCodeOffset] = CodeFromRate(wav.SampleRate);

    output.Write(header);
    output.Write(packed);
  }

  private static byte[] Eight2Sixteen(byte[] pcm8) {
    var r = new byte[pcm8.Length * 2];
    for (var i = 0; i < pcm8.Length; ++i) {
      var s = (short)((pcm8[i] - 128) << 8);
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(i * 2), s);
    }
    return r;
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "TXW archive accepts: FULL.txw or one mono WAV (packed to lossy 12-bit)";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.txw" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not a TXW-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }
}
