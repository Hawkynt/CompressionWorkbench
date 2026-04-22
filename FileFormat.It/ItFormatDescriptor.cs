#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.It;

/// <summary>
/// Exposes an Impulse Tracker (IT) module as an archive of <c>FULL.it</c>,
/// <c>metadata.ini</c>, <c>patterns/pattern_NN.bin</c>, <c>instruments/NN_{name}.bin</c>
/// (raw instrument-envelope blocks) and <c>samples/NN_{name}.raw</c> / <c>_compressed.bin</c>
/// payloads. IT compression (bit 3 of the sample flags) is not decoded; the
/// compressed bytes are surfaced verbatim with a <c>_compressed.bin</c> suffix.
/// </summary>
public sealed class ItFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "It";
  public string DisplayName => "IT (Impulse Tracker)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".it";
  public IReadOnlyList<string> Extensions => [".it"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("IMPM"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Impulse Tracker module; full file + patterns + instruments + raw PCM samples.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
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

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    return Parse(blob);
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> Parse(byte[] blob) {
    var entries = new List<(string, string, byte[])> {
      ("FULL.it", "Track", blob),
    };
    if (blob.Length < 192) return entries;

    var songName = ReadAsciiTrim(blob, 4, 26);
    var ordNum = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(32, 2));
    var insNum = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(34, 2));
    var smpNum = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(36, 2));
    var patNum = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(38, 2));

    // Offset tables start at 192 + ordNum (order list) — instrument offsets, sample offsets, pattern offsets.
    var insOffsetsStart = 192 + ordNum;
    var smpOffsetsStart = insOffsetsStart + insNum * 4;
    var patOffsetsStart = smpOffsetsStart + smpNum * 4;

    // Patterns first (preserve insert order stability for tests).
    for (var p = 0; p < patNum; ++p) {
      var off = patOffsetsStart + p * 4;
      if (off + 4 > blob.Length) break;
      var patOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off, 4));
      if (patOff == 0 || patOff + 4 > blob.Length) continue;
      var packedSize = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(patOff, 2));
      var rows = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(patOff + 2, 2));
      var dataStart = patOff + 8;
      if (dataStart + packedSize > blob.Length) continue;
      var data = new byte[packedSize];
      if (packedSize > 0) Buffer.BlockCopy(blob, dataStart, data, 0, packedSize);
      entries.Add(($"patterns/pattern_{p:D2}_r{rows}.bin", "Pattern", data));
    }

    // Instruments — surface the raw instrument block as metadata (554 bytes for IT 2.00+,
    // 64 bytes for pre-2.00 "old instrument" blocks). We just emit a sized chunk up to
    // the next known boundary.
    for (var i = 0; i < insNum; ++i) {
      var tableOff = insOffsetsStart + i * 4;
      if (tableOff + 4 > blob.Length) break;
      var insOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(tableOff, 4));
      if (insOff == 0 || insOff + 64 > blob.Length) continue;
      // IT 2.00+ instrument header is 554 bytes and starts with "IMPI".
      var isNew = insOff + 4 <= blob.Length && blob[insOff] == (byte)'I' && blob[insOff + 1] == (byte)'M' &&
                  blob[insOff + 2] == (byte)'P' && blob[insOff + 3] == (byte)'I';
      var insSize = isNew ? 554 : 64;
      var take = Math.Min(insSize, blob.Length - insOff);
      if (take <= 0) continue;
      var data = new byte[take];
      Buffer.BlockCopy(blob, insOff, data, 0, take);
      var nameOff = isNew ? insOff + 32 : insOff + 20;
      var name = ReadAsciiTrim(blob, nameOff, 26);
      var label = string.IsNullOrWhiteSpace(name) ? $"instrument_{i + 1:D2}" : SanitizeFileName(name);
      entries.Add(($"instruments/{(i + 1):D2}_{label}.bin", "Instrument", data));
    }

    // Samples.
    for (var s = 0; s < smpNum; ++s) {
      var tableOff = smpOffsetsStart + s * 4;
      if (tableOff + 4 > blob.Length) break;
      var smpHdrOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(tableOff, 4));
      if (smpHdrOff == 0 || smpHdrOff + 80 > blob.Length) continue;
      // "IMPS" magic at the sample header.
      if (blob[smpHdrOff] != (byte)'I' || blob[smpHdrOff + 1] != (byte)'M' ||
          blob[smpHdrOff + 2] != (byte)'P' || blob[smpHdrOff + 3] != (byte)'S') continue;
      var dosName = ReadAsciiTrim(blob, smpHdrOff + 4, 12);
      var flags = blob[smpHdrOff + 18];
      var sampleName = ReadAsciiTrim(blob, smpHdrOff + 20, 26);
      var length = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(smpHdrOff + 48, 4)));
      var dataOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(smpHdrOff + 72, 4));
      var hasData = (flags & 0x01) != 0;
      var is16 = (flags & 0x02) != 0;
      var isCompressed = (flags & 0x08) != 0;
      if (!hasData || length == 0 || dataOff <= 0 || dataOff >= blob.Length) continue;
      var byteLen = (long)length * (is16 ? 2 : 1);
      var take = (int)Math.Min(byteLen, blob.Length - dataOff);
      if (take <= 0) continue;
      var data = new byte[take];
      Buffer.BlockCopy(blob, dataOff, data, 0, take);
      var label = string.IsNullOrWhiteSpace(sampleName) ? (string.IsNullOrWhiteSpace(dosName) ? $"sample_{s + 1:D2}" : dosName) : sampleName;
      var safeLabel = SanitizeFileName(label);
      var suffix = isCompressed ? "_compressed.bin" : (is16 ? "_s16le.raw" : ".raw");
      entries.Add(($"samples/{(s + 1):D2}_{safeLabel}{suffix}", "Sample", data));
    }

    var info = new StringBuilder();
    info.AppendLine($"name={songName}");
    info.AppendLine($"signature=IMPM");
    info.AppendLine($"order_num={ordNum}");
    info.AppendLine($"patterns_count={patNum}");
    info.AppendLine($"instruments_count={insNum}");
    info.AppendLine($"samples_count={smpNum}");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static string ReadAsciiTrim(byte[] blob, int offset, int length) {
    var end = offset + length;
    if (end > blob.Length) end = blob.Length;
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
    foreach (var c in name) {
      if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
      else sb.Append('_');
    }
    var s = sb.ToString().Trim('.');
    return s.Length == 0 ? "sample" : s;
  }
}
