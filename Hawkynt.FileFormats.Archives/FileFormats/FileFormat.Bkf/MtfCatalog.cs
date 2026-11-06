using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Bkf;

/// <summary>
/// The Type 1 Media Based Catalog of MTF 1.00a §7.3: the File/Directory Detail (TFDD stream)
/// of one Data Set and the cumulative Set Map (TSMP stream), built from the DBLKs as written.
/// </summary>
internal static class MtfCatalog {
  private const int FddHeaderSize = 36;

  /// <summary>
  /// One MTF_FDD_VOLB/DIRB/FILE entry per DBLK, in medium order, then MTF_FDD_FEND padding the
  /// stream so the Set Map after it starts on a physical block (§7.3.2). LINK is, for a file,
  /// the stream offset of its folder's entry; for a folder, that of its next sibling folder; for
  /// a volume, that of the next volume — zero when there is none.
  /// </summary>
  public static byte[] FileDirectoryDetail(IReadOnlyList<(byte[] Dblk, MtfBlock Block)> blocks, ushort mediaSequence, int physicalBlock) {
    using var ms = new MemoryStream();
    var offsets = new int[blocks.Count];
    var entries = new List<byte[]>();
    var folders = new List<(int Index, string Path)>();
    var lastFolder = -1;
    var lastVolume = -1;
    var links = new int[blocks.Count];
    var linkTargets = new int[blocks.Count]; // index of the entry LINK points at, -1 for none
    Array.Fill(linkTargets, -1);

    for (var i = 0; i < blocks.Count; ++i) {
      var (dblk, block) = blocks[i];
      switch (block.Type) {
        case "VOLB":
          if (lastVolume >= 0) linkTargets[lastVolume] = i;
          lastVolume = i;
          lastFolder = -1;
          folders.Clear();
          break;
        case "DIRB": {
          var path = FolderPath(dblk, block);
          var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : path.Length == 0 ? null : "";
          // The previous folder with the same parent now links to this one.
          var sibling = folders.LastOrDefault(f => Parent(f.Path) == parent && f.Path != path);
          if (sibling.Path != null && parent != null && linkTargets[sibling.Index] < 0) linkTargets[sibling.Index] = i;
          folders.Add((i, path));
          lastFolder = i;
          break;
        }
        case "FILE":
          linkTargets[i] = lastFolder;
          break;
      }
      entries.Add(Entry(dblk, block, mediaSequence));
    }

    var at = 0;
    for (var i = 0; i < entries.Count; ++i) {
      offsets[i] = at;
      at += entries[i].Length;
    }
    for (var i = 0; i < entries.Count; ++i) {
      links[i] = linkTargets[i] < 0 ? 0 : offsets[linkTargets[i]];
      BinaryPrimitives.WriteInt32LittleEndian(entries[i].AsSpan(28), links[i]);
      ms.Write(entries[i]);
    }

    // FEND: its LENGTH runs to the physical block where the Set Map starts.
    var used = Mtf.StreamHeaderSize + (int)ms.Length;
    var fendLength = Mtf.Align(used + FddHeaderSize, physicalBlock) - used;
    var fend = new byte[fendLength];
    BinaryPrimitives.WriteUInt16LittleEndian(fend, (ushort)fendLength);
    Encoding.ASCII.GetBytes("FEND", fend.AsSpan(2));
    ms.Write(fend);
    return ms.ToArray();
  }

  private static string? Parent(string path) => path.Length == 0 ? null : path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

  private static string FolderPath(byte[] dblk, MtfBlock block) {
    var name = Mtf.DecodeString(NameBytes(dblk, block, 80, "PNAM"), dblk[Mtf.OffStringType]);
    return string.Join('/', name.Split('\0', StringSplitOptions.RemoveEmptyEntries));
  }

  /// <summary>The name a DIRB or FILE carries: its MTF_TAPE_ADDRESS string, or the PNAM/FNAM stream.</summary>
  private static byte[] NameBytes(byte[] dblk, MtfBlock block, int addressOffset, string streamId) {
    if ((BinaryPrimitives.ReadUInt32LittleEndian(dblk.AsSpan(52)) & (1u << 17)) != 0)
      return block.Stream(streamId)?.Data ?? [];
    return StringBytes(dblk, addressOffset);
  }

  private static byte[] StringBytes(byte[] dblk, int addressOffset) {
    var size = BinaryPrimitives.ReadUInt16LittleEndian(dblk.AsSpan(addressOffset));
    var offset = BinaryPrimitives.ReadUInt16LittleEndian(dblk.AsSpan(addressOffset + 2));
    return size == 0 || offset + size > dblk.Length ? [] : dblk.AsSpan(offset, size).ToArray();
  }

