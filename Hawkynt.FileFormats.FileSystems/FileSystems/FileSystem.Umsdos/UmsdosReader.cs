#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileSystem.Fat;

namespace FileSystem.Umsdos;

/// <summary>One file, directory or symbolic link as Linux saw it through UMSDOS.</summary>
public sealed record UmsdosEntry(string Name, long Size, bool IsDirectory, bool IsSymlink, string? LinkTarget, DateTime? LastModified, ushort Mode, int Uid, int Gid);

/// <summary>
/// Reads UMSDOS, the Linux file system that lived on an MS-DOS FAT volume: every
/// directory that Linux used holds a <c>--linux-.---</c> file (the EMD) whose 64-byte
/// records carry the Linux name, mode, owner, times and link count of each entry. The
/// record's position names the DOS file that holds the data. A name MS-DOS can store
/// is used as is (upper-cased by DOS). Any other name is stored under a mangled DOS
/// name whose extension encodes the record number: <c>"{_4"</c> is record 4.
/// <para>Symbolic links are DOS files holding the target. Pseudo hard links (flag 2)
/// are DOS files holding the path of the real file, which sits in a hidden record
/// (flag 1); hidden records are not listed. Devices, FIFOs and sockets have no DOS data
/// and are not listed. Directories without an EMD are listed by their DOS names.</para>
/// <para>Layout: Linux 2.4 <c>include/linux/umsdos_fs.h</c> (i386: 16-bit uid, gid, dev
/// and mode, 32-bit times) and <c>fs/umsdos/mangle.c</c>, read as a specification; checked
/// against Slackware 8.1's ZipSlack distribution, whose package logs name the files
/// the volume must hold.</para>
/// </summary>
public sealed class UmsdosReader : IDisposable {
  public const string EmdName = "--linux-.---";
  private const int RecordSize = 64;
  private const int NameOffset = 36;
  private const byte FlagHidden = 1;
  private const byte FlagHardLink = 2;
  // mangle.c's lookup3: nine odd characters, then lookup12 continues the same table.
  private const string Lookup12 = "_123456789abcdefghijklmnopqrstuv";
  private const string Lookup3 = "{}()!`^&@" + Lookup12;

  private readonly FatReader _fat;
  private readonly Dictionary<string, Dictionary<string, FatEntry>> _dos = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, List<(Record Rec, FatEntry? Dos)>> _emd = new(StringComparer.OrdinalIgnoreCase);
  private readonly List<UmsdosEntry> _entries = [];
  private readonly Dictionary<string, FatEntry> _data = new(StringComparer.Ordinal);

  public IReadOnlyList<UmsdosEntry> Entries => _entries;

  /// <summary>Number of directories that carried an EMD.</summary>
  public int EmdDirectories => _emd.Count;

  internal readonly record struct Record(int Index, byte Flags, ushort Links, ushort Uid, ushort Gid, uint MTime, ushort Mode, string Name);

