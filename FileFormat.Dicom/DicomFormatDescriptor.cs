#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dicom;

/// <summary>
/// Medical DICOM (Part 10) file surfaced as a read-only archive. Walks the data
/// elements (Explicit VR for the file meta, then either Explicit or Implicit VR
/// Little Endian for the body depending on the transfer syntax) and emits the
/// full file, a metadata summary, a per-element tag dump, the raw PixelData
/// payload (or encapsulated fragments), and any overlay data. Does not decode
/// JPEG-compressed pixel data — fragments are surfaced verbatim.
/// </summary>
public sealed class DicomFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Dicom";
  public string DisplayName => "DICOM";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dcm";
  public IReadOnlyList<string> Extensions => [".dcm", ".dicom"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // DICM magic at offset 128 (after 128-byte preamble)
    new("DICM"u8.ToArray(), Offset: 128, Confidence: 0.98),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "DICOM medical imaging file; surfaces metadata, tag dump, and (possibly encapsulated) pixel data. " +
    "Compressed pixel data is not decoded.";

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

  // VRs that use the long form: 2-byte VR + 2-byte reserved + 4-byte length.
  private static readonly HashSet<string> LongFormVrs = new(StringComparer.Ordinal) {
    "OB", "OW", "OF", "OD", "OL", "SQ", "UN", "UT", "UC", "UR",
  };

  // VRs whose payload is a text-ish string we can preview.
  private static readonly HashSet<string> TextVrs = new(StringComparer.Ordinal) {
    "AE", "AS", "CS", "DA", "DS", "DT", "IS", "LO", "LT", "PN",
    "SH", "ST", "TM", "UI", "UC", "UR", "UT",
  };

  private sealed record Element(ushort Group, ushort ElementId, string Vr, long Length, byte[] Value, long Offset);

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.dcm", "Track", blob),
    };

    var hasPreamble = blob.Length >= 132
      && blob[128] == 'D' && blob[129] == 'I' && blob[130] == 'C' && blob[131] == 'M';

    var transferSyntax = "1.2.840.10008.1.2"; // Implicit VR Little Endian (default).
    string? modality = null;
    string? patientName = null;
    ushort rows = 0, columns = 0, bitsAllocated = 0;

    var tags = new StringBuilder();
    tags.AppendLine("# (Group,Element)\tVR\tLength\tPreview");

    var elements = new List<Element>();
    var pos = hasPreamble ? 132L : 0L;

    // File meta (Group 0x0002) always Explicit VR LE.
    var bodyExplicitVr = false;
    while (pos < blob.Length) {
      if (!TryReadElement(blob, ref pos, explicitVr: true, out var el)) break;
      if (el.Group != 0x0002) {
        // Reverted — reparse this element under the body transfer syntax.
        pos = el.Offset;
        bodyExplicitVr = transferSyntax is "1.2.840.10008.1.2.1" or "1.2.840.10008.1.2.2"
          || transferSyntax.StartsWith("1.2.840.10008.1.2.4", StringComparison.Ordinal) // JPEG family
          || transferSyntax.StartsWith("1.2.840.10008.1.2.5", StringComparison.Ordinal);
        break;
      }
      elements.Add(el);
      AppendTagLine(tags, el);
      if (el.Group == 0x0002 && el.ElementId == 0x0010) // Transfer Syntax UID
        transferSyntax = Encoding.ASCII.GetString(el.Value).Trim('\0', ' ');
    }

    // Body elements.
    while (pos < blob.Length) {
      var savedPos = pos;
      if (!TryReadElement(blob, ref pos, bodyExplicitVr, out var el)) break;
      elements.Add(el);
      AppendTagLine(tags, el);

      if (el.Group == 0x0008 && el.ElementId == 0x0060)
        modality = Encoding.ASCII.GetString(el.Value).Trim('\0', ' ');
      else if (el.Group == 0x0010 && el.ElementId == 0x0010)
        patientName = Encoding.ASCII.GetString(el.Value).Trim('\0', ' ');
      else if (el.Group == 0x0028 && el.ElementId == 0x0010 && el.Value.Length >= 2)
        rows = BinaryPrimitives.ReadUInt16LittleEndian(el.Value);
      else if (el.Group == 0x0028 && el.ElementId == 0x0011 && el.Value.Length >= 2)
        columns = BinaryPrimitives.ReadUInt16LittleEndian(el.Value);
      else if (el.Group == 0x0028 && el.ElementId == 0x0100 && el.Value.Length >= 2)
        bitsAllocated = BinaryPrimitives.ReadUInt16LittleEndian(el.Value);
      else if (el.Group == 0x7FE0 && el.ElementId == 0x0010) {
        if (el.Length == -1) {
          // Encapsulated pixel data: stream of (FFFE,E000) item fragments ending with (FFFE,E0DD).
          ExtractEncapsulatedFragments(blob, savedPos, el.Offset, entries);
          // Advance pos to after the sequence end delimiter.
          pos = FindSequenceEnd(blob, el.Offset);
        } else {
          entries.Add(("pixel_data/pixel_data.bin", "Track", el.Value));
        }
      } else if (el.Group >= 0x6000 && el.Group <= 0x60FF && (el.Group & 1) == 0 && el.ElementId == 0x3000) {
        entries.Add(($"overlay_data/overlay_{el.Group:X4}.bin", "Track", el.Value));
      }
    }

    var meta = new StringBuilder();
    meta.AppendLine("; DICOM file metadata");
    meta.Append("transfer_syntax=").AppendLine(transferSyntax);
    meta.Append("has_preamble=").AppendLine(hasPreamble ? "true" : "false");
    if (modality != null) meta.Append("modality=").AppendLine(modality);
    if (patientName != null) meta.Append("patient_name=").AppendLine(patientName);
    meta.Append("rows=").AppendLine(rows.ToString(CultureInfo.InvariantCulture));
    meta.Append("columns=").AppendLine(columns.ToString(CultureInfo.InvariantCulture));
    meta.Append("bits_allocated=").AppendLine(bitsAllocated.ToString(CultureInfo.InvariantCulture));
    meta.Append("element_count=").AppendLine(elements.Count.ToString(CultureInfo.InvariantCulture));
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));
    entries.Insert(2, ("tags.txt", "Tag", Encoding.UTF8.GetBytes(tags.ToString())));

    return entries;
  }

  private static bool TryReadElement(byte[] blob, ref long pos, bool explicitVr, out Element el) {
    el = null!;
    if (pos + 8 > blob.Length) return false;
    var start = pos;
    var group = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos));
    var elementId = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos + 2));
    pos += 4;

    // Item / item-delimiter / sequence-delimiter markers always have implicit 4-byte length.
    if (group == 0xFFFE) {
      if (pos + 4 > blob.Length) return false;
      var ilen = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos));
      pos += 4;
      var ibody = Array.Empty<byte>();
      if (ilen != 0xFFFFFFFFu && pos + ilen <= blob.Length) {
        ibody = new byte[ilen];
        Array.Copy(blob, (int)pos, ibody, 0, (int)ilen);
        pos += ilen;
      }
      el = new Element(group, elementId, "  ", ilen == 0xFFFFFFFFu ? -1 : ilen, ibody, start);
      return true;
    }

    string vr;
    long length;
    if (explicitVr) {
      if (pos + 2 > blob.Length) return false;
      vr = Encoding.ASCII.GetString(blob, (int)pos, 2);
      pos += 2;
      if (LongFormVrs.Contains(vr)) {
        if (pos + 6 > blob.Length) return false;
        pos += 2; // reserved
        var lr = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos));
        pos += 4;
        length = lr == 0xFFFFFFFFu ? -1 : lr;
      } else {
        if (pos + 2 > blob.Length) return false;
        length = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos));
        pos += 2;
      }
    } else {
      if (pos + 4 > blob.Length) return false;
      vr = GuessImplicitVr(group, elementId);
      var lr = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos));
      pos += 4;
      length = lr == 0xFFFFFFFFu ? -1 : lr;
    }

    var value = Array.Empty<byte>();
    if (length > 0 && pos + length <= blob.Length) {
      value = new byte[length];
      Array.Copy(blob, (int)pos, value, 0, (int)length);
      pos += length;
    } else if (length < 0) {
      // Undefined length — caller handles (sequence / encapsulated pixel data).
    }
    el = new Element(group, elementId, vr, length, value, start);
    return true;
  }

  private static string GuessImplicitVr(ushort group, ushort element) {
    // Minimal whitelist for the tags we care about in metadata.
    if (group == 0x7FE0 && element == 0x0010) return "OW";
    if (group >= 0x6000 && group <= 0x60FF && element == 0x3000) return "OW";
    if (group == 0x0028 && (element == 0x0010 || element == 0x0011 || element == 0x0100)) return "US";
    if (group == 0x0008 && element == 0x0060) return "CS";
    if (group == 0x0010 && element == 0x0010) return "PN";
    return "UN";
  }

  private static void AppendTagLine(StringBuilder sb, Element el) {
    sb.Append('(').Append(el.Group.ToString("X4", CultureInfo.InvariantCulture))
      .Append(',').Append(el.ElementId.ToString("X4", CultureInfo.InvariantCulture)).Append(')').Append('\t');
    sb.Append(el.Vr).Append('\t');
    sb.Append(el.Length == -1 ? "UNDEF" : el.Length.ToString(CultureInfo.InvariantCulture)).Append('\t');
    if (el.Length == -1 || el.Value.Length == 0) {
      sb.AppendLine("<seq>");
    } else if (TextVrs.Contains(el.Vr) && el.Value.Length <= 128) {
      var text = Encoding.ASCII.GetString(el.Value).Trim('\0', ' ');
      // Replace control chars.
      var clean = new StringBuilder(text.Length);
      foreach (var c in text) clean.Append(c < 0x20 ? '.' : c);
      sb.AppendLine(clean.ToString());
    } else {
      sb.AppendLine("<bin>");
    }
  }

  private static void ExtractEncapsulatedFragments(byte[] blob, long elementStart, long valueStart, List<(string Name, string Kind, byte[] Data)> entries) {
    // valueStart is where the element value begins. After a PixelData header with undefined length,
    // this is followed by (FFFE,E000) item fragments ending at (FFFE,E0DD).
    var pos = valueStart;
    var idx = 0;
    while (pos + 8 <= blob.Length) {
      var group = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos));
      var element = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos + 2));
      var len = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos + 4));
      pos += 8;
      if (group != 0xFFFE) break;
      if (element == 0xE0DD) break; // sequence end
      if (element != 0xE000) break; // unexpected
      if (len == 0xFFFFFFFFu) break;
      if (pos + len > blob.Length) break;
      if (len > 0) {
        var frag = new byte[len];
        Array.Copy(blob, (int)pos, frag, 0, (int)len);
        entries.Add(($"pixel_data/encapsulated_{idx:D2}.bin", "Track", frag));
      }
      pos += len;
      idx++;
    }
  }

  private static long FindSequenceEnd(byte[] blob, long startPos) {
    var pos = startPos;
    while (pos + 8 <= blob.Length) {
      var group = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos));
      var element = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)pos + 2));
      var len = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos + 4));
      pos += 8;
      if (group == 0xFFFE && element == 0xE0DD) return pos;
      if (len != 0xFFFFFFFFu && pos + len <= blob.Length) pos += len;
      else break;
    }
    return blob.Length;
  }
}
