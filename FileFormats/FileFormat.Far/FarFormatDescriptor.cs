#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Far;

/// <summary>
/// Exposes a Farandole Composer (<c>.far</c>) module as a read-only pseudo-archive
/// of <c>FULL.far</c> (byte-exact original), <c>metadata.ini</c>, the song message
/// text as <c>message.txt</c> and one playable mono WAV per present sample under
/// <c>samples/NN_{name}.wav</c>.
/// </summary>
/// <remarks>
/// Layout interpretation (all little-endian). The 4-byte magic is
/// <c>{'F','A','R',0xFE}</c>. Main header: <c>magic[4]</c>, <c>char[40] name</c>,
/// <c>u8[3] eof = {13,10,26}</c>, <c>u16 headerLen</c>, <c>u8 version</c>,
/// <c>u8 onOff[16]</c> channel-enable flags, 9 reserved bytes, <c>u16 messageLen</c>.
/// The variable part of the header (orders, pattern-size table, message text) is
/// spanned by <c>headerLen</c>, so the song text is read at offset 98 for
/// <c>messageLen</c> bytes and the sample section is located at
/// <c>headerLen</c>. The sample section opens with a <c>u8[8] / 64-bit</c> bitfield
/// marking which of 64 sample slots are present; each present slot has a 48-byte
/// header — <c>char[32] name</c>, <c>u32 length</c>, <c>u8 finetune</c>,
/// <c>u8 volume</c>, <c>u32 repStart</c>, <c>u32 repEnd</c>, <c>u8 type</c>
/// (bit0 = 16-bit), <c>u8 loop</c> — immediately followed by its sample data.
/// FAR samples are signed (8-bit signed → converted to unsigned-8 WAV; 16-bit
/// signed little-endian surfaced as-is). FAR stores no per-sample replay rate, so
/// a fixed 8363 Hz is used and recorded in <c>metadata.ini</c>. Simplification:
/// the order list and pattern grid inside the header are not individually
/// surfaced — only their span (<c>headerLen</c>) is used to find the samples.
/// </remarks>
public sealed class FarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Far";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "FAR (Farandole Composer)";
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
public string DefaultExtension => ".far";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".far"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'F', (byte)'A', (byte)'R', 0xFE], Confidence: 0.95),
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
public AlgorithmFamily Family => AlgorithmFamily.Classic;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Farandole Composer module; full file + message + per-sample WAVs.";

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
    return Parse(ms.ToArray());
  }

  private const int SampleRate = 8363;

  private static IReadOnlyList<AudioPseudoArchive.Entry> Parse(byte[] blob) {
    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.far", "Container", blob),
    };
    if (blob.Length < 98 || blob[0] != 'F' || blob[1] != 'A' || blob[2] != 'R' || blob[3] != 0xFE)
      return entries;

    var name = ReadAsciiTrim(blob, 4, 40);
    var headerLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(47, 2));
    var version = blob[49];
    var messageLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(96, 2));

    // Song message text sits at offset 98 for messageLen bytes.
    if (messageLen > 0 && 98 + messageLen <= blob.Length) {
      var msg = new byte[messageLen];
      Buffer.BlockCopy(blob, 98, msg, 0, messageLen);
      entries.Add(new("message.txt", "Tag", msg));
    }

    // Sample section begins at headerLen: 8-byte (64-bit) present-slot bitfield.
    var samplesWithData = 0;
    var presentSlots = 0;
    int off = headerLen;
    if (off + 8 <= blob.Length) {
      Span<bool> present = stackalloc bool[64];
      for (var i = 0; i < 64; ++i)
        present[i] = (blob[off + (i >> 3)] & (1 << (i & 7))) != 0;
      off += 8;

      for (var slot = 0; slot < 64; ++slot) {
        if (!present[slot]) continue;
        ++presentSlots;
        if (off + 48 > blob.Length) break;
        var sName = ReadAsciiTrim(blob, off, 32);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 32, 4));
        var type = blob[off + 46];
        var is16Bit = (type & 0x01) != 0;
        off += 48;
        if (length == 0) continue;
        if (off >= blob.Length) break;
        var take = (int)Math.Min(length, (uint)(blob.Length - off));
        if (take <= 0) break;
        var pcm = new byte[take];
        Buffer.BlockCopy(blob, off, pcm, 0, take);
        off += (int)length;
        byte[] wav;
        if (is16Bit) {
          wav = PcmCodec.ToWavBlob(pcm, channels: 1, SampleRate, bitsPerSample: 16);
        } else {
          // 8-bit signed → unsigned-8 WAV.
          var u = new byte[pcm.Length];
          for (var i = 0; i < pcm.Length; ++i) u[i] = (byte)(pcm[i] + 128);
          wav = PcmCodec.ToWavBlob(u, channels: 1, SampleRate, bitsPerSample: 8);
        }
        var label = string.IsNullOrWhiteSpace(sName) ? "sample" : SanitizeFileName(sName);
        entries.Add(new($"samples/{(slot + 1):D2}_{label}.wav", "Sample", wav));
        ++samplesWithData;
      }
    }

    var info = new StringBuilder();
    info.AppendLine($"format=FAR");
    info.AppendLine($"version=0x{version:X2}");
    info.AppendLine($"name={name}");
    info.AppendLine($"header_length={headerLen}");
    info.AppendLine($"message_length={messageLen}");
    info.AppendLine($"present_sample_slots={presentSlots}");
    info.AppendLine($"samples_with_data={samplesWithData}");
    info.AppendLine($"sample_rate={SampleRate}");
    info.AppendLine($"sample_8bit_encoding=signed");
    info.AppendLine($"note=FAR stores no per-sample replay rate; WAVs use 8363 Hz.");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static string ReadAsciiTrim(byte[] blob, int offset, int length) {
    var end = Math.Min(offset + length, blob.Length);
    var sb = new StringBuilder();
    for (var i = offset; i < end; ++i) {
      var b = blob[i];
      if (b == 0) break;
      if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
    }
    return sb.ToString().Trim();
  }

  private static string SanitizeFileName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    var s = sb.ToString().Trim('.');
    return s.Length == 0 ? "sample" : s;
  }
}
