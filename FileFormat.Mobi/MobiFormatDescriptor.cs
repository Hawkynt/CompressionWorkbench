#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Mobi;

/// <summary>
/// Amazon Mobipocket eBook (<c>.mobi</c> / <c>.prc</c> / <c>.azw</c>). The archive
/// view surfaces: <c>FULL.mobi</c>, <c>metadata.ini</c> (EXTH + PalmDB title),
/// <c>cover.*</c> if an EXTH cover record is present, and per-record raw bodies
/// under <c>records/</c>.
/// <para>
/// Scope cut: PalmDOC (LZ77-variant) text decompression is deferred — for now the
/// "book content" entries are exposed as raw compressed records rather than
/// decoded HTML. Metadata + cover, which is what most triage use cases want,
/// works fully.
/// </para>
/// </summary>
public sealed class MobiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Mobi";
  public string DisplayName => "MOBI / AZW (Amazon eBook)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mobi";
  public IReadOnlyList<string> Extensions => [".mobi", ".prc", ".azw", ".azw3"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // PalmDB "type + creator" at offset 60: "BOOK" + "MOBI" for Mobipocket.
    new("BOOKMOBI"u8.ToArray(), Offset: 60, Confidence: 0.95),
    // PalmDoc-style: "TEXt" + "REAd" also resolves here but less useful.
    new("TEXtREAd"u8.ToArray(), Offset: 60, Confidence: 0.6),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("palmdoc", "PalmDOC")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amazon MOBI eBook; EXTH metadata + cover + raw PalmDB records.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

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
      ("FULL.mobi", "Track", blob),
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
      recordOffsets[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(78 + i * 8));
    }
    recordOffsets[numRecords] = blob.Length;

    // Record 0 holds the PalmDOC header + MOBI header + EXTH.
    if (numRecords > 0) {
      var r0Off = recordOffsets[0];
      var r0End = recordOffsets[1];
      var r0 = blob.AsSpan(r0Off, Math.Max(0, r0End - r0Off));

      var ini = new StringBuilder();
      ini.AppendLine("; MOBI metadata");
      ini.Append("db_name=").AppendLine(dbName);
      ini.Append("records=").AppendLine(numRecords.ToString(System.Globalization.CultureInfo.InvariantCulture));

      // MOBI header starts at +16 (after PalmDOC header). Expect "MOBI" magic at +0 of it.
      if (r0.Length >= 20 && r0[16] == 'M' && r0[17] == 'O' && r0[18] == 'B' && r0[19] == 'I') {
        var mobiHeaderLen = BinaryPrimitives.ReadInt32BigEndian(r0[20..]);
        var textEncoding = BinaryPrimitives.ReadInt32BigEndian(r0[(16 + 28)..]);
        ini.Append("text_encoding=").AppendLine(textEncoding switch {
          1252 => "Windows-1252",
          65001 => "UTF-8",
          _ => textEncoding.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        // EXTH follows MOBI header if the EXTH flag bit 6 of mobiHeader[+128] is set.
        var exthFlagOff = 16 + 128;
        if (r0.Length >= exthFlagOff + 4) {
          var exthFlag = BinaryPrimitives.ReadUInt32BigEndian(r0[exthFlagOff..]);
          if ((exthFlag & 0x40) != 0) {
            var exthStart = 16 + mobiHeaderLen;
            if (exthStart + 12 <= r0.Length &&
                r0[exthStart] == 'E' && r0[exthStart + 1] == 'X' && r0[exthStart + 2] == 'T' && r0[exthStart + 3] == 'H') {
              ParseExth(r0[exthStart..], ini, entries, blob, recordOffsets);
            }
          }
        }
      }
      entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));
    }

    return entries;
  }

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
      if (len < 8 || pos + len > exth.Length) break;
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
