using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Flex;

/// <summary>
/// Formats FLEX disks the way NEWDISK does and edits them the way the File Management
/// System does: files are written by taking sectors from the head of the free chain,
/// deleted files have their name's first byte set to $FF (negative) and their chain appended
/// to the tail of the free chain. The image is edited in memory; callers write it back.
/// </summary>
/// <remarks>
/// FLEX stores whole sectors of 252 data bytes and keeps no byte length, so a file whose
/// length is not a multiple of 252 is padded with zeros — what FLEX itself reads back. A
/// zero-length file takes one zero sector, the least a FLEX file can hold. Names are
/// folded to capitals and must fit FLEX's rule: a letter, then letters, digits, hyphens
/// or underscores, at most 8 characters, with an extension of 1-3 such characters.
/// </remarks>
public static class FlexWriter {
  /// <summary>Standard geometries tried, smallest first, when none is given.</summary>
  public static readonly (int Tracks, int Sectors)[] Geometries = [(35, 10), (40, 18), (77, 15), (80, 36), (255, 255)];

  /// <summary>Formats an empty disk.</summary>
  public static byte[] Format(int tracks, int sectorsPerTrack, string label = "", ushort volumeNumber = 0, DateTime? date = null) {
    if (tracks is < 2 or > 256 || sectorsPerTrack is < 5 or > 255)
      throw new NotSupportedException($"FLEX geometry {tracks}×{sectorsPerTrack} is outside 2-256 tracks × 5-255 sectors.");
    var img = new byte[(long)tracks * sectorsPerTrack * FlexDisk.SectorSize];

    // Directory: track 0 sectors 5..last, linked, zeroed, last link 0.
    for (var s = 5; s < sectorsPerTrack; ++s) {
      var at = Offset(sectorsPerTrack, 0, s);
      img[at] = 0; img[at + 1] = (byte)(s + 1);
    }
    // Free chain: every sector of tracks 1.., in order, last link 0.
    for (var t = 1; t < tracks; ++t)
      for (var s = 1; s <= sectorsPerTrack; ++s) {
        var at = Offset(sectorsPerTrack, t, s);
        var (nt, ns) = s < sectorsPerTrack ? (t, s + 1) : (t + 1, 1);
        if (nt >= tracks) (nt, ns) = (0, 0);
        img[at] = (byte)nt; img[at + 1] = (byte)ns;
      }

    var sir = img.AsSpan(FlexDisk.SirOffset, FlexDisk.SectorSize);
    var name = Encoding.ASCII.GetBytes(label.ToUpperInvariant());
    name.AsSpan(0, Math.Min(name.Length, 11)).CopyTo(sir[0x10..]);
    BinaryPrimitives.WriteUInt16BigEndian(sir[0x1B..], volumeNumber);
    sir[0x1D] = 1; sir[0x1E] = 1;
    sir[0x1F] = (byte)(tracks - 1); sir[0x20] = (byte)sectorsPerTrack;
    BinaryPrimitives.WriteUInt16BigEndian(sir[0x21..], checked((ushort)((tracks - 1) * sectorsPerTrack)));
    WriteDate(sir[0x23..], date ?? DateTime.UtcNow);
    sir[0x26] = (byte)(tracks - 1);
    sir[0x27] = (byte)sectorsPerTrack;
    return img;
  }

  /// <summary>Picks the smallest standard geometry whose data area holds <paramref name="sectorsNeeded"/> sectors.</summary>
  public static (int Tracks, int Sectors) ChooseGeometry(long sectorsNeeded, int directoryEntries) {
    foreach (var (t, s) in Geometries) {
      var dirSlots = (s - 4) * FlexDisk.EntriesPerSector;
      var extraDir = directoryEntries > dirSlots ? (directoryEntries - dirSlots + FlexDisk.EntriesPerSector - 1) / FlexDisk.EntriesPerSector : 0;
      if ((long)(t - 1) * s >= sectorsNeeded + extraDir && (long)(t - 1) * s <= ushort.MaxValue) return (t, s);
    }
    throw new NotSupportedException($"FLEX disks hold at most {ushort.MaxValue} data sectors; {sectorsNeeded} are needed.");
  }