  private static byte[] Entry(byte[] dblk, MtfBlock block, ushort mediaSequence) {
    var type = block.Type;
    var (fixedSize, strings) = type switch {
      "VOLB" => (61, new[] { StringBytes(dblk, 56), StringBytes(dblk, 60), StringBytes(dblk, 64) }),
      "DIRB" => (68, new[] { NameBytes(dblk, block, 80, "PNAM") }),
      "FILE" => (68, new[] { NameBytes(dblk, block, 84, "FNAM") }),
      _ => (FddHeaderSize, Array.Empty<byte[]>()),
    };
    var entry = new byte[fixedSize + strings.Sum(s => s.Length)];
    BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)entry.Length);
    Encoding.ASCII.GetBytes(type, entry.AsSpan(2));
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), mediaSequence);
    dblk.AsSpan(Mtf.OffAttributes, 4).CopyTo(entry.AsSpan(8));
    dblk.AsSpan(Mtf.OffFormatLogicalAddress, 8).CopyTo(entry.AsSpan(12));
    dblk.AsSpan(Mtf.OffDisplayableSize, 8).CopyTo(entry.AsSpan(20));
    entry[32] = dblk[Mtf.OffOsId];
    entry[33] = dblk[Mtf.OffOsVersion];
    entry[34] = dblk[Mtf.OffStringType];

    var cursor = fixedSize;
    void Address(int at, byte[] s) {
      if (s.Length == 0) return;
      BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(at), (ushort)s.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(at + 2), (ushort)cursor);
      s.CopyTo(entry, cursor);
      cursor += s.Length;
    }
    switch (type) {
      case "VOLB":
        dblk.AsSpan(52, 4).CopyTo(entry.AsSpan(36));        // VOLB attributes
        Address(40, strings[0]);                             // device
        Address(44, strings[1]);                             // volume
        Address(48, strings[2]);                             // machine
        dblk.AsSpan(68, 5).CopyTo(entry.AsSpan(56));        // media write date
        break;
      case "DIRB":
      case "FILE":
        dblk.AsSpan(56, 20).CopyTo(entry.AsSpan(36));       // modification, creation, backup, access dates
        dblk.AsSpan(52, 4).CopyTo(entry.AsSpan(56));        // attributes
        Address(60, strings[0]);                             // name
        break;
    }
    return entry;
  }

  /// <summary>The Set Map stream: header (Media Family ID, entry count) and the entries so far (§7.3.3).</summary>
  public static byte[] SetMap(uint mediaFamilyId, IReadOnlyList<byte[]> entries) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[8];
    header.Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(header, mediaFamilyId);
    BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)entries.Count);
    ms.Write(header);
    foreach (var e in entries) ms.Write(e);
    return ms.ToArray();
  }

  /// <summary>
  /// One MTF_SM_ENTRY (copies of the SSET's fields, the Data Set's counts and where its FDD is)
  /// followed by a Volume Entry per VOLB, which has the MTF_FDD_VOLB layout.
  /// </summary>
  public static byte[] SetMapEntry(byte[] sset, IReadOnlyList<byte[]> blocks, ushort mediaSequence, long fddPba) {
    // SSET strings: name 64, description 68, password 72, user 76; the Set Map orders them
    // name, password, description, user.
    var strings = new[] { StringBytes(sset, 64), StringBytes(sset, 72), StringBytes(sset, 68), StringBytes(sset, 76) };
    var entry = new byte[91 + strings.Sum(s => s.Length)];
    BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)entry.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), mediaSequence);
    sset.AsSpan(Mtf.OffAttributes, 4).CopyTo(entry.AsSpan(4));
    sset.AsSpan(52, 4).CopyTo(entry.AsSpan(8));                            // SSET attributes
    sset.AsSpan(80, 8).CopyTo(entry.AsSpan(12));                           // SSET PBA
    BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(20), (ulong)fddPba);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(28), mediaSequence);
    sset.AsSpan(62, 2).CopyTo(entry.AsSpan(30));                           // Data Set number
    var types = blocks.Select(b => Encoding.ASCII.GetString(b, 0, 4)).ToList();
    BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(40), (uint)types.Count(t => t == "DIRB"));
    BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(44), (uint)types.Count(t => t == "FILE"));
    BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(48), (uint)types.Count(t => t == "CFIL"));
    // Data Set Displayable Size (52) stays zero: §7.3.3.3 asks for the sum of the files' sizes,
    // but NTBackup writes zero there (measured on a Windows 2000 backup), and NTBackup is the
    // reader these media are for.
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(60), (ushort)types.Count(t => t == "VOLB"));
    sset.AsSpan(56, 2).CopyTo(entry.AsSpan(62));                           // password encryption algorithm
    var cursor = 91;
    for (var i = 0; i < strings.Length; ++i) {
      if (strings[i].Length == 0) continue;
      BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(64 + 4 * i), (ushort)strings[i].Length);
      BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(66 + 4 * i), (ushort)cursor);
      strings[i].CopyTo(entry, cursor);
      cursor += strings[i].Length;
    }
    sset.AsSpan(88, 5).CopyTo(entry.AsSpan(80));                           // media write date
    entry[85] = sset[95];                                                  // time zone
    entry[86] = sset[Mtf.OffOsId];
    entry[87] = sset[Mtf.OffOsVersion];
    entry[88] = sset[Mtf.OffStringType];
    entry[89] = sset[96];                                                  // MTF minor version
    entry[90] = sset[97];                                                  // media catalog version

    using var ms = new MemoryStream();
    ms.Write(entry);
    foreach (var volume in blocks.Where(b => Encoding.ASCII.GetString(b, 0, 4) == "VOLB"))
      ms.Write(Entry(volume, new MtfBlock { Dblk = volume }, mediaSequence));
    return ms.ToArray();
  }
}
