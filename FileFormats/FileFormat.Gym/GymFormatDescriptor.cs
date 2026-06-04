#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Gym;

/// <summary>
/// Surfaces a Genesis/Mega Drive GYM register-log file (<c>.gym</c>) as a metadata-rich
/// pseudo-archive. A GYM file is a recording of writes to the YM2612 FM chip and SN76489 PSG;
/// there is no synthesised audio to decode, so the command log is surfaced verbatim as a Kind
/// <c>Stream</c> blob.
/// <para>The <c>GYMX</c> header carries five 32-byte song/game/copyright/emulator/dumper strings,
/// a 256-byte comment, a u32 loopStart frame and a u32 packedSize. When <c>packedSize != 0</c>
/// the log is zlib-compressed and surfaced as <c>log.z</c>; otherwise the raw log is surfaced as
/// <c>log.bin</c>. The log is a stream of command bytes where <c>0x00</c> is a 1/60-second frame
/// wait; counting those markers yields an approximate duration that is reported in the
/// metadata.</para>
/// Read-only; parsing degrades to FULL-only on malformed input.
/// </summary>
public sealed class GymFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Gym";
  public string DisplayName => "Genesis GYM log";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gym";
  public IReadOnlyList<string> Extensions => [".gym"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("GYMX"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Genesis GYM register-log (GYMX); full file + header metadata + YM2612/PSG command log.";

  // GYMX header layout.
  private const int HeaderSize = 0x1A4;        // 4 (magic) + 5*32 (strings) + 256 (comment) + 4 (loop) + 4 (packed)
  private const int LoopStartOffset = 0x19C;   // 4 + 160 + 256
  private const int PackedSizeOffset = 0x1A0;
  private const double FrameRate = 60.0;

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.gym", "Container", blob),
    };

    if (blob.Length < HeaderSize)
      return entries;

    var song = ReadFixed(blob, 0x04, 32);
    var game = ReadFixed(blob, 0x24, 32);
    var copyright = ReadFixed(blob, 0x44, 32);
    var emulator = ReadFixed(blob, 0x64, 32);
    var dumper = ReadFixed(blob, 0x84, 32);
    var comment = ReadFixed(blob, 0xA4, 256);
    var loopStart = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(LoopStartOffset));
    var packedSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(PackedSizeOffset));

    var sb = new StringBuilder();
    sb.AppendLine("[gym]");
    sb.AppendLine("variant=GYMX");
    AppendField(sb, "song", song);
    AppendField(sb, "game", game);
    AppendField(sb, "copyright", copyright);
    AppendField(sb, "emulator", emulator);
    AppendField(sb, "dumper", dumper);
    AppendField(sb, "comment", comment);
    sb.AppendLine($"loop_start_frame={loopStart}");
    sb.AppendLine($"packed_size={packedSize}");

    var log = blob[HeaderSize..];
    if (packedSize != 0) {
      sb.AppendLine("compression=zlib");
      sb.AppendLine($"note=log is zlib-compressed; surfaced as log.z (packed_size={packedSize} bytes)");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
      if (log.Length > 0)
        entries.Add(new("log.z", "Stream", log));
    } else {
      // Raw log: 0x00 bytes are 1/60s frame waits → duration estimate.
      var frames = 0L;
      foreach (var b in log)
        if (b == 0x00)
          ++frames;
      var seconds = frames / FrameRate;
      sb.AppendLine("compression=none");
      sb.AppendLine($"frame_count={frames}");
      sb.AppendLine($"duration_seconds={seconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
      if (log.Length > 0)
        entries.Add(new("log.bin", "Stream", log));
    }

    return entries;
  }

  private static string ReadFixed(byte[] blob, int offset, int length) {
    if (offset + length > blob.Length)
      length = Math.Max(0, blob.Length - offset);
    var raw = blob.AsSpan(offset, length);
    var end = raw.IndexOf((byte)0);
    if (end < 0)
      end = raw.Length;
    return Encoding.Latin1.GetString(raw[..end]).Trim();
  }

  private static void AppendField(StringBuilder sb, string key, string value) {
    value = value.Trim();
    if (value.Length > 0)
      sb.AppendLine($"{key}={value}");
  }
}
