#pragma warning disable CS1591
namespace FileSystem.OpenVms;

/// <summary>
/// Walks a CWB-OVMS-WB OpenVMS Files-11 ODS-2 volume and surfaces the user
/// files held in 000000.DIR. Confirms the volume by checking the
/// "CWB-OVMS-WB" layout marker at byte 132 of the home block — when the
/// marker is absent the reader returns no entries (the descriptor's
/// generic header-surface path takes over).
/// <para>
/// For each file the reader produces an <see cref="Entry"/> bundle
/// containing the File-ID, name, logical size, and the in-memory file
/// bytes (assembled by walking the File Header's retrieval pointers).
/// </para>
/// </summary>
public sealed class OpenVmsReader {

  /// <summary>Records that the volume's home block carries the CWB-OVMS-WB layout marker.</summary>
  public bool IsCwbVolume { get; }

  /// <summary>User entries surfaced from 000000.DIR (skips reserved FIDs 1, 2, 4).</summary>
  public List<Entry> Entries { get; } = [];

  /// <summary>Convenience accessor over the volume bytes (read-only).</summary>
  public byte[] Image { get; }

  public OpenVmsReader(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.CanSeek) image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    this.Image = ms.ToArray();

    this.IsCwbVolume = HasLayoutMarker(this.Image);
    if (!this.IsCwbVolume) return;

    this.Entries = ParseDirectory(this.Image);
  }

  public OpenVmsReader(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);
    this.Image = image;
    this.IsCwbVolume = HasLayoutMarker(image);
    if (!this.IsCwbVolume) return;
    this.Entries = ParseDirectory(image);
  }

  /// <summary>Extracts the bytes for <paramref name="entry"/> by walking its retrieval pointers.</summary>
  public byte[] Extract(Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var fh = ReadFileHeader(this.Image, entry.FileId);
    if (fh == null || !fh.InUse) return [];
    return AssembleFileBytes(this.Image, fh);
  }

  /// <summary>Reads a File Header from INDEXF.SYS by File-ID number (1-based).</summary>
  public static OpenVmsFileHeader? ReadFileHeader(byte[] image, int fileId) {
    if (fileId < 1 || fileId > OpenVmsLayout.MaxFiles) return null;
    var off = OpenVmsLayout.FileHeaderByteOffset(fileId);
    if (off + OpenVmsLayout.BlockSize > image.Length) return null;
    return OpenVmsFileHeader.Deserialize(image.AsSpan(off, OpenVmsLayout.BlockSize));
  }

  /// <summary>Reassembles file bytes from <paramref name="fh"/>'s retrieval pointers.</summary>
  public static byte[] AssembleFileBytes(byte[] image, OpenVmsFileHeader fh) {
    var bytes = new byte[fh.Size];
    long bytesWritten = 0;
    foreach (var ext in fh.Extents) {
      var extentBytes = ext.Count * (long)OpenVmsLayout.BlockSize;
      var copy = (int)Math.Min(extentBytes, fh.Size - bytesWritten);
      if (copy <= 0) break;
      var srcOff = OpenVmsLayout.LbnToByteOffset(ext.StartLbn);
      if (srcOff + copy > image.Length) copy = Math.Max(0, image.Length - srcOff);
      if (copy > 0)
        image.AsSpan(srcOff, copy).CopyTo(bytes.AsSpan((int)bytesWritten));
      bytesWritten += copy;
      if (bytesWritten >= fh.Size) break;
    }
    return bytes;
  }

  private static bool HasLayoutMarker(byte[] image) {
    var off = OpenVmsLayout.LbnToByteOffset(OpenVmsLayout.HomeBlockLbn) + OpenVmsLayout.LayoutMarkerOffset;
    if (off + OpenVmsLayout.LayoutMarker.Length > image.Length) return false;
    return image.AsSpan(off, OpenVmsLayout.LayoutMarker.Length)
      .SequenceEqual(OpenVmsLayout.LayoutMarker.AsSpan());
  }

  private static List<Entry> ParseDirectory(byte[] image) {
    var entries = new List<Entry>();
    var visited = new HashSet<int>();
    var lbn = OpenVmsLayout.RootDirectoryLbn;
    while (lbn > 0 && visited.Add(lbn)) {
      var off = OpenVmsLayout.LbnToByteOffset(lbn);
      if (off + OpenVmsLayout.BlockSize > image.Length) break;
      var blk = image.AsSpan(off, OpenVmsLayout.BlockSize);
      for (var slot = OpenVmsDirectory.FileEntryStartSlot; slot < OpenVmsDirectory.EntriesPerBlock; slot++) {
        var de = OpenVmsDirectory.ReadEntry(blk, slot);
        if (de.IsFree) continue;
        entries.Add(new Entry(de.FileId, de.Sequence, de.Name, de.Size));
      }
      lbn = OpenVmsDirectory.ReadChainLink(blk);
    }
    return entries;
  }

  /// <summary>One user-visible directory entry surfaced by the reader.</summary>
  public sealed record class Entry(int FileId, ushort Sequence, string Name, long Size);
}
