using System.Buffers.Binary;
using System.Text;

namespace FileFormat.InstallerVise;

/// <summary>Record layouts of the VISE catalog, chosen by revision and storage (not VISE version numbers).</summary>
internal enum ViseLayout { Lite, Short, Compact, Normal, Wide, Late }

/// <summary>One catalog record: a directory (DVCT), a file or installer action (FVCT), or a header.</summary>
internal sealed class ViseRecord {
  public long Off, End;
  public string Tag = "";
  public string? Name;
  public string Path = "";
  public int OutputGroup = -1;
  public bool File, HasForkOffsets, BaseDependent, External;
  public uint Parent, DirId, Payload, Checksum, Created, Modified, RecordFlags, PayloadMode;
  public ushort Depth, FixedSize, Segment;
  public byte Subtype;
  public readonly uint[] Packed = new uint[2], Expanded = new uint[2], ForkOffset = new uint[2];
  public readonly byte[] FinderInfo = new byte[16];

  public bool IsDirectory => this.Tag == "DVCT";
  public bool IsFileRecord => this.Tag == "FVCT" && this.File;
  public bool OutputsFork => this.IsFileRecord && (this.Expanded[0] != 0 || this.Expanded[1] != 0) && (this.Packed[0] != 0 || this.Packed[1] != 0);
}

