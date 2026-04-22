#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.S3m;

/// <summary>
/// Exposes a Scream Tracker 3 (S3M) module as an archive of <c>FULL.s3m</c>,
/// <c>metadata.ini</c>, <c>patterns/pattern_NN.bin</c> (raw packed pattern blocks
/// with their 2-byte length prefix stripped), and <c>samples/NN_{name}.raw</c>
/// carrying each instrument's raw sample payload. Stereo and 16-bit flags are
/// recorded in metadata but bytes are surfaced verbatim.
/// </summary>
public sealed class S3mFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "S3m";
  public string DisplayName => "S3M (Scream Tracker 3)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".s3m";
  public IReadOnlyList<string> Extensions => [".s3m"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SCRM"u8.ToArray(), Offset: 44, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Scream Tracker 3 module; full file + patterns + raw PCM samples.";

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
      ("FULL.s3m", "Track", blob),
    };
    if (blob.Length < 96) return entries;

    var title = ReadAsciiTrim(blob, 0, 28);
    var songLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(32, 2));
    var numInstruments = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(34, 2));
    var numPatterns = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(36, 2));

    // Order table starts at 96, songLen bytes.
    var instrParaOff = 96 + songLen;
    var patternParaOff = instrParaOff + numInstruments * 2;

    // Parse patterns: each pattern's parapointer → offset (×16); first two bytes give the
    // packed pattern length (including the 2-byte length itself on some writers), then data.
    for (var p = 0; p < numPatterns; ++p) {
      var pp = patternParaOff + p * 2;
      if (pp + 2 > blob.Length) break;
      var para = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pp, 2));
      if (para == 0) continue;
      var off = para * 16;
      if (off + 2 > blob.Length) continue;
      var length = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off, 2));
      if (length < 2 || off + length > blob.Length) continue;
      var data = new byte[length - 2];
      Buffer.BlockCopy(blob, off + 2, data, 0, data.Length);
      entries.Add(($"patterns/pattern_{p:D2}.bin", "Pattern", data));
    }

    // Parse instruments: each instrument header is 80 bytes at (parapointer × 16).
    var instrumentsWithData = 0;
    for (var s = 0; s < numInstruments; ++s) {
      var ip = instrParaOff + s * 2;
      if (ip + 2 > blob.Length) break;
      var para = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(ip, 2));
      if (para == 0) continue;
      var off = para * 16;
      if (off + 80 > blob.Length) continue;
      var type = blob[off];
      // Type 1 = PCM sample; 0 = empty; >1 = adlib/other — skip non-PCM.
      if (type != 1) continue;
      var dosName = ReadAsciiTrim(blob, off + 1, 12);
      // Sample data parapointer stored as MemSeg: high byte at +13, low word at +14 LE.
      var memSegHi = blob[off + 13];
      var memSegLo = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off + 14, 2));
      var memSeg = (memSegHi << 16) | memSegLo;
      var length = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 16, 4));
      var flags = blob[off + 31];
      var is16Bit = (flags & 0x04) != 0;
      var dataOff = memSeg * 16;
      var byteLen = (long)length * (is16Bit ? 2 : 1);
      if (dataOff < 0 || dataOff >= blob.Length || length == 0) continue;
      var take = (int)Math.Min(byteLen, blob.Length - dataOff);
      if (take <= 0) continue;
      var data = new byte[take];
      Buffer.BlockCopy(blob, dataOff, data, 0, take);
      var sampleName = ReadAsciiTrim(blob, off + 35, 28);
      var label = string.IsNullOrWhiteSpace(sampleName) ? dosName : sampleName;
      entries.Add(($"samples/{(s + 1):D2}_{SanitizeFileName(label)}.raw", "Sample", data));
      ++instrumentsWithData;
    }

    var info = new StringBuilder();
    info.AppendLine($"title={title}");
    info.AppendLine($"signature=SCRM");
    info.AppendLine($"song_length={songLen}");
    info.AppendLine($"num_patterns={numPatterns}");
    info.AppendLine($"num_instruments={numInstruments}");
    info.AppendLine($"instruments_with_data={instrumentsWithData}");
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
