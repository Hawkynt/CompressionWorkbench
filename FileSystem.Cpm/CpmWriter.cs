#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Cpm;

/// <summary>
/// Writer for CP/M 2.2 disk images using the 8" SSSD reference geometry. Files
/// are split into 16 KB extents; each extent carries up to 16 block numbers and
/// the record count of its final used sector. The writer enforces the built-in
/// disk size limit (241 data blocks, 64 directory entries) and rejects overflow
/// explicitly rather than producing a truncated volume.
/// </summary>
public sealed class CpmWriter {

  public static byte[] Build(IReadOnlyList<(string Name, byte[] Data, byte UserCode)> files) {
    var image = new byte[CpmLayout.TotalBytes];
    // Fill the directory area with the CP/M 'empty slot' marker (0xE5 throughout each entry).
    for (var i = CpmLayout.ReservedBytes; i < CpmLayout.ReservedBytes + CpmLayout.DirectoryBytes; i++)
      image[i] = CpmLayout.EmptyEntryUserCode;

    // Plan block allocations.
    var nextBlock = CpmLayout.DataBlockStart;
    var entries = new List<byte[]>();
    foreach (var (name, data, user) in files) {
      var (baseName, ext) = SplitName(name);
      var totalRecords = (int)Math.Ceiling(data.Length / (double)CpmLayout.SectorSize);
      var totalBlocks = (int)Math.Ceiling(data.Length / (double)CpmLayout.BlockSize);
      if (totalBlocks == 0) totalBlocks = 1; // empty file still gets one block

      if (nextBlock + totalBlocks > CpmLayout.TotalBlocks)
        throw new InvalidOperationException($"CP/M: disk full — cannot fit {name} ({totalBlocks} blocks needed).");

      // Allocate blocks and copy data.
      var blockNumbers = new int[totalBlocks];
      for (var b = 0; b < totalBlocks; b++) {
        blockNumbers[b] = nextBlock;
        var srcStart = b * CpmLayout.BlockSize;
        var srcLen = Math.Min(CpmLayout.BlockSize, data.Length - srcStart);
        if (srcLen > 0)
          Array.Copy(data, srcStart, image, CpmLayout.ReservedBytes + nextBlock * CpmLayout.BlockSize, srcLen);
        nextBlock++;
      }

      // Emit one directory entry per extent.
      var extentsNeeded = (totalBlocks + CpmLayout.BlocksPerExtent - 1) / CpmLayout.BlocksPerExtent;
      if (extentsNeeded == 0) extentsNeeded = 1;
      for (var e = 0; e < extentsNeeded; e++) {
        var entry = new byte[CpmLayout.DirectoryEntrySize];
        entry[0] = user;
        CopyAscii(entry, 1, baseName, 8);
        CopyAscii(entry, 9, ext, 3);
        var extNum = e;
        entry[12] = (byte)(extNum & 0x1F);
        entry[13] = 0;
        entry[14] = (byte)((extNum >> 5) & 0x3F);

        var isLast = e == extentsNeeded - 1;
        var blocksInThis = isLast
          ? totalBlocks - e * CpmLayout.BlocksPerExtent
          : CpmLayout.BlocksPerExtent;
        var recordsInThis = isLast
          ? totalRecords - e * CpmLayout.RecordsPerExtent
          : CpmLayout.RecordsPerExtent;
        if (recordsInThis > CpmLayout.RecordsPerExtent) recordsInThis = CpmLayout.RecordsPerExtent;
        if (recordsInThis < 0) recordsInThis = 0;
        entry[15] = (byte)recordsInThis;

        for (var b = 0; b < blocksInThis; b++)
          entry[16 + b] = (byte)blockNumbers[e * CpmLayout.BlocksPerExtent + b];
        // Remaining block-list bytes stay zero (unused slot).

        entries.Add(entry);
      }
    }

    if (entries.Count > CpmLayout.DirectoryEntries)
      throw new InvalidOperationException(
        $"CP/M: directory full — {entries.Count} entries needed but only {CpmLayout.DirectoryEntries} available.");

    for (var i = 0; i < entries.Count; i++) {
      var off = CpmLayout.ReservedBytes + i * CpmLayout.DirectoryEntrySize;
      entries[i].CopyTo(image.AsSpan(off));
    }

    return image;
  }

  private static (string Name, string Ext) SplitName(string fullName) {
    var file = Path.GetFileName(fullName);
    var dot = file.LastIndexOf('.');
    if (dot < 0) return (Truncate(file, 8).ToUpperInvariant(), "");
    var name = Truncate(file[..dot], 8).ToUpperInvariant();
    var ext = Truncate(file[(dot + 1)..], 3).ToUpperInvariant();
    return (name, ext);
  }

  private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

  private static void CopyAscii(byte[] dest, int offset, string s, int width) {
    var bytes = Encoding.ASCII.GetBytes(s);
    for (var i = 0; i < width; i++) {
      var b = i < bytes.Length ? bytes[i] : (byte)' ';
      // Replace invalid filesystem characters with underscore; strip high bit.
      if (b < 0x20 || b > 0x7E) b = (byte)'_';
      if (b is (byte)'<' or (byte)'>' or (byte)'.' or (byte)',' or (byte)';' or (byte)':' or (byte)'=' or (byte)'?' or (byte)'*' or (byte)'[' or (byte)']')
        b = (byte)'_';
      dest[offset + i] = b;
    }
  }
}