/// <summary>
/// Parses a VISE catalog (CVCT, then DVCT/FVCT records, then PACK): record boundaries come from
/// revision-specific fixed bodies and declared variable fields, each checked against the next
/// signature; names are MacRoman; the hierarchy comes from directory IDs, Lite preorder, or the
/// late layout's explicit depths; colliding alternatives get a <c>~NNNN</c> record suffix.
/// </summary>
/// <remarks>Converted from unvise's <c>catalog.c</c> (MIT); see THIRD-PARTY-NOTICE.unvise.txt.</remarks>
internal static class ViseCatalog {
  private static uint Be32(byte[] b, long off) {
    if (off < 0 || off + 4 > b.Length)
      throw new InvalidDataException("VISE: truncated 32-bit field.");
    return BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)off));
  }

  private static ushort Be16(byte[] b, long off) {
    if (off < 0 || off + 2 > b.Length)
      throw new InvalidDataException("VISE: truncated 16-bit field.");
    return BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan((int)off));
  }

  private static byte Byte(byte[] b, long off) {
    if (off < 0 || off >= b.Length)
      throw new InvalidDataException("VISE: truncated catalog field.");
    return b[off];
  }

  private static bool Span(byte[] b, long off, long n) => off >= 0 && off <= b.Length && n <= b.Length - off;

  private static bool TagAt(byte[] b, long off, string tag)
    => Span(b, off, 4) && b[off] == tag[0] && b[off + 1] == tag[1] && b[off + 2] == tag[2] && b[off + 3] == tag[3];

  private static readonly ushort[] MacRoman = [
    0x00C4, 0x00C5, 0x00C7, 0x00C9, 0x00D1, 0x00D6, 0x00DC, 0x00E1,
    0x00E0, 0x00E2, 0x00E4, 0x00E3, 0x00E5, 0x00E7, 0x00E9, 0x00E8,
    0x00EA, 0x00EB, 0x00ED, 0x00EC, 0x00EE, 0x00EF, 0x00F1, 0x00F3,
    0x00F2, 0x00F4, 0x00F6, 0x00F5, 0x00FA, 0x00F9, 0x00FB, 0x00FC,
    0x2020, 0x00B0, 0x00A2, 0x00A3, 0x00A7, 0x2022, 0x00B6, 0x00DF,
    0x00AE, 0x00A9, 0x2122, 0x00B4, 0x00A8, 0x2260, 0x00C6, 0x00D8,
    0x221E, 0x00B1, 0x2264, 0x2265, 0x00A5, 0x00B5, 0x2202, 0x2211,
    0x220F, 0x03C0, 0x222B, 0x00AA, 0x00BA, 0x2126, 0x00E6, 0x00F8,
    0x00BF, 0x00A1, 0x00AC, 0x221A, 0x0192, 0x2248, 0x2206, 0x00AB,
    0x00BB, 0x2026, 0x00A0, 0x00C0, 0x00C3, 0x00D5, 0x0152, 0x0153,
    0x2013, 0x2014, 0x201C, 0x201D, 0x2018, 0x2019, 0x00F7, 0x25CA,
    0x00FF, 0x0178, 0x2044, 0x00A4, 0x2039, 0x203A, 0xFB01, 0xFB02,
    0x2021, 0x00B7, 0x201A, 0x201E, 0x2030, 0x00C2, 0x00CA, 0x00C1,
    0x00CB, 0x00C8, 0x00CD, 0x00CE, 0x00CF, 0x00CC, 0x00D3, 0x00D4,
    0xF8FF, 0x00D2, 0x00DA, 0x00DB, 0x00D9, 0x0131, 0x02C6, 0x02DC,
    0x00AF, 0x02D8, 0x02D9, 0x02DA, 0x00B8, 0x02DD, 0x02DB, 0x02C7,
  ];

  /// <summary>A MacRoman byte string as Unicode.</summary>
  public static string FromMacRoman(ReadOnlySpan<byte> p) {
    var sb = new StringBuilder(p.Length);
    foreach (var c in p)
      sb.Append(c < 128 ? (char)c : (char)MacRoman[c - 128]);
    return sb.ToString();
  }

  public static ViseLayout UncompressedLayout(byte generation, byte revision) {
    if (revision == 0)
      return ViseLayout.Lite;
    if (generation < 3 && revision is >= 1 and <= 2)
      return ViseLayout.Short;
    if (revision is >= 1 and <= 4)
      return ViseLayout.Compact;
    if (revision is >= 10 and < 12)
      return ViseLayout.Wide;
    if (revision is >= 5 and < 10)
      return ViseLayout.Normal;
    throw new NotSupportedException($"VISE: uncompressed catalog revision {revision} is not supported.");
  }

  public static ViseLayout PackedLayout(byte revision) => revision >= 12 ? ViseLayout.Late
    : revision >= 5 ? ViseLayout.Wide
    : throw new NotSupportedException($"VISE: compressed catalog revision {revision} is not supported.");

  private static long LateFileSize(byte[] data, long offset) {
    const long fixedSize = 0xC6;
    if (!Span(data, offset, fixedSize))
      throw new InvalidDataException("VISE: truncated late FVCT record.");
    long primary = data[offset + 0x7A], variable = data[offset + 0x7B], secondary = data[offset + 0xB9];
    int type = data[offset + 0x75];
    var size = fixedSize + primary;
    var actionTail = (data[offset + 0x0C] & 0x08) != 0;
    if (actionTail) {
      size += variable;
      switch (type) {
        case 3:
        case 7:
        case 9:
        case 10:
          size = size + 1 + Byte(data, offset + size);
          break;
        case 4:
          // Some late replace actions append an optional Pascal display name.
          if (Span(data, offset + size, 4) && !TagAt(data, offset + size, "DVCT") && !TagAt(data, offset + size, "FVCT") && !TagAt(data, offset + size, "PACK"))
            size += 1 + data[offset + size];
          break;
        case 5: {
            var lengthOffset = size + Be16(data, offset + 0x38);
            size = lengthOffset + 1 + Byte(data, offset + lengthOffset);
            break;
          }
        case 6:
          size += Be16(data, offset + 0x2E) + Be16(data, offset + 0x32) + Be16(data, offset + 0x38);
          break;
        case 14:
        case 17:
          size += Be16(data, offset + 0x38);
          break;
        case 15:
          size += data[offset + 0xC1] + data[offset + 0xC5];
          break;
      }
    }
    size += secondary;
    if (!Span(data, offset, size))
      throw new InvalidDataException("VISE: truncated late FVCT variable fields.");
    return size;
  }

  private static ushort LateDirectoryFixed(byte revision) => revision switch {
    12 or 13 => 0xA0,
    14 => 0xA4,
    _ => throw new NotSupportedException($"VISE: late catalog revision {revision} is not supported."),
  };

  private static List<ViseRecord> ParseLate(byte[] data, int expected, byte revision) {
    var records = new List<ViseRecord>(expected + 1);
    long offset = 0;
    for (var i = 0; i < expected; ++i) {
      if (!Span(data, offset, 4))
        throw new InvalidDataException("VISE: truncated late catalog.");
      var record = new ViseRecord { Off = offset, Tag = Encoding.ASCII.GetString(data, (int)offset, 4) };
      records.Add(record);
      var last = i + 1 == expected;
      if (record.Tag == "FVCT") {
        record.FixedSize = 0xC6;
        offset += LateFileSize(data, offset);
      } else if (record.Tag == "DVCT") {
        record.FixedSize = LateDirectoryFixed(revision);
        if (!Span(data, offset, record.FixedSize))
          throw new InvalidDataException("VISE: truncated late DVCT record.");
        offset += record.FixedSize + data[offset + 0x4F] + data[offset + 0x50];
      } else
        throw new InvalidDataException("VISE: invalid late catalog record signature.");
      var nextOk = last ? TagAt(data, offset, "PACK") : TagAt(data, offset, "DVCT") || TagAt(data, offset, "FVCT");
      if (!nextOk)
        throw new InvalidDataException("VISE: invalid late catalog record length.");
    }
    records.Add(new ViseRecord { Off = offset, Tag = "PACK" });
    return records;
  }

  private static long VariableFileSize(byte[] data, long offset, long fixedSize) {
    if (!Span(data, offset, fixedSize))
      throw new InvalidDataException("VISE: truncated FVCT record.");
    long primary = data[offset + 0x7A];
    long variable = fixedSize > 0x7B ? data[offset + 0x7B] : 0;
    long secondary = fixedSize > 0xB9 ? data[offset + 0xB9] : 0;
    int type = fixedSize > 0x75 ? data[offset + 0x75] : 0;
    var size = fixedSize + primary;
    var action = (Be32(data, offset + 0x0C) & 0x08000000) != 0;
    if (!action)
      size += secondary;
    else
      switch (type) {
        case 8:
        case 11:
        case 12:
          size += variable;
          break;
        case 1:
        case 2:
          if (fixedSize >= 0xBA)
            size += variable + Be16(data, offset + 0x38);
          break;
        case 3:
        case 7:
        case 9:
        case 10: {
            var lengthOffset = size + variable;
            size = lengthOffset + 1 + Byte(data, offset + lengthOffset);
            break;
          }
        case 5: {
            var lengthOffset = size + variable + Be16(data, offset + 0x38);
            size = lengthOffset + 1 + Byte(data, offset + lengthOffset);
            break;
          }
        case 4:
          size += variable + Be16(data, offset + 0x38);
          break;
        case 6:
          size += variable + Be16(data, offset + 0x2E) + Be16(data, offset + 0x32) + Be16(data, offset + 0x38);
          break;
        case 13:
          size += variable + secondary;
          break;
        default:
          size += secondary;
          break;
      }
    if (!Span(data, offset, size))
      throw new InvalidDataException("VISE: truncated FVCT variable fields.");
    return size;
  }

  private static bool SemanticTag(byte[] data, long offset) => TagAt(data, offset, "DVCT") || TagAt(data, offset, "FVCT");

  private static long CompactRecordSize(byte[] data, long offset, bool directory, byte revision, out ushort fixedOut) {
    long baseFixed = directory ? 0x58 : 0x7C;
    long recordFixed = baseFixed + (revision >= 4 ? 0x10 : 0);
    long names = Byte(data, offset + (directory ? 0x50 : 0x7A));
    long size;
    if (directory)
      size = recordFixed + names;
    else {
      var action = (Be32(data, offset + 0x0C) & 0x08000000) != 0;
      var type = Byte(data, offset + 0x75);
      if (action && type is 4 or 6) {
        long extra = Be16(data, offset + 0x38);
        if (type == 6)
          extra += Be16(data, offset + 0x2E) + Be16(data, offset + 0x32);
        size = recordFixed + names + extra;
      } else
        size = VariableFileSize(data, offset, recordFixed);
    }
    if (!Span(data, offset, size))
      throw new InvalidDataException("VISE: truncated compact catalog record.");
    fixedOut = (ushort)recordFixed;
    return size;
  }

  private static List<ViseRecord> ParseRecords(byte[] data, long offset, int expected, ViseLayout layout, byte revision) {
    var records = new List<ViseRecord>(expected + 3);
    var position = offset;
    if (TagAt(data, position, "CVCT")) {
      records.Add(new ViseRecord { Off = position, Tag = "CVCT" });
      position += 0x14;
      if (TagAt(data, position, "PACK")) {
        if (!Span(data, position, 0x50) || !SemanticTag(data, position + 0x50))
          throw new InvalidDataException("VISE: invalid leading PACK record.");
        records.Add(new ViseRecord { Off = position, Tag = "PACK" });
        position += 0x50;
      }
    }
    for (var i = 0; i < expected; ++i) {
      if (!SemanticTag(data, position))
        throw new InvalidDataException("VISE: invalid catalog record signature.");
      var directory = TagAt(data, position, "DVCT");
      var record = new ViseRecord { Off = position, Tag = directory ? "DVCT" : "FVCT" };
      records.Add(record);
      if (layout == ViseLayout.Short) {
        if (!directory)
          position += CompactRecordSize(data, position, false, revision, out record.FixedSize);
        else {
          record.FixedSize = 0x52;
          position += 0x52 + Byte(data, position + 0x50);
        }
      } else if (layout == ViseLayout.Compact)
        position += CompactRecordSize(data, position, directory, revision, out record.FixedSize);
      else if (directory) {
        var fixedSize = layout == ViseLayout.Lite ? 0x58 : layout == ViseLayout.Normal ? 0x94 : 0x98;
        record.FixedSize = (ushort)fixedSize;
        position += fixedSize + Byte(data, position + 0x50);
      } else {
        var fixedSize = layout == ViseLayout.Lite ? 0x7C : layout == ViseLayout.Normal ? 0xBA : 0xBE;
        record.FixedSize = (ushort)fixedSize;
        position += VariableFileSize(data, position, fixedSize);
      }
      if (i + 1 < expected && !SemanticTag(data, position))
        throw new InvalidDataException("VISE: invalid catalog record length.");
    }
    if (!TagAt(data, position, "PACK"))
      throw new InvalidDataException("VISE: catalog lacks its trailing PACK record.");
    records.Add(new ViseRecord { Off = position, Tag = "PACK" });
    return records;
  }

  private static string? RecordName(byte[] data, ViseRecord r) {
    if (r.FixedSize == 0)
      return null;
    long lengthOffset = r.Tag == "FVCT" ? 0x7A : r.Tag == "DVCT" ? 0x50 : -1;
    if (lengthOffset < 0 || !Span(data, r.Off + lengthOffset, 1))
      return null;
    long length = data[r.Off + lengthOffset];
    if (length == 0 || r.FixedSize > r.End - r.Off || length > r.End - r.Off - r.FixedSize)
      return null;
    return FromMacRoman(data.AsSpan((int)(r.Off + r.FixedSize), (int)length));
  }

  private static void DecodeFileRecord(byte[] data, ViseRecord record, ViseLayout layout) {
    if (record.End - record.Off < 0x68)
      return;
    record.Packed[0] = Be32(data, record.Off + 0x44);
    record.RecordFlags = Be32(data, record.Off + 0x0C);
    record.Expanded[0] = Be32(data, record.Off + 0x48);
    record.Packed[1] = Be32(data, record.Off + 0x4C);
    record.Expanded[1] = Be32(data, record.Off + 0x50);
    record.Checksum = Be32(data, record.Off + 0x54);
    record.Segment = Be16(data, record.Off + 0x62);
    record.Payload = Be32(data, record.Off + 0x64);
    record.PayloadMode = Be32(data, record.Off + 0x60);
    if (record.End - record.Off > 0x75)
      record.Subtype = data[record.Off + 0x75];
    record.Created = Be32(data, record.Off + 0x3C);
    record.Modified = Be32(data, record.Off + 0x40);
    data.AsSpan((int)record.Off + 0x2C, 16).CopyTo(record.FinderInfo);
    record.File = (record.RecordFlags & 0x08000000) == 0;
    if (layout == ViseLayout.Lite) {
      record.Parent = Be32(data, record.Off + 0x58);
      return;
    }
    record.Parent = Be32(data, record.Off + (layout is ViseLayout.Compact or ViseLayout.Short ? 0x60 : 0x58));
    if (record.File && record.End - record.Off >= 0x70) {
      record.ForkOffset[0] = Be32(data, record.Off + 0x68);
      record.ForkOffset[1] = Be32(data, record.Off + 0x6C);
      record.HasForkOffsets = record.ForkOffset[0] != 0 || record.ForkOffset[1] != 0;
    }
    if (layout == ViseLayout.Late)
      record.Depth = Be16(data, record.Off + 0x60);
  }

  private static void DecodeRecords(byte[] data, List<ViseRecord> records, ViseLayout layout) {
    for (var i = 0; i < records.Count; ++i) {
      var record = records[i];
      record.End = i + 1 < records.Count ? records[i + 1].Off : data.Length;
      record.Name = RecordName(data, record);
      if (record.Tag == "DVCT" && record.End - record.Off >= 0x24) {
        record.DirId = Be32(data, record.Off + 0x1C);
        record.Parent = Be32(data, record.Off + 0x20);
        if (record.Parent == record.DirId)
          record.Parent = 0; // early creators encode a top-level folder as its own parent
        data.AsSpan((int)record.Off + 4, 16).CopyTo(record.FinderInfo);
        record.Created = Be32(data, record.Off + 0x14);
        record.Modified = Be32(data, record.Off + 0x18);
        if (layout == ViseLayout.Late)
          record.Depth = Be16(data, record.Off + 0x48);
      } else if (record.Tag == "FVCT")
        DecodeFileRecord(data, record, layout);
    }

    if (layout == ViseLayout.Late) {
      // Preorder with explicit nesting depths: the depth, not the parent field, is the hierarchy.
      var parents = new uint[records.Count + 1];
      for (var i = 0; i < records.Count; ++i) {
        var record = records[i];
        if (record.Tag is not ("DVCT" or "FVCT"))
          continue;
        if (record.Depth > records.Count || (record.Depth != 0 && parents[record.Depth - 1] == 0))
          throw new InvalidDataException("VISE: invalid late catalog depth.");
        record.Parent = record.Depth != 0 ? parents[record.Depth - 1] : 0;
        if (record.Tag == "DVCT") {
          record.DirId = (uint)i + 1;
          parents[record.Depth] = record.DirId;
        }
      }
    }

    if (layout is ViseLayout.Wide or ViseLayout.Late)
      foreach (var record in records)
        if (record.File && (record.PayloadMode & 1) == 0)
          record.BaseDependent = true; // an update whose decoder history is the installed file
  }

  private static void RecoverLitePaths(List<ViseRecord> records) {
    uint currentDir = 0;
    foreach (var record in records) {
      if (record.Tag == "DVCT") {
        currentDir = record.DirId;
        continue;
      }
      if (record.Tag != "FVCT" || !record.File || records.Any(r => r.Tag == "DVCT" && r.DirId == record.Parent))
        continue;
      if (records.Any(r => r.Tag == "DVCT" && r.Parent == record.Parent)) {
        currentDir = 0; // a destination used as a DVCT parent denotes the virtual root
        continue;
      }
      // A custom folder icon applies to the directory just before it; other runtime destination
      // objects keep the file under the current directory in preorder.
      record.Parent = currentDir;
    }
  }

  private static string SafeName(string name) {
    var s = name.Replace("\0", "").Replace('/', ':');
    if (s.Length == 0)
      s = "unnamed";
    if (s is "." or "..")
      s = "_" + s;
    return s;
  }

  private static string PathName(ViseRecord r, int index) => SafeName(r.Name ?? $"unnamed-{index:D4}");

  private static string RelativePath(List<ViseRecord> all, int index) {
    var r = all[index];
    var parts = new List<string> { PathName(r, index) };
    var visited = new bool[all.Count];
    var parent = r.Parent;
    for (var guard = 0; guard < all.Count; ++guard) {
      var d = all.FindIndex(x => x.Tag == "DVCT" && x.DirId == parent);
      if (d < 0)
        break;
      if (visited[d])
        throw new InvalidDataException("VISE: directory parent cycle.");
      visited[d] = true;
      parts.Add(PathName(all[d], d));
      parent = all[d].Parent;
    }
    parts.Reverse();
    return string.Join('/', parts);
  }

  private static bool PathEqual(string a, string b) {
    if (a.Length != b.Length)
      return false;
    for (var i = 0; i < a.Length; ++i) {
      var ca = a[i] is >= 'A' and <= 'Z' ? (char)(a[i] + 32) : a[i];
      var cb = b[i] is >= 'A' and <= 'Z' ? (char)(b[i] + 32) : b[i];
      if (ca != cb)
        return false;
    }
    return true;
  }

  private static void PlanOutputPaths(List<ViseRecord> all) {
    for (var i = 0; i < all.Count; ++i)
      all[i].Path = RelativePath(all, i);
    // The first output-bearing record keeps the path; later alternatives at it get their record number.
    for (var i = 0; i < all.Count; ++i) {
      if (!all[i].OutputsFork)
        continue;
      for (var j = 0; j < i; ++j)
        if (all[j].OutputsFork && PathEqual(all[i].Path, all[j].Path)) {
          all[j].OutputGroup = j;
          all[i].OutputGroup = j;
          all[i].Path += $"~{i:D4}";
          break;
        }
    }
    // A file alternative at a directory's path gets its record number; the directory keeps the name.
    for (var i = 0; i < all.Count; ++i) {
      if (!all[i].IsFileRecord)
        continue;
      if (all.Any(d => d.Tag == "DVCT" && PathEqual(all[i].Path, d.Path)))
        all[i].Path += $"~{i:D4}";
    }
  }

  /// <summary>Parses the catalog's records, their hierarchy and output paths.</summary>
  public static List<ViseRecord> Read(byte[] data, long offset, int expected, ViseLayout layout, byte revision) {
    var records = layout == ViseLayout.Late ? ParseLate(data, expected, revision) : ParseRecords(data, offset, expected, layout, revision);
    DecodeRecords(data, records, layout);
    if (layout == ViseLayout.Lite)
      RecoverLitePaths(records);
    PlanOutputPaths(records);
    return records;
  }
}
