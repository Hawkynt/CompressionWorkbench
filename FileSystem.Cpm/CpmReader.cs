#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Cpm;

/// <summary>
/// Reader for CP/M 2.2 disk images (8" SSSD reference geometry). Each file is
/// reconstructed from its directory extents; extents are matched by <c>(userCode,
/// name.ext)</c> and ordered by the extent counter before their block lists are
/// concatenated.
/// </summary>
public sealed class CpmReader {

  public sealed record CpmFile(
    byte UserCode,
    string Name,
    string Extension,
    bool ReadOnly,
    bool System,
    bool Archive,
    int RecordCount,
    byte[] Data
  ) {
    public string FullName =>
      string.IsNullOrEmpty(this.Extension) ? this.Name : $"{this.Name}.{this.Extension}";
  }

  public sealed record Volume(IReadOnlyList<CpmFile> Files, byte[] Image);

  public static Volume Read(ReadOnlySpan<byte> image) {
    if (image.Length < CpmLayout.TotalBytes)
      throw new InvalidDataException($"CP/M: image too small ({image.Length} bytes, expected at least {CpmLayout.TotalBytes}).");

    // Collect extents keyed by (userCode, name, ext).
    var extents = new Dictionary<(byte u, string n, string x), List<Extent>>();

    for (var i = 0; i < CpmLayout.DirectoryEntries; i++) {
      var off = CpmLayout.ReservedBytes + i * CpmLayout.DirectoryEntrySize;
      var entry = image.Slice(off, CpmLayout.DirectoryEntrySize);
      var userCode = entry[0];
      if (userCode == CpmLayout.EmptyEntryUserCode) continue;
      if (userCode > 0x1F) continue; // user codes 0..31 valid (16 in bare 2.2, 32 with CP/M 3)

      var nameBytes = entry.Slice(1, 8).ToArray();
      var extBytes = entry.Slice(9, 3).ToArray();

      var readOnly = (extBytes[0] & 0x80) != 0;
      var sysHidden = (extBytes[1] & 0x80) != 0;
      var archive = (extBytes[2] & 0x80) != 0;
      for (var k = 0; k < nameBytes.Length; k++) nameBytes[k] &= 0x7F;
      for (var k = 0; k < extBytes.Length; k++) extBytes[k] &= 0x7F;

      var name = Encoding.ASCII.GetString(nameBytes).TrimEnd(' ');
      var ext = Encoding.ASCII.GetString(extBytes).TrimEnd(' ');

      var ex = entry[12];
      var s2 = entry[14];
      var rc = entry[15];
      // Block list: 16 single-byte block numbers (for disks with ≤ 256 blocks — our reference geometry has 243).
      var blocks = new int[16];
      for (var b = 0; b < 16; b++) blocks[b] = entry[16 + b];

      var key = (userCode, name, ext);
      if (!extents.TryGetValue(key, out var list)) {
        list = new List<Extent>();
        extents[key] = list;
      }
      list.Add(new Extent(
        EntryNumber: ((s2 & 0x3F) << 5) | (ex & 0x1F),
        RecordCount: rc,
        Blocks: blocks,
        ReadOnly: readOnly,
        System: sysHidden,
        Archive: archive));
    }

    var files = new List<CpmFile>();
    foreach (var ((u, n, x), list) in extents) {
      list.Sort((a, b) => a.EntryNumber.CompareTo(b.EntryNumber));
      var dataBytes = new List<byte>();
      var totalRecords = 0;
      var ro = false; var sys = false; var arc = false;

      for (var ei = 0; ei < list.Count; ei++) {
        var e = list[ei];
        ro |= e.ReadOnly; sys |= e.System; arc |= e.Archive;
        totalRecords += ei == list.Count - 1 ? e.RecordCount : CpmLayout.RecordsPerExtent;
        var extRecords = ei == list.Count - 1 ? e.RecordCount : CpmLayout.RecordsPerExtent;
        var extBytes = extRecords * CpmLayout.SectorSize;
        var written = 0;
        for (var bi = 0; bi < e.Blocks.Length && written < extBytes; bi++) {
          var blk = e.Blocks[bi];
          if (blk == 0) break;
          var blkOffset = CpmLayout.ReservedBytes + blk * CpmLayout.BlockSize;
          if (blkOffset + CpmLayout.BlockSize > image.Length) break;
          var toTake = Math.Min(CpmLayout.BlockSize, extBytes - written);
          var slice = image.Slice(blkOffset, toTake);
          dataBytes.AddRange(slice.ToArray());
          written += toTake;
        }
      }

      files.Add(new CpmFile(
        UserCode: u,
        Name: n,
        Extension: x,
        ReadOnly: ro,
        System: sys,
        Archive: arc,
        RecordCount: totalRecords,
        Data: dataBytes.ToArray()));
    }

    return new Volume(files, image.ToArray());
  }

  private sealed record Extent(int EntryNumber, byte RecordCount, int[] Blocks, bool ReadOnly, bool System, bool Archive);
}