  /// <summary>Encodes a host name as FLEX name and extension, or throws <see cref="NotSupportedException"/>.</summary>
  public static (byte[] Name, byte[] Ext) EncodeName(string fileName) {
    ArgumentNullException.ThrowIfNull(fileName);
    var up = fileName.ToUpperInvariant();
    var dot = up.LastIndexOf('.');
    var name = dot < 0 ? up : up[..dot];
    var ext = dot < 0 ? "" : up[(dot + 1)..];
    static bool Valid(string s, int max) => s.Length is > 0 && s.Length <= max && s[0] is >= 'A' and <= 'Z'
      && s.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');
    if (!Valid(name, 8) || !Valid(ext, 3))
      throw new NotSupportedException($"FLEX names are a letter then letters, digits, '-' or '_' (8 at most) with a 1-3 character extension; '{fileName}' is not.");
    var n = new byte[8];
    var e = new byte[3];
    Encoding.ASCII.GetBytes(name, n);
    Encoding.ASCII.GetBytes(ext, e);
    return (n, e);
  }

  /// <summary>Adds (or replaces) a file in the image.</summary>
  public static void AddFile(byte[] img, string fileName, ReadOnlySpan<byte> data, DateTime? date = null) {
    ArgumentNullException.ThrowIfNull(img);
    var (name, ext) = EncodeName(fileName);
    var disk = new FlexDisk(new MemoryStream(img, writable: false));
    var existing = disk.Entries.FirstOrDefault(e => Matches(img, e.EntryOffset, name, ext));
    if (existing is not null) {
      if ((existing.Attributes & 0xC0) != 0)
        throw new NotSupportedException($"FLEX file {existing.FullName} is write- or delete-protected.");
      RemoveFile(img, fileName);
      disk = new FlexDisk(new MemoryStream(img, writable: false));
    }

    var sectors = Math.Max(1, (data.Length + FlexDisk.DataBytes - 1) / FlexDisk.DataBytes);
    var spt = disk.SectorsPerTrack;
    var free = FreeChain(img, disk);
    var slot = FindSlot(img, disk);
    var needDirSector = slot is null ? 1 : 0;
    if (free.Count < sectors + needDirSector)
      throw new NotSupportedException($"FLEX disk has {free.Count} free sectors; '{fileName}' needs {sectors + needDirSector}.");

    var taken = 0;
    if (slot is null) {
      // Grow the directory by one sector from the head of the free chain, as FMS does.
      var (dt, ds) = free[taken++];
      var dat = Offset(spt, dt, ds);
      img.AsSpan((int)dat, FlexDisk.SectorSize).Clear();
      var (lt, ls) = disk.DirectorySectors[^1];
      var lat = Offset(spt, lt, ls);
      img[lat] = dt; img[lat + 1] = ds;
      slot = dat + 16;
    }

    for (var i = 0; i < sectors; ++i) {
      var (t, s) = free[taken + i];
      var at = (int)Offset(spt, t, s);
      var sector = img.AsSpan(at, FlexDisk.SectorSize);
      sector.Clear();
      if (i + 1 < sectors) { sector[0] = free[taken + i + 1].Track; sector[1] = free[taken + i + 1].Sector; }
      BinaryPrimitives.WriteUInt16BigEndian(sector[2..], (ushort)(i + 1));
      var start = i * FlexDisk.DataBytes;
      if (start < data.Length) data.Slice(start, Math.Min(FlexDisk.DataBytes, data.Length - start)).CopyTo(sector[4..]);
    }
    var first = free[taken];
    var last = free[taken + sectors - 1];
    taken += sectors;

    var entry = img.AsSpan((int)slot.Value, FlexDisk.EntrySize);
    entry.Clear();
    name.CopyTo(entry);
    ext.CopyTo(entry[8..]);
    entry[13] = first.Track; entry[14] = first.Sector;
    entry[15] = last.Track; entry[16] = last.Sector;
    BinaryPrimitives.WriteUInt16BigEndian(entry[17..], (ushort)sectors);
    WriteDate(entry[21..], date ?? DateTime.UtcNow);

    var sir = img.AsSpan(FlexDisk.SirOffset, FlexDisk.SectorSize);
    if (taken < free.Count) { sir[0x1D] = free[taken].Track; sir[0x1E] = free[taken].Sector; }
    else { sir[0x1D] = 0; sir[0x1E] = 0; sir[0x1F] = 0; sir[0x20] = 0; }
    BinaryPrimitives.WriteUInt16BigEndian(sir[0x21..], (ushort)(free.Count - taken));
  }

