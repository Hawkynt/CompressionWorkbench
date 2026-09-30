#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Mobi;

/// <summary>
/// Amazon Mobipocket eBook (<c>.mobi</c> / <c>.prc</c> / <c>.azw</c>). The archive
/// view surfaces the original container, parsed metadata and cover, raw PalmDB
/// records, and decoded PalmDOC text when its compression type is supported.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://wiki.mobileread.com/wiki/MOBI</c> — MobileRead wiki — de-facto MOBI/EXTH format documentation</description></item>
///   <item><description><c>https://github.com/kovidgoyal/calibre</c> — Calibre — maintained implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Mobipocket</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class MobiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Mobi";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "MOBI / AZW (Amazon eBook)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".mobi";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".mobi", ".prc", ".azw", ".azw3"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // PalmDB "type + creator" at offset 60: "BOOK" + "MOBI" for Mobipocket.
    new("BOOKMOBI"u8.ToArray(), Offset: 60, Confidence: 0.95),
    // PalmDoc-style: "TEXt" + "REAd" also resolves here but less useful.
    new("TEXtREAd"u8.ToArray(), Offset: 60, Confidence: 0.6),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("palmdoc", "PalmDOC"), new("stored", "Stored")];
  /// <summary>Format-specific creation options.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("title", "Book title", FormatOptionKind.String, "", Description: "Title stored in the Palm database and EXTH metadata."),
    new("author", "Author", FormatOptionKind.String, "", Description: "EXTH author metadata."),
    new("publisher", "Publisher", FormatOptionKind.String, "", Description: "EXTH publisher metadata."),
    new("description", "Description", FormatOptionKind.String, "", Description: "EXTH description metadata."),
    new("isbn", "ISBN", FormatOptionKind.String, "", Description: "EXTH ISBN metadata."),
    new("subject", "Subject", FormatOptionKind.String, "", Description: "EXTH subject metadata."),
    new("language", "Language locale", FormatOptionKind.Integer, "1033", Description: "Palm/MOBI locale code; 1033 is English (United States)."),
  ];
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
  public string Description => "Amazon MOBI 7 eBook; creates UTF-8 HTML books with stored or PalmDOC text and preserves original containers, metadata, and raw records when reading.";

  /// <summary>Creates a simple MOBI 7 book from one HTML input.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite)
      throw new ArgumentException("Output stream must be writable.", nameof(output));
    if (options.Password != null || options.EncryptFilenames || options.EncryptionMethod != null)
      throw new NotSupportedException("This MOBI writer does not implement encryption.");
    if (inputs.Count != 1 || inputs[0].IsDirectory)
      throw new ArgumentException("MOBI creation requires exactly one HTML document input.", nameof(inputs));
    var input = inputs[0];
    var content = input.ReadContent();
    MobiWriter.Write(output, content, input.ArchiveName, options);
  }

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. Each entry's
  /// decoded byte buffer is produced by <see cref="BuildEntries"/> and
  /// wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    foreach (var e in BuildEntries(archive)) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(e.Data, writable: false), e.Data.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.mobi", "Container", blob),
    };

    if (blob.Length < 78) return entries;

    // PalmDB header: name[32], attributes[2], version[2], created[4], modified[4], backup[4],
    // modnum[4], appInfoOff[4], sortInfoOff[4], type[4], creator[4], uniqueIdSeed[4],
    // nextRecordList[4], numRecords[2].
    var dbName = Encoding.Latin1.GetString(blob.AsSpan(0, 32)).TrimEnd('\0');
    var numRecords = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(76));
    // Record-info list follows: numRecords × 8 bytes (4 offset + 1 attr + 3 uniqueId).
    var recordOffsets = new int[numRecords + 1];
    for (var i = 0; i < numRecords; ++i) {
      if (78 + i * 8 + 4 > blob.Length) break;
      var offset = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(78 + i * 8));
      if (offset > blob.Length) return entries;
      recordOffsets[i] = (int)offset;
    }
    recordOffsets[numRecords] = blob.Length;

    // Expose every payload record verbatim so record-level data and proprietary
    // extensions remain recoverable even when this reader cannot interpret them.
    for (var i = 0; i < numRecords; ++i) {
      var start = recordOffsets[i];
      var end = recordOffsets[i + 1];
      if (start < 0 || end < start || end > blob.Length) return entries;
      entries.Add(($"records/{i:D4}.bin", "Record", blob.AsSpan(start, end - start).ToArray()));
    }

    // Record 0 holds the PalmDOC header + MOBI header + EXTH.
    if (numRecords > 0) {
      var r0Off = recordOffsets[0];
      var r0End = recordOffsets[1];
      var r0 = blob.AsSpan(r0Off, Math.Max(0, r0End - r0Off));

      var ini = new StringBuilder();
      ini.AppendLine("; MOBI metadata");
      ini.Append("db_name=").AppendLine(dbName);
      ini.Append("records=").AppendLine(numRecords.ToString(System.Globalization.CultureInfo.InvariantCulture));

      // The MOBI header follows the 16-byte PalmDOC header. Field offsets below are from the start
      // of record 0, as the MobileRead MOBI page tabulates them.
      var hasMobiHeader = r0.Length >= 24 && r0[16..20].SequenceEqual("MOBI"u8);
      var mobiHeaderLen = hasMobiHeader ? BinaryPrimitives.ReadUInt32BigEndian(r0[20..]) : 0u;
      var mobiHeaderEnd = hasMobiHeader ? Math.Min((long)r0.Length, 16L + mobiHeaderLen) : 0L;
      bool HasField(int offset) => offset + 4 <= mobiHeaderEnd;

      // Extra record data flags (0xF0) only exist in headers long enough to hold them (228 or more).
      var extraDataFlags = HasField(ExtraDataFlagsOffset) && mobiHeaderLen >= 0xE4
        ? (ushort)BinaryPrimitives.ReadUInt32BigEndian(r0[ExtraDataFlagsOffset..])
        : (ushort)0;

      if (r0.Length >= 16) {
        var compression = BinaryPrimitives.ReadUInt16BigEndian(r0);
        var textLength = BinaryPrimitives.ReadUInt32BigEndian(r0[4..]);
        var textRecordCount = BinaryPrimitives.ReadUInt16BigEndian(r0[8..]);
        var encryption = BinaryPrimitives.ReadUInt16BigEndian(r0[12..]);
        ini.Append("compression=").AppendLine(compression.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ini.Append("text_length=").AppendLine(textLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ini.Append("text_records=").AppendLine(textRecordCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ini.Append("encryption=").AppendLine(encryption.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (extraDataFlags != 0)
          ini.Append("extra_data_flags=0x").AppendLine(extraDataFlags.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        if (encryption == 0 && textLength <= int.MaxValue && textRecordCount > 0 && textRecordCount < numRecords) {
          try {
            var textRecords = new byte[textRecordCount][];
            for (var i = 0; i < textRecordCount; ++i) {
              var start = recordOffsets[i + 1];
              var end = recordOffsets[i + 2];
              if (end < start || end > blob.Length) throw new InvalidDataException("Invalid PalmDB text record offsets.");
              textRecords[i] = blob.AsSpan(start, end - start).ToArray();
            }
            var text = PalmDocCodec.DecodeRecords(textRecords, compression, (int)textLength, extraDataFlags);
            entries.Add(("book.html", "BookText", text));
          } catch (NotSupportedException) {
            // Keep the raw records available for HUFF/CDIC or DRM-protected books.
          } catch (InvalidDataException) {
            // Malformed text streams do not invalidate access to intact raw records.
          }
        }
      }

      if (hasMobiHeader) {
        if (HasField(TextEncodingOffset)) {
          var textEncoding = BinaryPrimitives.ReadUInt32BigEndian(r0[TextEncodingOffset..]);
          ini.Append("text_encoding=").AppendLine(textEncoding switch {
            1252 => "Windows-1252",
            65001 => "UTF-8",
            _ => textEncoding.ToString(System.Globalization.CultureInfo.InvariantCulture),
          });
        }
        if (HasField(FullNameLengthOffset)) {
          var nameOffset = BinaryPrimitives.ReadUInt32BigEndian(r0[FullNameOffsetOffset..]);
          var nameLength = BinaryPrimitives.ReadUInt32BigEndian(r0[FullNameLengthOffset..]);
          if (nameOffset <= (uint)r0.Length && nameLength <= (uint)r0.Length - nameOffset)
            AppendString(ini, "full_name", r0.Slice((int)nameOffset, (int)nameLength));
        }
        if (HasField(LocaleOffset))
          ini.Append("language_locale=").AppendLine(BinaryPrimitives.ReadUInt32BigEndian(r0[LocaleOffset..]).ToString(System.Globalization.CultureInfo.InvariantCulture));

        // An EXTH block follows the MOBI header when bit 6 of the EXTH flags is set.
        if (HasField(ExthFlagsOffset) && (BinaryPrimitives.ReadUInt32BigEndian(r0[ExthFlagsOffset..]) & 0x40) != 0) {
          var exthStart = 16L + mobiHeaderLen;
          if (exthStart + 12 <= r0.Length) {
            var exthIndex = (int)exthStart;
            if (r0[exthIndex..(exthIndex + 4)].SequenceEqual("EXTH"u8))
              ParseExth(r0[exthIndex..], ini, entries, blob, recordOffsets);
          }
        }
      }
      entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));
    }

    return entries;
  }

  // MOBI header fields, as offsets from the start of record 0 (MobileRead "MOBI Header" table).
  private const int TextEncodingOffset = 0x1C;
  private const int FullNameOffsetOffset = 0x54;
  private const int FullNameLengthOffset = 0x58;
  private const int LocaleOffset = 0x5C;
  private const int ExthFlagsOffset = 0x80;
  private const int ExtraDataFlagsOffset = 0xF0;

  // EXTH: "EXTH" + header-len (4 BE) + record-count (4 BE) + N × (type:4 BE + length:4 BE + data).
  private static void ParseExth(ReadOnlySpan<byte> exth, StringBuilder ini,
      List<(string, string, byte[])> entries, byte[] fullBlob, int[] recordOffsets) {
    if (exth.Length < 12) return;
    var recordCount = BinaryPrimitives.ReadInt32BigEndian(exth[8..]);
    var pos = 12;
    int? coverRecordIdx = null;

    for (var i = 0; i < recordCount && pos + 8 <= exth.Length; ++i) {
      var type = BinaryPrimitives.ReadInt32BigEndian(exth[pos..]);
      var len = BinaryPrimitives.ReadInt32BigEndian(exth[(pos + 4)..]);
      if (len < 8 || len > exth.Length - pos) break;
      var data = exth.Slice(pos + 8, len - 8);

      switch (type) {
        case 100: AppendString(ini, "author", data); break;
        case 101: AppendString(ini, "publisher", data); break;
        case 103: AppendString(ini, "description", data); break;
        case 104: AppendString(ini, "isbn", data); break;
        case 105: AppendString(ini, "subject", data); break;
        case 106: AppendString(ini, "publishing_date", data); break;
        case 108: AppendString(ini, "contributor", data); break;
        case 109: AppendString(ini, "rights", data); break;
        case 201 when data.Length == 4: coverRecordIdx = BinaryPrimitives.ReadInt32BigEndian(data); break;
        case 503: AppendString(ini, "title", data); break;
      }
      pos += len;
    }

    // Cover image is stored in a separate PalmDB record (index relative to first image record).
    // The "first image record" index is in the MOBI header at offset 108 from the MOBI magic —
    // we approximate by walking records looking for known image magic bytes.
    if (coverRecordIdx.HasValue) {
      var imageIdx = coverRecordIdx.Value;
      for (var r = 1; r < recordOffsets.Length - 1; ++r) {
        var off = recordOffsets[r];
        var end = recordOffsets[r + 1];
        if (off >= fullBlob.Length || end <= off) continue;
        var body = fullBlob.AsSpan(off, end - off);
        if (IsJpegOrPng(body)) {
          if (imageIdx-- == 0) {
            var ext = body[0] == 0xFF ? ".jpg" : ".png";
            entries.Add(($"cover{ext}", "Tag", body.ToArray()));
            break;
          }
        }
      }
    }
  }

  private static bool IsJpegOrPng(ReadOnlySpan<byte> body)
    => (body.Length >= 3 && body[0] == 0xFF && body[1] == 0xD8 && body[2] == 0xFF) ||
       (body.Length >= 4 && body[0] == 0x89 && body[1] == 0x50 && body[2] == 0x4E && body[3] == 0x47);

  private static void AppendString(StringBuilder ini, string key, ReadOnlySpan<byte> data)
    => ini.Append(key).Append('=').AppendLine(Encoding.UTF8.GetString(data).Trim('\0').Trim());
}
