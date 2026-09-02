#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Wave64;

/// <summary>
/// Exposes a Sony Wave64 (.w64) file as an archive of <c>FULL.w64</c> plus one mono WAV
/// per channel plus any ancillary chunks. Mirrors <c>WavFormatDescriptor</c>; Wave64 is a
/// RIFF-like container with 16-byte GUIDs and 64-bit little-endian sizes.
/// </summary>
public sealed class Wave64FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Wave64";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Wave64 (.w64)";
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
public string DefaultExtension => ".w64";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".w64"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Wave64Reader.RiffGuid, Confidence: 0.95),
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
public string Description => "Sony Wave64 audio; full file + per-channel PCM + ancillary chunks.";

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

  // ── IArchiveCreatable: passthrough FULL.w64, or interleave per-channel WAVs into a .w64 ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // If FULL.w64 is provided, passthrough it verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f => System.IO.Path.GetFileName(f.Name).Equals("FULL.w64", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    // Otherwise, look for per-channel mono WAVs and interleave them.
    var channelBlobs = fileList
      .Where(f => System.IO.Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelOrder(System.IO.Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("Wave64 archive create needs either FULL.w64 or one or more per-channel WAVs.");

    var channels = channelBlobs.Select(f => new WavReader().Read(f.Data)).ToList();

    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");

    var bytesPerSample = first.BitsPerSample / 8;
    var frameCount = first.InterleavedPcm.Length / bytesPerSample;
    if (channels.Any(c => c.InterleavedPcm.Length / bytesPerSample != frameCount))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), first.BitsPerSample);

    WriteWave64(output, interleaved, channels.Count, first.SampleRate, first.BitsPerSample, formatCode: 1);
  }

  /// <summary>
  /// Writes a minimal valid Wave64 file: riff/wave preamble + <c>fmt </c> + <c>data</c>
  /// chunks. Sizes are 64-bit little-endian and include the 16-byte GUID and the 8-byte
  /// size field (so a body of N bytes yields chunkSize N+24); whole chunks pad to 8 bytes.
  /// </summary>
  private static void WriteWave64(Stream output, byte[] pcm, int channels, int sampleRate, int bitsPerSample, int formatCode) {
    var byteRate = sampleRate * channels * bitsPerSample / 8;
    var blockAlign = (ushort)(channels * bitsPerSample / 8);

    // fmt body = WAVEFORMATEX (16 bytes); fmt chunk = 24 + 16 = 40 (already 8-aligned).
    var fmt = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(0), (ushort)formatCode);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
    BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), (uint)byteRate);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), blockAlign);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bitsPerSample);

    var fmtChunkSize = 24L + fmt.Length;
    var fmtPadded = (fmtChunkSize + 7) & ~7L;
    var dataChunkSize = 24L + pcm.Length;
    var dataPadded = (dataChunkSize + 7) & ~7L;

    // Total file = riff guid (16) + fileSize (8) + wave guid (16) + padded chunks.
    var fileSize = 40L + fmtPadded + dataPadded;

    var header = new byte[40];
    Wave64Reader.RiffGuid.CopyTo(header.AsSpan(0));
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)fileSize);
    Wave64Reader.WaveGuid.CopyTo(header.AsSpan(24));
    output.Write(header);

    WriteChunk(output, Wave64Reader.FmtGuid, fmt);
    WriteChunk(output, Wave64Reader.DataGuid, pcm);
  }

  private static void WriteChunk(Stream output, byte[] guid, byte[] body) {
    var chunkSize = 24L + body.Length;
    var hdr = new byte[24];
    guid.CopyTo(hdr.AsSpan(0));
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(16), (ulong)chunkSize);
    output.Write(hdr);
    output.Write(body);
    var pad = (int)(((chunkSize + 7) & ~7L) - chunkSize);
    if (pad > 0)
      output.Write(new byte[pad]);
  }

  // Canonical speaker ordering (FFmpeg/WAVE bit order, mono through 22.2).
  private static int ChannelOrder(string name) => ChannelLayout.OrderIndex(name);

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "Wave64 archive accepts: FULL.w64, LEFT/RIGHT/CENTER/… .wav (per-channel), metadata/*.bin";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = System.IO.Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = System.IO.Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";

    if (dir == "" && name.Equals("full.w64", StringComparison.Ordinal)) { reason = null; return true; }
    if (dir == "" && name.EndsWith(".wav")) { reason = null; return true; }
    if (dir == "metadata" && name.EndsWith(".bin")) { reason = null; return true; }
    reason = $"not a Wave64-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new Wave64Reader().Read(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.w64", "Container", blob, WavCodecName(parsed.FormatCode)),
    };

    // Split PCM integer formats (code 1) per-channel; float/other are skipped.
    if (parsed.FormatCode == 1 && parsed.BitsPerSample is 8 or 16 or 24 or 32 && parsed.NumChannels > 1) {
      foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
          parsed.InterleavedPcm, parsed.NumChannels, parsed.SampleRate, parsed.BitsPerSample,
          parsed.ChannelMask))
        entries.Add(new($"{name}.wav", "Channel", wavBlob, "pcm"));
    } else if (parsed.FormatCode == 1 && parsed.BitsPerSample is 8 or 16 or 24 or 32) {
      var mono = PcmCodec.ToWavBlob(parsed.InterleavedPcm, channels: 1, parsed.SampleRate, parsed.BitsPerSample, formatCode: 1);
      entries.Add(new("MONO.wav", "Channel", mono, "pcm"));
    }

    foreach (var (guid, data) in parsed.OtherChunks)
      entries.Add(new($"metadata/{ChunkLabel(guid)}.bin", "Tag", data));

    return entries;
  }

  /// <summary>Labels a chunk GUID by its ASCII 4CC when the leading four bytes are
  /// printable, otherwise by the full GUID in hex.</summary>
  private static string ChunkLabel(byte[] guid) {
    var printable = guid.Take(4).All(b => b is >= 0x20 and < 0x7F);
    if (printable)
      return System.Text.Encoding.ASCII.GetString(guid, 0, 4).Trim();
    return Convert.ToHexString(guid).ToLowerInvariant();
  }

  /// <summary>Maps the <c>wFormatTag</c> from the <c>fmt </c> chunk to a short codec name.</summary>
  private static string WavCodecName(int formatCode) => formatCode switch {
    0x0001 => "pcm",
    0x0003 => "pcm_float",
    0x0006 => "alaw",
    0x0007 => "mulaw",
    0xFFFE => "extensible",
    _ => $"format_0x{formatCode:X4}",
  };
}
