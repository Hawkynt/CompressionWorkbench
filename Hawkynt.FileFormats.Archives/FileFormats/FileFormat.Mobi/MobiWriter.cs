using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Mobi;

/// <summary>
/// Writes a minimal unencrypted MOBI 7 book containing one UTF-8 HTML document: record 0 (PalmDOC
/// header, 232-byte MOBI header, EXTH, full name), the text in 4096-byte records, stored or
/// PalmDOC-compressed, and an end-of-file record. No index, image, FLIS or FCIS records are written.
/// Layout from the MobileRead MOBI and PDB pages; accepted by KindleUnpack (see the MOBI tests).
/// </summary>
internal static class MobiWriter {
  private const int TextBlockSize = 4096;
  private const int MobiHeaderSize = 232;

  /// <summary>The end-of-file record MobileRead's "Magic Records" section describes.</summary>
  private static readonly byte[] EndOfFileRecord = [0xE9, 0x8E, 0x0D, 0x0A];

  public static void Write(Stream output, ReadOnlySpan<byte> text, string databaseName, FormatCreateOptions options) {
    ushort method = options.MethodName?.ToLowerInvariant() switch {
      null or "" or "palmdoc" => 2,
      "stored" => 1,
      var unknown => throw new ArgumentException($"Unsupported MOBI compression method '{unknown}'.", nameof(options)),
    };

    var blockCount = Math.Max(1L, (text.Length + (long)TextBlockSize - 1) / TextBlockSize);
    if (blockCount >= ushort.MaxValue)
      throw new ArgumentException("MOBI text exceeds the 16-bit PalmDB record count field.", nameof(text));
    var textRecords = new List<byte[]>();
    if (text.IsEmpty) {
      textRecords.Add([]);
    } else {
      for (var offset = 0; offset < text.Length; offset += TextBlockSize) {
        var block = text.Slice(offset, Math.Min(TextBlockSize, text.Length - offset));
        textRecords.Add(method == 1 ? block.ToArray() : PalmDocCodec.Encode(block));
      }
    }

    var title = options.GetOption("title", Path.GetFileNameWithoutExtension(databaseName) ?? "Book");
    var record0 = BuildRecordZero(text.Length, textRecords.Count, method, title, options);
    var records = new List<byte[]>(textRecords.Count + 2) { record0 };
    records.AddRange(textRecords);
    records.Add(EndOfFileRecord);

    var headerLength = checked(78 + records.Count * 8);
    long nextOffset = headerLength;
    foreach (var record in records) {
      if (nextOffset > uint.MaxValue)
        throw new ArgumentException("The generated PalmDB would exceed the 32-bit record-offset limit.", nameof(text));
      nextOffset += record.Length;
    }
    if (nextOffset > uint.MaxValue)
      throw new ArgumentException("The generated PalmDB would exceed the 32-bit file-size limit.", nameof(text));

    var dbHeader = new byte[headerLength];
    Encoding.Latin1.GetBytes(title.AsSpan(0, Math.Min(title.Length, 31)), dbHeader.AsSpan(0, Math.Min(title.Length, 31)));
    "BOOK"u8.CopyTo(dbHeader.AsSpan(60));
    "MOBI"u8.CopyTo(dbHeader.AsSpan(64));
    BinaryPrimitives.WriteUInt32BigEndian(dbHeader.AsSpan(68), 1); // unique-ID seed; next-record-list stays zero
    BinaryPrimitives.WriteUInt16BigEndian(dbHeader.AsSpan(76), checked((ushort)records.Count));
    var dataOffset = headerLength;
    for (var i = 0; i < records.Count; ++i) {
      BinaryPrimitives.WriteUInt32BigEndian(dbHeader.AsSpan(78 + i * 8), checked((uint)dataOffset));
      dbHeader[78 + i * 8 + 4] = 0;
      var id = i + 1;
      dbHeader[78 + i * 8 + 5] = (byte)(id >> 16);
      dbHeader[78 + i * 8 + 6] = (byte)(id >> 8);
      dbHeader[78 + i * 8 + 7] = (byte)id;
      dataOffset = checked(dataOffset + records[i].Length);
    }

    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }
    output.Write(dbHeader);
    foreach (var record in records)
      output.Write(record);
  }

  private static byte[] BuildRecordZero(int textLength, int textRecordCount, ushort compression, string title, FormatCreateOptions options) {
    if (textRecordCount > ushort.MaxValue)
      throw new ArgumentException("MOBI text exceeds the 16-bit record count field.", nameof(textRecordCount));
    var language = 1033;
    if (options.HasOption("language") && !options.TryGetInt("language", out language))
      throw new ArgumentException("The MOBI language option must be a numeric locale code.", nameof(options));
    if (language < 0)
      throw new ArgumentException("The MOBI language locale code must be non-negative.", nameof(options));

    var exth = BuildExth(options, title);
    var fullName = Encoding.UTF8.GetBytes(title);
    var fullNameOffset = checked(16 + MobiHeaderSize + exth.Length);
    // The full name is followed by at least two zero bytes and the record padded to four bytes.
    var r0 = new byte[checked((fullNameOffset + fullName.Length + 2 + 3) & ~3)];

    // PalmDOC header.
    BinaryPrimitives.WriteUInt16BigEndian(r0, compression);
    BinaryPrimitives.WriteUInt32BigEndian(r0.AsSpan(4), (uint)textLength);
    BinaryPrimitives.WriteUInt16BigEndian(r0.AsSpan(8), (ushort)textRecordCount);
    BinaryPrimitives.WriteUInt16BigEndian(r0.AsSpan(10), TextBlockSize);
    // 12: encryption 0 (none).

    // MOBI header; offsets are from the start of record 0 (MobileRead "MOBI Header" table).
    "MOBI"u8.CopyTo(r0.AsSpan(0x10));
    Put(0x14, MobiHeaderSize);
    Put(0x18, 2);                                  // Mobipocket book
    Put(0x1C, 65001);                              // UTF-8
    Put(0x20, 1);                                  // unique id
    Put(0x24, 6);                                  // file version: MOBI 6/7
    r0.AsSpan(0x28, 0x50 - 0x28).Fill(0xFF);       // orthographic, inflection, names, keys, extra 0-5: absent
    Put(0x50, (uint)(textRecordCount + 1));        // first non-book record: the end-of-file record
    Put(0x54, (uint)fullNameOffset);
    Put(0x58, (uint)fullName.Length);
    Put(0x5C, (uint)language);
    // 0x60 input language, 0x64 output language: 0 (not a dictionary).
    Put(0x68, 6);                                  // minimum reader version
    Put(0x6C, uint.MaxValue);                      // first image record: none
    // 0x70..0x7F Huffman record offset/count and table offset/length: 0 (no HUFF/CDIC).
    Put(0x80, 0x40);                               // EXTH follows the header
    // 0x84..0xA3: 32 unknown bytes, zero.
    Put(0xA4, uint.MaxValue);
    Put(0xA8, uint.MaxValue);                      // DRM offset: no DRM
    Put(0xAC, uint.MaxValue);                      // DRM count: no DRM
    // 0xB0 DRM size, 0xB4 DRM flags, 0xB8..0xBF: 0.
    BinaryPrimitives.WriteUInt16BigEndian(r0.AsSpan(0xC0), 1);                            // first content record
    BinaryPrimitives.WriteUInt16BigEndian(r0.AsSpan(0xC2), checked((ushort)textRecordCount)); // last content record
    Put(0xC4, 1);
    Put(0xC8, uint.MaxValue);                      // FCIS record: none
    Put(0xCC, 1);
    Put(0xD0, uint.MaxValue);                      // FLIS record: none
    Put(0xD4, 1);
    // 0xD8..0xDF: 0.
    Put(0xE0, uint.MaxValue);
    // 0xE4 first compilation data section count: 0.
    Put(0xE8, uint.MaxValue);                      // compilation data sections: none
    Put(0xEC, uint.MaxValue);
    // 0xF0 extra record data flags: 0, no trailing entries on the text records.
    Put(0xF4, uint.MaxValue);                      // INDX record: none

    exth.CopyTo(r0.AsSpan(16 + MobiHeaderSize));
    fullName.CopyTo(r0.AsSpan(fullNameOffset));
    return r0;

    void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(r0.AsSpan(offset), value);
  }

  private static byte[] BuildExth(FormatCreateOptions options, string title) {
    var records = new List<(uint Type, byte[] Data)>();
    records.Add((503, Encoding.UTF8.GetBytes(title)));
    Add(100, "author");
    Add(101, "publisher");
    Add(103, "description");
    Add(104, "isbn");
    Add(105, "subject");
    records.Add((501, "EBOK"u8.ToArray()));

    var length = checked(12 + records.Sum(record => 8 + record.Data.Length));
    var result = new byte[(length + 3) & ~3];
    "EXTH"u8.CopyTo(result);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)length);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)records.Count);
    var offset = 12;
    foreach (var (type, data) in records) {
      BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset), type);
      BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset + 4), checked((uint)(8 + data.Length)));
      data.CopyTo(result, offset + 8);
      offset += 8 + data.Length;
    }
    return result;

    void Add(uint type, string key) {
      var value = options.GetString(key);
      if (!string.IsNullOrWhiteSpace(value))
        records.Add((type, Encoding.UTF8.GetBytes(value)));
    }
  }
}
