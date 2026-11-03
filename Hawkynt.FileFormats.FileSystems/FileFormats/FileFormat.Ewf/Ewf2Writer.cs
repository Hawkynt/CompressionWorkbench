using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using FileFormat.Zlib;

namespace FileFormat.Ewf;

/// <summary>
/// Writes a single-segment EWF2-Ex01 image (libyal's <i>EWF 2 specification</i>): device
/// information, case data, the chunks (zlib-compressed where that is smaller, else stored with an
/// Adler-32 trailer), one sector table, the MD5 and SHA-1 of the media, and "done". Every section
/// and chunk is 16-byte aligned. Chunks are never pattern-filled.
/// </summary>
public sealed class Ewf2Writer {
  private const int BytesPerSector = 512, SectorsPerChunk = 64, ChunkSize = BytesPerSector * SectorsPerChunk;

  /// <summary>Whether chunks are zlib-compressed (when that makes them smaller).</summary>
  public bool Compress { get; init; } = true;
  /// <summary>The acquisition time recorded in the case data.</summary>
  public DateTimeOffset AcquisitionTime { get; init; } = DateTimeOffset.UtcNow;
  /// <summary>The segment file set identifier.</summary>
  public Guid SetIdentifier { get; init; } = Guid.NewGuid();
  /// <summary>Case name, case number, evidence number, examiner and notes.</summary>
  public (string Name, string CaseNumber, string EvidenceNumber, string Examiner, string Notes) Case { get; init; } = ("", "", "", "", "");

  /// <summary>
  /// Writes <paramref name="media"/> as an Ex01 image to <paramref name="output"/>; a trailing
  /// partial sector is padded with zeros to a whole one, as EWF stores whole sectors.
  /// </summary>
  public void Write(Stream output, ReadOnlySpan<byte> media) {
    ArgumentNullException.ThrowIfNull(output);
    if (media.Length % BytesPerSector != 0) {
      var padded = new byte[(media.Length + BytesPerSector - 1) / BytesPerSector * BytesPerSector];
      media.CopyTo(padded);
      media = padded;
    }
    var image = new MemoryStream();
    Span<byte> header = stackalloc byte[Ewf2Image.FileHeaderSize];
    Ewf2Image.Signature.CopyTo(header);
    header[8] = 2;
    header[9] = 1;
    // The method is zlib even when no chunk is compressed: the specification says no compression is
    // never used here, and libewf refuses a header that says so.
    BinaryPrimitives.WriteUInt16LittleEndian(header[10..], Ewf2Image.CompressionZlib);
    BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 1);
    this.SetIdentifier.TryWriteBytes(header[16..]);
    image.Write(header);

    var chunkCount = (media.Length + ChunkSize - 1) / ChunkSize;
    long previous = 0;
    var device = ObjectText(["sn", "md", "lb", "ts", "hs", "dc", "dt", "pid", "rs", "ls", "bp", "ph"],
      ["", "", "", (media.Length / BytesPerSector).ToString(CultureInfo.InvariantCulture), "", "", "f", "", "", "", BytesPerSector.ToString(CultureInfo.InvariantCulture), "1"]);
    previous = WriteSection(image, Ewf2Image.TypeDeviceInformation, ZlibStream.Compress(device), previous);
    var time = this.AcquisitionTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    var caseText = ObjectText(["nm", "cn", "en", "ex", "nt", "av", "os", "tt", "at", "tb", "cp", "sb", "gr", "wb"],
      [Clean(this.Case.Name), Clean(this.Case.CaseNumber), Clean(this.Case.EvidenceNumber), Clean(this.Case.Examiner), Clean(this.Case.Notes),
        "CompressionWorkbench", "", time, time, chunkCount.ToString(CultureInfo.InvariantCulture), this.Compress ? "1" : "",
        SectorsPerChunk.ToString(CultureInfo.InvariantCulture), SectorsPerChunk.ToString(CultureInfo.InvariantCulture), ""]);
    previous = WriteSection(image, Ewf2Image.TypeCaseData, ZlibStream.Compress(caseText), previous);

