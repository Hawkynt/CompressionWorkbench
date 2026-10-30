#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.BlakHole;

/// <summary>One member of a BlakHole archive.</summary>
public sealed record BlakHoleEntry(string Name, int Method, uint CompressedSize, uint OriginalSize, uint Crc32,
  DateTime? LastModified, long DataOffset);

/// <summary>
/// BlakHole archives (.bh), read from the layout measured on the one sample with provenance:
/// back-to-back "BH" member records with raw-Deflate data and a CRC-32 (docs/BLAKHOLE-ON-DISK.md).
/// Every field whose meaning is unknown must hold the value the sample has; anything else is
/// refused rather than guessed. Read-only, because there is no BlakHole program to check writes.
/// </summary>
public static class BlakHoleArchive {
  private const int FixedHeader = 37;

  /// <summary>Parses the records.</summary>
  /// <param name="data">The whole archive.</param>
  /// <param name="truncated">Set when a record does not have the measured shape or runs past the end.</param>
  /// <exception cref="InvalidDataException">The data does not start with a BlakHole record.</exception>
  public static List<BlakHoleEntry> Read(ReadOnlySpan<byte> data, out bool truncated) {
    truncated = false;
    var entries = new List<BlakHoleEntry>();
    for (var at = 0; at < data.Length;) {
      var record = data[at..];
      if (record.Length < FixedHeader || record[0] != 'B' || record[1] != 'H' || record[2] != 5 || record[3] != 7
          || !record.Slice(6, 4).SequenceEqual(new byte[] { 0x25, 0xD8, 0x0C, 0x00 }) || record.Slice(28, 5).IndexOfAnyExcept((byte)0) >= 0) {
        if (at == 0)
          throw new InvalidDataException("Not a BlakHole archive of the known layout (see docs/BLAKHOLE-ON-DISK.md).");
        truncated = true;
        break;
      }

      var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
      var nameLength = BinaryPrimitives.ReadInt32LittleEndian(record[33..]);
      var compressed = BinaryPrimitives.ReadUInt32LittleEndian(record[15..]);
      if (nameLength < 0 || headerLength != FixedHeader + nameLength || headerLength > record.Length
          || compressed > (uint)(record.Length - headerLength)) {
        truncated = true;
        break;
      }

      var dos = BinaryPrimitives.ReadUInt32LittleEndian(record[11..]);
      entries.Add(new BlakHoleEntry(
        Encoding.Latin1.GetString(record.Slice(FixedHeader, nameLength)).Replace('\\', '/'),
        record[10],
        compressed,
        BinaryPrimitives.ReadUInt32LittleEndian(record[19..]),
        BinaryPrimitives.ReadUInt32LittleEndian(record[23..]),
        DosTime(dos),
        at + headerLength));
      at += headerLength + (int)compressed;
    }

    return entries;
  }

  private static DateTime? DosTime(uint value) {
    var date = (int)(value >> 16);
    var time = (int)(value & 0xFFFF);
    try {
      return new DateTime(1980 + (date >> 9), (date >> 5) & 15, date & 31, time >> 11, (time >> 5) & 63, (time & 31) * 2);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }

  /// <summary>Decodes a member and checks its CRC-32.</summary>
  /// <exception cref="NotSupportedException">The member uses a method other than the measured raw Deflate (8).</exception>
  /// <exception cref="InvalidDataException">The data is corrupt.</exception>
  public static byte[] Extract(ReadOnlySpan<byte> data, BlakHoleEntry entry) {
    if (entry.Method != 8)
      throw new NotSupportedException($"BlakHole method {entry.Method} has not been observed; only 8 (Deflate) is read.");
    var result = DeflateDecompressor.Decompress(data.Slice((int)entry.DataOffset, (int)entry.CompressedSize));
    if (result.Length != entry.OriginalSize || Compression.Core.Checksums.Crc32.Compute(result) != entry.Crc32)
      throw new InvalidDataException($"BlakHole member '{entry.Name}' does not match its size and CRC-32.");
    return result;
  }
}

/// <summary>
/// BlakHole archive (.bh), read-only.
///
/// References:
/// <list type="bullet">
///   <item><description><c>docs/BLAKHOLE-ON-DISK.md</c> — the layout measured on the Archive Team sample (no published specification exists)</description></item>
///   <item><description><c>http://sembiance.com/fileFormatSamples/archive/blakHole/</c> — the sample</description></item>
/// </list>
/// </summary>
public sealed class BlakHoleFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "BlakHole";
  public string DisplayName => "BlakHole";
  public FormatCategory Category => FormatCategory.Archive;
  // RO: decoded bytes are proven by each member's CRC-32; there is no BlakHole program to check writes.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".bh";
  public IReadOnlyList<string> Extensions => [".bh"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'B', (byte)'H', 5, 7], Confidence: 0.7)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "BlakHole archive: Deflate members with CRC-32, layout measured on a real sample; read-only";

  private static byte[] ReadAll(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    return BlakHoleArchive.Read(data, out _).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.OriginalSize, e.CompressedSize,
      e.Method == 8 ? "deflate" : $"method{e.Method}", false, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    foreach (var e in BlakHoleArchive.Read(data, out _)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, BlakHoleArchive.Extract(data, e));
    }
  }
}
