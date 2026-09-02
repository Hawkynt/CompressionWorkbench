#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Med;

/// <summary>
/// Exposes an OctaMED MMD0 / MMD1 module (big-endian Amiga) as a pseudo-archive of
/// <c>FULL.&lt;ext&gt;</c> (Kind <c>Container</c>), a <c>metadata.ini</c> (Kind <c>Tag</c>)
/// noting the MMD version, and one playable WAV per instrument sample (Kind <c>Sample</c>).
/// </summary>
/// <remarks>
/// PRAGMATIC SCOPE: the full MMD0 song structure is not walked. Instead the sample
/// pointer array at offset 24 (<c>smplarr</c>, a u32 pointer that points to an array
/// of u32 instrument offsets) is read until a null / out-of-range entry. Each pointed-to
/// block is treated as an <c>InstrHdr</c> (<c>u32 length</c>, <c>s16 type</c>); a
/// <c>type</c> of 0 means 8-bit signed PCM of <c>length</c> bytes which is rebiased to
/// WAV's unsigned 8-bit. Unknown / non-zero types are skipped. No per-sample rate is
/// stored in this view, so 8363 Hz is assumed (documented in metadata).
/// </remarks>
public sealed class MedFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Med";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "OctaMED (MMD0 / MMD1)";
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
public string DefaultExtension => ".med";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".med", ".mmd0", ".mmd1"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MMD0"u8.ToArray(), Offset: 0, Confidence: 0.9),
    new("MMD1"u8.ToArray(), Offset: 0, Confidence: 0.9),
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
public string Description => "OctaMED MMD0/MMD1 tracker module; full file + playable 8-bit sample WAVs.";

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
    return Parse(blob);
  }

  private const int AssumedSampleRate = 8363;

  private static IReadOnlyList<AudioPseudoArchive.Entry> Parse(byte[] blob) {
    var ext = blob.Length >= 4 && blob[3] == (byte)'1' ? ".mmd1" : ".mmd0";
    var entries = new List<AudioPseudoArchive.Entry> {
      new($"FULL{ext}", "Container", blob),
    };

    if (blob.Length < 28 ||
        !(blob[0] == 'M' && blob[1] == 'M' && blob[2] == 'D' && (blob[3] == '0' || blob[3] == '1')))
      return entries;

    var version = (char)blob[3];
    var sampleCount = 0;
    try {
      // smplarr pointer at offset 24 → array of u32 instrument offsets.
      var smplArrOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(24, 4));
      if (smplArrOffset > 0 && smplArrOffset + 4 <= blob.Length) {
        for (var i = 0; ; ++i) {
          var entryOff = smplArrOffset + i * 4;
          if (entryOff + 4 > blob.Length) break;
          var instrOff = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(entryOff, 4));
          if (instrOff == 0) {
            // Null pointer = empty slot; keep scanning a bounded window then stop.
            if (i >= 63) break;
            continue;
          }
          if (instrOff < 0 || instrOff + 6 > blob.Length) break;

          var length = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(instrOff, 4)));
          var type = BinaryPrimitives.ReadInt16BigEndian(blob.AsSpan(instrOff + 4, 2));
          var dataOff = instrOff + 6;
          if (length <= 0 || dataOff >= blob.Length) {
            if (i >= 63) break;
            continue;
          }
          var take = Math.Min(length, blob.Length - dataOff);
          if (take <= 0) { if (i >= 63) break; continue; }

          ++sampleCount;
          var idx = sampleCount;
          if (type == 0) {
            // 8-bit signed PCM → WAV unsigned 8-bit.
            var pcm = ToUnsigned8(blob.AsSpan(dataOff, take));
            entries.Add(new($"samples/{idx:D2}_sample.wav", "Sample",
              PcmCodec.ToWavBlob(pcm, 1, AssumedSampleRate, 8)));
          } else {
            // Unknown / non-8-bit instrument type → surface raw block, not as playable WAV.
            var raw = blob.AsSpan(dataOff, take).ToArray();
            entries.Add(new($"samples/{idx:D2}_type{type}.bin", "Sample", raw));
          }
          if (i >= 63) break;
        }
      }
    } catch {
      // Graceful FULL-only fallback on any structural surprise.
    }

    var info = new StringBuilder();
    info.AppendLine($"format=MMD{version}");
    info.AppendLine($"sample_count={sampleCount}");
    info.AppendLine($"sample_rate_assumed={AssumedSampleRate} (MMD sample view carries no per-sample rate)");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  /// <summary>Signed 8-bit PCM → WAV's unsigned 8-bit (add 128).</summary>
  private static byte[] ToUnsigned8(ReadOnlySpan<byte> signed) {
    var r = new byte[signed.Length];
    for (var i = 0; i < signed.Length; ++i)
      r[i] = unchecked((byte)(signed[i] + 128));
    return r;
  }
}