    // Sector data: every chunk 16-byte aligned; the table records each one's offset, size and flags.
    var dataStart = image.Position;
    var table = new byte[chunkCount * 16];
    var padding = 0u;
    for (var i = 0; i < chunkCount; ++i) {
      var chunk = media.Slice(i * ChunkSize, Math.Min(ChunkSize, media.Length - i * ChunkSize));
      byte[] stored;
      uint flags;
      var packed = this.Compress ? ZlibStream.Compress(chunk) : null;
      if (packed != null && packed.Length < chunk.Length) {
        stored = packed;
        flags = Ewf2Image.ChunkCompressed;
      } else {
        stored = new byte[chunk.Length + 4];
        chunk.CopyTo(stored);
        BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(chunk.Length), Adler32.Compute(chunk));
        flags = Ewf2Image.ChunkChecksummed;
      }
      var entry = table.AsSpan(i * 16, 16);
      BinaryPrimitives.WriteUInt64LittleEndian(entry, (ulong)image.Position);
      BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)stored.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], flags);
      image.Write(stored);
      padding += (uint)Pad(image);
    }
    previous = WriteDescriptor(image, Ewf2Image.TypeSectorData, image.Position - dataStart, padding, previous);

    var tableData = new byte[32 + table.Length + 16];
    BinaryPrimitives.WriteUInt32LittleEndian(tableData.AsSpan(8), (uint)chunkCount);
    BinaryPrimitives.WriteUInt32LittleEndian(tableData.AsSpan(16), Adler32.Compute(tableData.AsSpan(0, 16)));
    table.CopyTo(tableData, 32);
    BinaryPrimitives.WriteUInt32LittleEndian(tableData.AsSpan(32 + table.Length), Adler32.Compute(table));
    image.Write(tableData);
    previous = WriteDescriptor(image, Ewf2Image.TypeSectorTable, tableData.Length, 24, previous);

    previous = WriteSection(image, Ewf2Image.TypeMd5, HashData(MD5.HashData(media)), previous);
    previous = WriteSection(image, Ewf2Image.TypeSha1, HashData(SHA1.HashData(media)), previous);
    WriteDescriptor(image, Ewf2Image.TypeDone, 0, 0, previous);

    image.Position = 0;
    image.CopyTo(output);
  }

  private static byte[] HashData(byte[] hash) {
    var data = new byte[hash.Length + 4];
    hash.CopyTo(data, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(hash.Length), Adler32.Compute(hash));
    return data;
  }

  /// <summary>"1", "main", the tag line, the value line and an empty line, as UTF-16LE with a byte order mark.</summary>
  private static byte[] ObjectText(string[] tags, string[] values) {
    var text = $"1\nmain\n{string.Join('\t', tags)}\n{string.Join('\t', values)}\n\n";
    return [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)];
  }

  private static string Clean(string value) => value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

  /// <summary>Writes the data, pads it to 16 bytes and appends the descriptor; returns the descriptor's offset.</summary>
  private static long WriteSection(Stream image, uint type, byte[] data, long previous) {
    var start = image.Position;
    image.Write(data);
    var padding = Pad(image);
    return WriteDescriptor(image, type, image.Position - start, (uint)padding, previous);
  }

  private static long WriteDescriptor(Stream image, uint type, long dataSize, uint padding, long previous) {
    Span<byte> d = stackalloc byte[Ewf2Image.DescriptorSize];
    d.Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(d, type);
    BinaryPrimitives.WriteUInt64LittleEndian(d[8..], (ulong)previous);
    BinaryPrimitives.WriteUInt64LittleEndian(d[16..], (ulong)dataSize);
    BinaryPrimitives.WriteUInt32LittleEndian(d[24..], Ewf2Image.DescriptorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(d[28..], padding);
    BinaryPrimitives.WriteUInt32LittleEndian(d[60..], Adler32.Compute(d[..60]));
    var at = image.Position;
    image.Write(d);
    return at;
  }

  private static int Pad(Stream image) {
    var pad = (int)((16 - image.Position % 16) % 16);
    for (var i = 0; i < pad; ++i)
      image.WriteByte(0);
    return pad;
  }
}