  /// <summary>Deletes a file: name marked deleted, sectors zeroed and appended to the free chain. False when absent.</summary>
  public static bool RemoveFile(byte[] img, string fileName) {
    ArgumentNullException.ThrowIfNull(img);
    var (name, ext) = EncodeName(fileName);
    var disk = new FlexDisk(new MemoryStream(img, writable: false));
    var e = disk.Entries.FirstOrDefault(x => Matches(img, x.EntryOffset, name, ext));
    if (e is null) return false;
    if ((e.Attributes & 0x40) != 0) throw new NotSupportedException($"FLEX file {e.FullName} is delete-protected.");
    _ = disk.Extract(e);                       // the chain must be sound before it joins the free chain
    var spt = disk.SectorsPerTrack;
    var chain = disk.Chain(e.StartTrack, e.StartSector, e.Sectors).Select(c => (c.Track, c.Sector)).ToList();
    var free = FreeChain(img, disk);

    for (var i = 0; i < chain.Count; ++i) {
      var at = (int)Offset(spt, chain[i].Track, chain[i].Sector);
      img.AsSpan(at + 2, FlexDisk.SectorSize - 2).Clear();   // wipe record number and data, keep the link
    }
    var sir = img.AsSpan(FlexDisk.SirOffset, FlexDisk.SectorSize);
    if (free.Count == 0) { sir[0x1D] = e.StartTrack; sir[0x1E] = e.StartSector; }
    else {
      var tail = (int)Offset(spt, free[^1].Track, free[^1].Sector);
      img[tail] = e.StartTrack; img[tail + 1] = e.StartSector;
    }
    sir[0x1F] = e.EndTrack; sir[0x20] = e.EndSector;
    BinaryPrimitives.WriteUInt16BigEndian(sir[0x21..], (ushort)(free.Count + chain.Count));
    img[e.EntryOffset] = 0xFF;   // "negative" first byte (FPM); $FF is what real disks show and what flexfloppy skips
    return true;
  }

  private static bool Matches(byte[] img, long entryOffset, byte[] name, byte[] ext)
    => img.AsSpan((int)entryOffset, 8).SequenceEqual(name) && img.AsSpan((int)entryOffset + 8, 3).SequenceEqual(ext);

  /// <summary>The free chain, checked against the SIR's head, tail and count; an inconsistent one is refused.</summary>
  private static List<(byte Track, byte Sector)> FreeChain(byte[] img, FlexDisk disk) {
    List<(byte Track, byte Sector)> list = disk.FirstFree == (0, 0)
      ? []
      : [.. disk.Chain(disk.FirstFree.Track, disk.FirstFree.Sector, disk.Tracks * disk.SectorsPerTrack).Select(c => (c.Track, c.Sector))];
    if (list.Count != disk.FreeSectors || (list.Count > 0 && list[^1] != disk.LastFree))
      throw new NotSupportedException($"FLEX free chain ({list.Count} sectors) does not match the SIR ({disk.FreeSectors} sectors ending {disk.LastFree.Track},{disk.LastFree.Sector}); the disk is not edited.");
    return list;
  }

  /// <summary>First never-used or deleted directory entry, or null when every directory sector is full.</summary>
  private static long? FindSlot(byte[] img, FlexDisk disk) {
    foreach (var (t, s) in disk.DirectorySectors) {
      var at = Offset(disk.SectorsPerTrack, t, s);
      for (var i = 0; i < FlexDisk.EntriesPerSector; ++i) {
        var e = at + 16 + i * FlexDisk.EntrySize;
        if (img[e] == 0 || (img[e] & 0x80) != 0) return e;
      }
    }
    return null;
  }

  private static long Offset(int spt, int track, int sector) => ((long)track * spt + sector - 1) * FlexDisk.SectorSize;

  private static void WriteDate(Span<byte> d, DateTime t) {
    d[0] = (byte)t.Month; d[1] = (byte)t.Day; d[2] = (byte)(t.Year % 100);
  }
}
