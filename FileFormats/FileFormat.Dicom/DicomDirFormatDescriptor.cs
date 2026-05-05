#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dicom;

/// <summary>
/// DICOMDIR (DICOM Part 10 chapter 8, "Media Storage Directory") — a DICOM
/// file whose payload is a directory index referencing sibling DICOM files on
/// the same medium. Surfaced as a pseudo-archive: one entry per referenced
/// sibling (resolved relative to the DICOMDIR's own file location), plus a
/// <c>metadata.ini</c> summary of the patient / study / series hierarchy.
/// Detection: DICM preamble at offset 128 plus presence of tag (0004,1220)
/// DirectoryRecordSequence. Filename on media is usually "DICOMDIR" with no
/// extension, occasionally <c>.dcmdir</c>.
/// </summary>
/// <remarks>
/// Reference: https://dicom.nema.org/medical/dicom/current/output/chtml/part10/chapter_8.html
/// </remarks>
public sealed class DicomDirFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>Format identifier.</summary>
  public string Id => "DicomDir";
  /// <summary>Display name.</summary>
  public string DisplayName => "DICOMDIR (multi-study index)";
  /// <summary>Archive category — surfaces referenced sibling DICOM files.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>List + extract, multi-entry.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>Default extension (uncommon — usually the file is simply named DICOMDIR).</summary>
  public string DefaultExtension => ".dcmdir";
  /// <summary>Known extensions. The canonical filename is "DICOMDIR" without extension,
  /// so we leave this list intentionally narrow and rely on magic + content sniffing.</summary>
  public IReadOnlyList<string> Extensions => [".dcmdir"];
  /// <summary>No compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>DICM at offset 128 (same as DICOM). Detection further refined by List() parsing.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Lower confidence than DicomFormatDescriptor (0.98) so plain DICOM wins the race for .dcm files.
    new("DICM"u8.ToArray(), Offset: 128, Confidence: 0.80),
  ];
  /// <summary>Stored only.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>Not a tar compound format.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Archive family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>Short description.</summary>
  public string Description =>
    "DICOMDIR (DICOM Media Storage Directory). Surfaces referenced sibling files + hierarchy metadata.";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var basePath = (stream as FileStream)?.Name;
    return this.BuildEntries(stream, basePath).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var basePath = (stream as FileStream)?.Name;
    foreach (var e in this.BuildEntries(stream, basePath)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <inheritdoc />
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    var basePath = (input as FileStream)?.Name;
    foreach (var e in this.BuildEntries(input, basePath)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  /// <summary>
  /// Parses the DICOMDIR, extracts all (0004,1500) ReferencedFileID elements, and resolves each
  /// against the DICOMDIR's own folder on disk. If <paramref name="basePath"/> is null
  /// (non-filestream source), referenced file bytes are empty placeholders and metadata-only.
  /// </summary>
  private IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream, string? basePath) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.dcmdir", "Track", blob),
    };

    var parse = DicomDirParser.Parse(blob);
    var baseDir = basePath != null ? Path.GetDirectoryName(Path.GetFullPath(basePath)) : null;

    // One entry per referenced file.
    foreach (var rec in parse.Records) {
      if (rec.ReferencedFileId == null) continue;
      var relPath = string.Join('/', rec.ReferencedFileId);
      byte[] data;
      if (baseDir != null) {
        // DICOM spec says ReferencedFileID is a sequence of Component Group names; on disk those
        // translate to path segments joined by the OS separator. Try OS separator first, fall
        // back to forward slash.
        var candidate = Path.Combine([baseDir, .. rec.ReferencedFileId]);
        data = File.Exists(candidate) ? File.ReadAllBytes(candidate) : [];
      } else {
        data = [];
      }
      entries.Add((relPath, "Track", data));
    }

    var meta = new StringBuilder();
    meta.AppendLine("; DICOMDIR summary");
    meta.Append("patients=").AppendLine(parse.Records.Count(r => string.Equals(r.RecordType, "PATIENT", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture));
    meta.Append("studies=").AppendLine(parse.Records.Count(r => string.Equals(r.RecordType, "STUDY", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture));
    meta.Append("series=").AppendLine(parse.Records.Count(r => string.Equals(r.RecordType, "SERIES", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture));
    var imageRefs = parse.Records.Count(r => r.ReferencedFileId != null);
    meta.Append("referenced_files=").AppendLine(imageRefs.ToString(CultureInfo.InvariantCulture));
    meta.Append("file_set_id=").AppendLine(parse.FileSetId ?? "");
    meta.Append("has_directory_record_sequence=").AppendLine(parse.HasDirectoryRecordSequence ? "true" : "false");
    meta.AppendLine();
    meta.AppendLine("; Record hierarchy");
    foreach (var r in parse.Records) {
      meta.Append("record=").Append(r.RecordType ?? "?");
      if (r.ReferencedFileId != null)
        meta.Append(" file=").Append(string.Join('/', r.ReferencedFileId));
      if (!string.IsNullOrEmpty(r.Label)) meta.Append(" label=").Append(r.Label);
      meta.AppendLine();
    }
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    return entries;
  }
}

/// <summary>
/// Minimal DICOMDIR parser. Walks explicit-VR Little Endian elements starting after the
/// 128-byte preamble + "DICM" marker, looks for the DirectoryRecordSequence tag (0004,1220),
/// and returns its flat record list. Does not decode private tags, overlays, or pixel data.
/// </summary>
internal static class DicomDirParser {
  public sealed record Record(string? RecordType, IReadOnlyList<string>? ReferencedFileId, string? Label);
  public sealed record Result(bool HasDirectoryRecordSequence, string? FileSetId, IReadOnlyList<Record> Records);

  private static readonly HashSet<string> LongFormVrs = new(StringComparer.Ordinal) {
    "OB", "OW", "OF", "OD", "OL", "SQ", "UN", "UT", "UC", "UR",
  };

  public static Result Parse(byte[] blob) {
    var hasPreamble = blob.Length >= 132
      && blob[128] == 'D' && blob[129] == 'I' && blob[130] == 'C' && blob[131] == 'M';
    if (!hasPreamble) return new Result(false, null, []);

    // File meta group (0002) is always Explicit VR LE; body transfer syntax defaults to
    // Explicit VR LE for DICOMDIR per Part 10 §8.
    string? fileSetId = null;
    var pos = 132L;
    while (pos < blob.Length) {
      var saved = pos;
      if (!TryReadHeader(blob, ref pos, explicitVr: true, out var group, out var elementId, out var _, out var length, out var undef)) break;
      if (group != 0x0002) { pos = saved; break; }
      if (!undef && length > 0 && pos + length <= blob.Length) pos += length;
    }

    var records = new List<Record>();
    var hasDirectoryRecordSequence = false;

    // Body loop — Explicit VR LE.
    while (pos < blob.Length) {
      var saved = pos;
      if (!TryReadHeader(blob, ref pos, explicitVr: true, out var group, out var elementId, out var vr, out var length, out var undef)) break;

      // FileSetID (0004,1130)
      if (group == 0x0004 && elementId == 0x1130) {
        var bytes = ReadValue(blob, ref pos, length, undef);
        fileSetId = Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
        continue;
      }

      // DirectoryRecordSequence (0004,1220)
      if (group == 0x0004 && elementId == 0x1220) {
        hasDirectoryRecordSequence = true;
        ReadDirectoryRecordSequence(blob, ref pos, length, undef, records);
        continue;
      }

      // Unknown element — skip.
      if (!undef && length > 0 && pos + length <= blob.Length) pos += length;
      else if (undef) {
        // Skip over sequence with undefined length.
        pos = FindSequenceDelimiter(blob, pos);
      } else break;
    }

    return new Result(hasDirectoryRecordSequence, fileSetId, records);
  }

  /// <summary>Returns true if blob is a valid DICOM file containing (0004,1220).</summary>
  public static bool HasDirectoryRecordSequence(byte[] blob) => Parse(blob).HasDirectoryRecordSequence;

  private static void ReadDirectoryRecordSequence(byte[] blob, ref long pos, long length, bool undef, List<Record> records) {
    var endPos = undef ? long.MaxValue : Math.Min(blob.Length, pos + length);
    while (pos + 8 <= blob.Length && pos < endPos) {
      // Item header (FFFE,E000) + length, or sequence-delim (FFFE,E0DD)
      var g = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos));
      var e = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos + 2));
      var itemLen = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos + 4));
      pos += 8;
      if (g == 0xFFFE && e == 0xE0DD) return; // sequence delimiter
      if (g != 0xFFFE || e != 0xE000) return; // unexpected

      var itemEnd = itemLen == 0xFFFFFFFFu ? endPos : Math.Min(endPos, pos + itemLen);
      string? recordType = null;
      List<string>? referencedFileId = null;
      string? label = null;

      var itemConsumed = false;
      while (pos + 8 <= blob.Length && pos < itemEnd) {
        var saved = pos;
        if (!TryReadHeader(blob, ref pos, explicitVr: true, out var ig, out var ie, out var ivr, out var ilen, out var iundef)) return;
        if (ig == 0xFFFE && ie == 0xE00D) { itemConsumed = true; break; } // item delimiter ends this item

        if (ig == 0x0004 && ie == 0x1430) {
          // DirectoryRecordType (CS)
          var bytes = ReadValue(blob, ref pos, ilen, iundef);
          recordType = Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
        } else if (ig == 0x0004 && ie == 0x1500) {
          // ReferencedFileID (CS, VM 1-8): backslash-separated components.
          var bytes = ReadValue(blob, ref pos, ilen, iundef);
          var s = Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
          referencedFileId = s.Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        } else if ((ig == 0x0010 && ie == 0x0010) // PatientName
                || (ig == 0x0008 && ie == 0x103E) // SeriesDescription
                || (ig == 0x0008 && ie == 0x1030) // StudyDescription
                || (ig == 0x0020 && ie == 0x0010)) { // StudyID
          var bytes = ReadValue(blob, ref pos, ilen, iundef);
          var s = Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
          if (!string.IsNullOrWhiteSpace(s)) label = label == null ? s : label + "|" + s;
        } else if (iundef) {
          pos = FindSequenceDelimiter(blob, pos);
        } else if (ilen > 0 && pos + ilen <= blob.Length) {
          pos += ilen;
        } else {
          return;
        }
      }

      records.Add(new Record(recordType, referencedFileId, label));
      if (itemLen == 0xFFFFFFFFu) {
        // For undefined-length items we already consumed up through the item delimiter
        // (or the enclosing sequence end) — continue to the next item.
        _ = itemConsumed;
        continue;
      }
      pos = itemEnd;
    }
  }

  private static byte[] ReadValue(byte[] blob, ref long pos, long length, bool undef) {
    if (undef || length <= 0) return [];
    var end = Math.Min(blob.Length, pos + length);
    var take = (int)(end - pos);
    if (take <= 0) return [];
    var result = new byte[take];
    Array.Copy(blob, (int)pos, result, 0, take);
    pos += take;
    return result;
  }

  private static long FindSequenceDelimiter(byte[] blob, long startPos) {
    var pos = startPos;
    while (pos + 8 <= blob.Length) {
      var g = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos));
      var e = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos + 2));
      var len = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos + 4));
      pos += 8;
      if (g == 0xFFFE && e == 0xE0DD) return pos;
      if (len != 0xFFFFFFFFu && pos + len <= blob.Length) pos += len;
      else break;
    }
    return blob.Length;
  }

  private static bool TryReadHeader(byte[] blob, ref long pos, bool explicitVr,
      out ushort group, out ushort elementId, out string vr, out long length, out bool undef) {
    group = 0; elementId = 0; vr = ""; length = 0; undef = false;
    if (pos + 8 > blob.Length) return false;
    group = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos));
    elementId = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos + 2));
    pos += 4;

    // Items & delimiters are implicit 4-byte length regardless of VR mode.
    if (group == 0xFFFE) {
      if (pos + 4 > blob.Length) return false;
      var ilen = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos));
      pos += 4;
      vr = "  ";
      length = ilen == 0xFFFFFFFFu ? 0 : ilen;
      undef = ilen == 0xFFFFFFFFu;
      return true;
    }

    if (explicitVr) {
      if (pos + 2 > blob.Length) return false;
      vr = Encoding.ASCII.GetString(blob, (int)pos, 2);
      pos += 2;
      if (LongFormVrs.Contains(vr)) {
        if (pos + 6 > blob.Length) return false;
        pos += 2; // reserved
        var lr = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos));
        pos += 4;
        length = lr == 0xFFFFFFFFu ? 0 : lr;
        undef = lr == 0xFFFFFFFFu;
        return true;
      }
      if (pos + 2 > blob.Length) return false;
      length = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos));
      pos += 2;
      undef = false;
      return true;
    }
    if (pos + 4 > blob.Length) return false;
    var lr2 = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos));
    pos += 4;
    length = lr2 == 0xFFFFFFFFu ? 0 : lr2;
    undef = lr2 == 0xFFFFFFFFu;
    vr = "UN";
    return true;
  }
}