  public UmsdosReader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    _fat = new FatReader(stream, leaveOpen);
    _dos[""] = new(StringComparer.OrdinalIgnoreCase);
    foreach (var e in _fat.Entries) {
      var slash = e.Name.LastIndexOf('/');
      var parent = slash < 0 ? "" : e.Name[..slash];
      var leaf = slash < 0 ? e.Name : e.Name[(slash + 1)..];
      if (!_dos.TryGetValue(parent, out var map)) _dos[parent] = map = new(StringComparer.OrdinalIgnoreCase);
      map[leaf] = e;
      if (e.IsDirectory && !_dos.ContainsKey(e.Name)) _dos[e.Name] = new(StringComparer.OrdinalIgnoreCase);
    }
    if (!_dos.Values.Any(d => d.ContainsKey(EmdName)))
      throw new InvalidDataException("UMSDOS: no directory of this FAT volume holds a --linux-.--- file.");
    Walk("", "");
  }

  /// <summary>Parses an EMD file into its live records.</summary>
  internal static List<Record> ParseEmd(ReadOnlySpan<byte> emd) {
    var list = new List<Record>();
    for (var off = 0; off + NameOffset <= emd.Length;) {
      int nameLength = emd[off];
      if (nameLength == 0) { off += RecordSize; continue; }
      var size = (NameOffset + nameLength + RecordSize - 1) / RecordSize * RecordSize;
      if (off + NameOffset + nameLength > emd.Length) break;
      list.Add(new Record(
        off / RecordSize,
        emd[off + 1],
        BinaryPrimitives.ReadUInt16LittleEndian(emd[(off + 2)..]),
        BinaryPrimitives.ReadUInt16LittleEndian(emd[(off + 4)..]),
        BinaryPrimitives.ReadUInt16LittleEndian(emd[(off + 6)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(emd[(off + 12)..]),
        BinaryPrimitives.ReadUInt16LittleEndian(emd[(off + 22)..]),
        Encoding.Latin1.GetString(emd.Slice(off + NameOffset, nameLength))));
      off += size;
    }
    return list;
  }

  /// <summary>The three-character extension UMSDOS gives the DOS file of record <paramref name="index"/>.</summary>
  internal static string MangledExtension(int index)
    => new([Lookup3[(index >> 10) & 31], Lookup12[(index >> 5) & 31], Lookup12[index & 31]]);

  private static FatEntry? DosFile(Dictionary<string, FatEntry> dir, Record r) {
    var ext = MangledExtension(r.Index);
    FatEntry? match = null;
    foreach (var (name, entry) in dir) {
      var dot = name.LastIndexOf('.');
      if (dot < 0 || !name[(dot + 1)..].Equals(ext, StringComparison.OrdinalIgnoreCase)) continue;
      if (match != null) return null; // ambiguous: refuse rather than guess
      match = entry;
    }
    if (match != null) return match;
    return dir.TryGetValue(r.Name, out var plain) && !plain.Name.EndsWith(EmdName, StringComparison.OrdinalIgnoreCase) ? plain : null;
  }

  private void Walk(string dosPath, string linuxPath) {
    var dir = _dos[dosPath];
    if (!dir.TryGetValue(EmdName, out var emdEntry)) {
      foreach (var (name, e) in dir) {
        var path = linuxPath.Length == 0 ? name : linuxPath + "/" + name;
        _entries.Add(new UmsdosEntry(path, e.IsDirectory ? 0 : e.Size, e.IsDirectory, false, null, e.LastModified, 0, 0, 0));
        if (e.IsDirectory) Walk(e.Name, path); else _data[path] = e;
      }
      return;
    }
    var records = ParseEmd(_fat.Extract(emdEntry));
    var mapped = records.Select(r => (r, DosFile(dir, r))).ToList();
    _emd[dosPath] = mapped;
    foreach (var (r, dos) in mapped) {
      if ((r.Flags & FlagHidden) != 0 || dos == null || r.Name is "." or ".." || r.Name.Contains('/')) continue;
      var path = linuxPath.Length == 0 ? r.Name : linuxPath + "/" + r.Name;
      var time = r.MTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.MTime).UtcDateTime : (DateTime?)null;
      switch (r.Mode & 0xF000) {
        case 0x4000 when dos.IsDirectory:
          _entries.Add(new UmsdosEntry(path, 0, true, false, null, time, r.Mode, r.Uid, r.Gid));
          Walk(dos.Name, path);
          break;
        case 0xA000 when !dos.IsDirectory:
          var target = Encoding.Latin1.GetString(_fat.Extract(dos));
          _entries.Add(new UmsdosEntry(path, target.Length, false, true, target, time, r.Mode, r.Uid, r.Gid));
          break;
        case 0x8000 when !dos.IsDirectory:
          var data = (r.Flags & FlagHardLink) != 0 ? ResolveHardLink(dos) : dos;
          if (data == null) continue;
          _entries.Add(new UmsdosEntry(path, data.Size, false, false, null, time, r.Mode, r.Uid, r.Gid));
          _data[path] = data;
          break;
      }
    }
  }

  // A pseudo hard link's DOS file holds the path, from the DOS root, of the hidden record with the data.
  private FatEntry? ResolveHardLink(FatEntry link) {
    var path = Encoding.Latin1.GetString(_fat.Extract(link)).TrimEnd('\0').TrimStart('/');
    var dosPath = "";
    FatEntry? current = null;
    foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
      if (!_dos.TryGetValue(dosPath, out var dir)) return null;
      FatEntry? next;
      if (dir.TryGetValue(EmdName, out var emd)) {
        if (!_emd.TryGetValue(dosPath, out var records)) _emd[dosPath] = records = ParseEmd(_fat.Extract(emd)).Select(r => (r, DosFile(dir, r))).ToList();
        next = records.FirstOrDefault(x => x.Rec.Name == part).Dos;
      } else {
        next = dir.GetValueOrDefault(part);
      }
      if (next == null) return null;
      current = next;
      dosPath = next.Name;
    }
    return current is { IsDirectory: false } ? current : null;
  }

  /// <summary>Returns the bytes of <paramref name="entry"/>: file data, or a symbolic link's target.</summary>
  public byte[] Extract(UmsdosEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.IsSymlink) return Encoding.Latin1.GetBytes(entry.LinkTarget ?? "");
    return _data.TryGetValue(entry.Name, out var dos) ? _fat.Extract(dos) : [];
  }

  public void Dispose() => _fat.Dispose();
}
