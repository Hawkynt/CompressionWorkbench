using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Iso;

/// <summary>
/// In-place editing of an ISO 9660 image at any depth: files and folders are added to or
/// removed from any directory, and the primary tree (with Rock Ridge when the image uses it)
/// and the Joliet tree are kept describing the same volume.
/// </summary>
/// <remarks>
/// <para>File data is never moved: a new file's data is appended after the volume space, a
/// removed file's sectors are zeroed. Every directory record that is not edited is kept byte
/// for byte. A directory whose records change is written back sorted by identifier
/// (ECMA-119 9.3); one that no longer fits its extent moves to new sectors after the volume
/// space, and every pointer to it — its parent's record, its own "." and its children's "..",
/// the path tables, the root record of the volume descriptors — is updated. The path tables
/// (ECMA-119 9.4) are written anew from the trees, in place while they fit, else after the
/// volume space. El Torito records and everything else outside the directory hierarchy stay
/// as they are.</para>
/// <para>New Rock Ridge entries carry PX, TF and NM (RRIP 1.10), with the name in a
/// continuation area (SUSP CE) when it does not fit the record, as <see cref="IsoWriter"/>
/// writes them. New ISO 9660 identifiers are made unique within their directory.</para>
/// <para>Refused, with the image left as it was: data stored after the volume space (a
/// hybrid image's partitions or another session), a Joliet tree that has no folder matching
/// the one being edited, a path through something that is not a folder, and interleaved or
/// multi-extent directories.</para>
/// </remarks>
internal static class IsoTreeEditor {
  private const int SectorSize = 2048;
  private const int PvdLba = 16;

  // ── Model ────────────────────────────────────────────────────────────────

  private sealed class Dir {
    public int Lba;
    public int Length;
    public int OldLba;
    public int OldLength;
    public Dir? Parent;
    public Rec? InParent;
    public byte[] Dot = [];
    public byte[] DotDot = [];
    public readonly List<Rec> Records = [];
    public bool Dirty;
    public Dir? Counterpart;      // the matching directory of the other tree
  }

  private sealed class Rec {
    public required byte[] Raw;
    public required string Name;   // logical name as a reader shows it
    public required byte[] Identifier;
    public Dir? Child;
    public bool IsDirectory => (this.Raw[25] & 0x02) != 0;
    public int DataLba => (int)BinaryPrimitives.ReadUInt32LittleEndian(this.Raw.AsSpan(2));
    public int DataLength => (int)BinaryPrimitives.ReadUInt32LittleEndian(this.Raw.AsSpan(10));
  }

  private sealed class Volume {
    public required Stream Image;
    public required byte[] Pvd;
    public byte[]? Svd;
    public int SvdLba = -1;
    public required Dir Root;
    public Dir? JolietRoot;
    public bool RockRidge;
    public int VolumeSpace;
    public readonly List<(long Offset, int Length)> Wipe = [];
    public readonly List<(int Lba, int Sectors)> FreedData = [];
    public readonly HashSet<int> Protected = [];
    public readonly List<(int Lba, byte[] Bytes)> Appended = [];

    public int Allocate(int sectors) {
      var lba = this.VolumeSpace;
      this.VolumeSpace += sectors;
      return lba;
    }
  }

  // ── Public operations ───────────────────────────────────────────────────

  /// <summary>True when the image uses Rock Ridge (SUSP "SP" in the root's first record).</summary>
  public static bool UsesRockRidge(Stream image) {
    var pvd = ReadSector(image, PvdLba);
    var rootLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 2));
    var root = ReadSector(image, rootLba);
    return HasSuspSp(root, 0);
  }

  /// <summary>Adds (or replaces) a file at <paramref name="path"/>, creating missing folders.</summary>
  public static void AddFile(Stream image, string path, byte[] data, int? mode = null, DateTime? modified = null) {
    ArgumentNullException.ThrowIfNull(data);
    var segments = Segments(path);
    if (segments.Length == 0) throw new ArgumentException("ISO 9660: a file needs a name.", nameof(path));
    var vol = Load(image);
    RemoveNamed(vol, segments, mustExist: false, filesOnly: true);
    var (dir, jdir) = EnsureFolders(vol, segments[..^1], mode: null, modified);

    var sectors = Math.Max(1, (data.Length + SectorSize - 1) / SectorSize);
    var lba = vol.Allocate(sectors);
    var padded = new byte[sectors * SectorSize];
    data.CopyTo(padded, 0);
    vol.Appended.Add((lba, padded));
    var when = modified ?? DateTime.UtcNow;

    dir.Records.Add(NewRecord(vol, dir, segments[^1], isDirectory: false, lba, data.Length, mode, when, joliet: false));
    dir.Dirty = true;
    if (jdir != null) {
      jdir.Records.Add(NewRecord(vol, jdir, segments[^1], isDirectory: false, lba, data.Length, mode, when, joliet: true));
      jdir.Dirty = true;
    }
    Commit(vol);
  }

  /// <summary>Adds a folder (and any missing parents); an existing one is left as it is.</summary>
  public static void AddDirectory(Stream image, string path, int? mode = null, DateTime? modified = null) {
    var segments = Segments(path);
    if (segments.Length == 0) return;
    var vol = Load(image);
    var before = vol.VolumeSpace;
    EnsureFolders(vol, segments, mode, modified);
    if (vol.VolumeSpace != before) Commit(vol);
  }

  /// <summary>
  /// Removes the file or folder (with everything in it) at <paramref name="path"/>; false when
  /// there is nothing there.
  /// </summary>
  public static bool Remove(Stream image, string path) {
    var segments = Segments(path);
    if (segments.Length == 0) return false;
    var vol = Load(image);
    if (!RemoveNamed(vol, segments, mustExist: false, filesOnly: false)) return false;
    Commit(vol);
    return true;
  }

  // ── Editing the model ──────────────────────────────────────────────────

  private static string[] Segments(string path)
    => (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

  /// <summary>Removes the entry at the path from both trees; false when it is not there.</summary>
  private static bool RemoveNamed(Volume vol, string[] segments, bool mustExist, bool filesOnly) {
    var (dir, jdir) = ResolveFolders(vol, segments[..^1]);
    if (dir == null) {
      if (mustExist) throw new FileNotFoundException($"ISO 9660: '{string.Join('/', segments)}' does not exist.");
      return false;
    }
    var shown = ShownDirectory(vol, dir, jdir);
    var hits = Find(shown, segments[^1]);
    if (hits.Count == 0) return false;
    if (filesOnly && hits.Any(h => h.IsDirectory))
      throw new IOException($"ISO 9660: '{string.Join('/', segments)}' is a folder.");

    var other = shown == dir ? jdir : dir;
    foreach (var hit in hits) {
      // The other tree's entry: the matching folder, or the file record sharing the data.
      Rec? twin = null;
      if (other != null) {
        twin = hit.IsDirectory ? hit.Child?.Counterpart?.InParent : FileTwin(other, hit);
        if (twin == null && (hit.IsDirectory || hit.DataLength > 0))
          throw new NotSupportedException(
            $"ISO 9660: the {(other == jdir ? "Joliet" : "primary")} tree has no entry matching '{string.Join('/', segments)}'.");
      }
      foreach (var (parent, rec) in new[] { (shown, hit), (other, twin) }) {
        if (parent == null || rec == null) continue;
        parent.Records.Remove(rec);
        parent.Dirty = true;
        if (rec.Child != null) WipeTree(vol, rec.Child);
        else WipeData(vol, rec);
      }
    }
    return true;
  }

  /// <summary>The other tree's file record that shares this one's data (and, when several do, its name).</summary>
  private static Rec? FileTwin(Dir other, Rec hit) {
    var same = other.Records.Where(r => !r.IsDirectory && r.DataLba == hit.DataLba && r.DataLength == hit.DataLength).ToList();
    if (same.Count <= 1) return same.FirstOrDefault();
    return same.FirstOrDefault(r => Find(other, hit.Name).Contains(r)
      || string.Equals(r.Name, IsoWriter.TruncateJolietName(hit.Name), StringComparison.Ordinal));
  }

  /// <summary>Zeroes a removed folder's extent and its files' data.</summary>
  private static void WipeTree(Volume vol, Dir dir) {
    vol.Wipe.Add(((long)dir.OldLba * SectorSize, RoundUp(dir.OldLength)));
    foreach (var r in dir.Records) {
      if (r.Child != null) WipeTree(vol, r.Child);
      else WipeData(vol, r);
    }
  }

  private static void WipeData(Volume vol, Rec rec) {
    if (rec.DataLength > 0 && !rec.IsDirectory)
      vol.FreedData.Add((rec.DataLba, (rec.DataLength + SectorSize - 1) / SectorSize));
  }

  /// <summary>The primary and Joliet folders at the path; (null, null) when a part is missing.</summary>
  private static (Dir? Primary, Dir? Joliet) ResolveFolders(Volume vol, string[] segments) {
    Dir? dir = vol.Root;
    Dir? jdir = vol.JolietRoot;
    foreach (var segment in segments) {
      var shown = ShownDirectory(vol, dir!, jdir);
      var hit = Find(shown, segment).FirstOrDefault(r => r.IsDirectory) ?? Find(shown, segment).FirstOrDefault();
      if (hit == null) return (null, null);
      if (!hit.IsDirectory || hit.Child == null)
        throw new IOException($"ISO 9660: '{segment}' is not a folder.");
      if (shown == dir) {
        dir = hit.Child;
        jdir = jdir == null ? null : hit.Child.Counterpart
          ?? throw new NotSupportedException($"ISO 9660: the Joliet tree has no folder matching '{segment}'.");
      } else {
        jdir = hit.Child;
        dir = hit.Child.Counterpart
          ?? throw new NotSupportedException($"ISO 9660: the primary tree has no folder matching '{segment}'.");
      }
    }
    return (dir, jdir);
  }

  /// <summary>Creates the folders of the path that are missing; returns the innermost pair.</summary>
  private static (Dir Primary, Dir? Joliet) EnsureFolders(Volume vol, string[] segments, int? mode, DateTime? modified) {
    var dir = vol.Root;
    var jdir = vol.JolietRoot;
    for (var i = 0; i < segments.Length; ++i) {
      var (found, jfound) = ResolveFolders(vol, segments[..(i + 1)]);
      if (found != null) {
        dir = found;
        jdir = jfound;
        continue;
      }
      var when = modified ?? DateTime.UtcNow;
      var child = NewDirectory(vol, dir, segments[i], mode, when, joliet: false);
      if (jdir != null) {
        var jchild = NewDirectory(vol, jdir, segments[i], mode, when, joliet: true);
        child.Counterpart = jchild;
        jchild.Counterpart = child;
        jdir = jchild;
      }
      dir = child;
    }
    return (dir, jdir);
  }

  private static Dir NewDirectory(Volume vol, Dir parent, string name, int? mode, DateTime when, bool joliet) {
    var rr = vol.RockRidge && !joliet;
    var dir = new Dir { Parent = parent, Lba = vol.Allocate(1), Length = SectorSize, Dirty = true };
    dir.OldLba = dir.Lba;
    dir.OldLength = 0;
    dir.Dot = BuildRecord([0], dir.Lba, SectorSize, 0x02, rr ? IsoWriter.RockRidgeFields(mode, when, true, null, 1, null) : null, when);
    dir.DotDot = BuildRecord([1], parent.Lba, parent.Length, 0x02, rr ? IsoWriter.RockRidgeFields(null, when, true, null, 1, null) : null, when);
    var rec = NewRecord(vol, parent, name, isDirectory: true, dir.Lba, SectorSize, mode, when, joliet);
    rec.Child = dir;
    dir.InParent = rec;
    parent.Records.Add(rec);
    parent.Dirty = true;
    return dir;
  }

  private static Rec NewRecord(Volume vol, Dir dir, string name, bool isDirectory, int lba, int length,
      int? mode, DateTime when, bool joliet) {
    byte[] identifier;
    byte[]? systemUse = null;
    if (joliet) {
      var jname = UniqueName(IsoWriter.TruncateJolietName(name), dir, joliet: true, maxLength: 64);
      identifier = Encoding.BigEndianUnicode.GetBytes(jname);
    } else {
      string id;
      int maxLength;
      if (vol.RockRidge) {
        id = IsoWriter.DChars(name, keepDot: !isDirectory);
        maxLength = 30;
      } else if (isDirectory) {
        id = IsoWriter.DChars(name, keepDot: false);
        if (id.Length > 8) id = id[..8];
        maxLength = 8;
      } else {
        id = Encoding.ASCII.GetString(IsoModifier.BuildIsoIdentifier(name))[..^2];
        maxLength = 8;
      }
      id = UniqueName(id, dir, joliet: false, maxLength);
      identifier = Encoding.ASCII.GetBytes(isDirectory ? id : id + ";1");
      if (vol.RockRidge) {
        IsoWriter.ContinuationArea? continuation = null;
        if (IsoWriter.NameContinuation(name, identifier.Length) is { } area) {
          var areaLba = vol.Allocate(1);
          var sector = new byte[SectorSize];
          area.CopyTo(sector, 0);
          vol.Appended.Add((areaLba, sector));
          continuation = new IsoWriter.ContinuationArea(areaLba, 0, area.Length);
        }
        systemUse = IsoWriter.RockRidgeFields(mode, when, isDirectory, name, identifier.Length, continuation);
      }
    }
    return new Rec {
      Raw = BuildRecord(identifier, lba, length, isDirectory ? (byte)0x02 : (byte)0x00, systemUse, when),
      Name = name,
      Identifier = identifier,
    };
  }

  /// <summary>
  /// An identifier not yet used in the folder: "~N" replaces the end of the stem when needed.
  /// <paramref name="maxLength"/> bounds the stem (8, an 8.3 name) or the whole name (30 for
  /// a Level 2 identifier, 64 for a Joliet name).
  /// </summary>
  private static string UniqueName(string id, Dir dir, bool joliet, int maxLength) {
    var taken = dir.Records.Select(r => joliet
      ? StripVersion(Encoding.BigEndianUnicode.GetString(r.Identifier))
      : StripVersion(Encoding.ASCII.GetString(r.Identifier))).ToHashSet(joliet ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    var dot = id.LastIndexOf('.');
    var stem = dot > 0 ? id[..dot] : id;
    var ext = dot > 0 ? id[dot..] : "";
    var stemRoom = maxLength == 8 ? 8 : maxLength - ext.Length;
    var candidate = id;
    for (var n = 1; taken.Contains(candidate); ++n) {
      var suffix = $"~{n}";
      candidate = stem[..Math.Min(stem.Length, Math.Max(1, stemRoom - suffix.Length))] + suffix + ext;
    }
    return candidate;
  }

  private static byte[] BuildRecord(byte[] identifier, int lba, int size, byte flags, byte[]? systemUse, DateTime when) {
    var buffer = new byte[IsoWriter.RecordLength(identifier.Length, systemUse?.Length ?? 0)];
    IsoWriter.WriteDirectoryRecord(buffer, 0, lba, size, flags, identifier, systemUse, when);
    return buffer;
  }

  /// <summary>The tree a reader shows names from: Rock Ridge primary, else Joliet, else primary.</summary>
  private static Dir ShownDirectory(Volume vol, Dir primary, Dir? joliet)
    => !vol.RockRidge && joliet != null ? joliet : primary;

  /// <summary>
  /// The entries named <paramref name="name"/>: those whose shown name matches exactly, else
  /// the single one matching regardless of case (or by its ISO 9660 identifier without the
  /// ";1" version) — never several that only differ in case.
  /// </summary>
  private static List<Rec> Find(Dir dir, string name) {
    var exact = dir.Records.Where(r => string.Equals(r.Name, name, StringComparison.Ordinal)).ToList();
    if (exact.Count > 0) return exact;
    var bare = StripVersion(name).TrimEnd('.');
    var loose = dir.Records.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)
      || string.Equals(StripIdentifier(r.Identifier), bare, StringComparison.OrdinalIgnoreCase)).ToList();
    return loose.Count == 1 ? loose : [];
  }


  // ── Loading ──────────────────────────────────────────────────────────────

  private static Volume Load(Stream image) {
    if (!image.CanSeek || !image.CanRead || !image.CanWrite)
      throw new ArgumentException("Image stream must be readable, writable, and seekable.", nameof(image));
    var pvd = ReadSector(image, PvdLba);
    if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
      throw new InvalidDataException("ISO 9660: no primary volume descriptor at sector 16.");
    var volumeSpace = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(80));

    // Anything stored right after the volume space would be overwritten by appended sectors.
    var tail = (long)volumeSpace * SectorSize;
    if (tail < image.Length) {
      var probe = new byte[(int)Math.Min(64L * SectorSize, image.Length - tail)];
      image.Position = tail;
      image.ReadExactly(probe);
      if (probe.AsSpan().ContainsAnyExcept((byte)0))
        throw new NotSupportedException(
          "ISO 9660: the image carries data right after its volume space; appending to it would overwrite that data.");
    }

    using var data = new ImageAccessor(image, leaveOpen: true);
    var rootLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 2));
    var rootLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 10));
    var rockRidge = HasSuspSp(ReadSector(image, rootLba), 0);
    var visited = new HashSet<int>();
    var root = LoadDirectory(image, data, rootLba, rootLen, null, joliet: false, rockRidge, visited, 0);

    var vol = new Volume { Image = image, Pvd = pvd, Root = root, RockRidge = rockRidge, VolumeSpace = volumeSpace };
    for (var lba = PvdLba + 1; lba < 64 && (long)(lba + 1) * SectorSize <= image.Length; ++lba) {
      var sector = ReadSector(image, lba);
      if (sector[0] == 0xFF) break;
      if (Encoding.ASCII.GetString(sector, 1, 5) != "CD001") continue;
      if (sector[0] == 0 && Encoding.ASCII.GetString(sector, 7, 23) == "EL TORITO SPECIFICATION") {
        ProtectBootImages(vol, BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(0x47)));
        continue;
      }
      if (sector[0] != 2 || vol.Svd != null) continue;
      if (sector[88] != 0x25 || sector[89] != 0x2F || sector[90] is not (0x40 or 0x43 or 0x45)) continue;
      vol.Svd = sector;
      vol.SvdLba = lba;
      var jLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(156 + 2));
      var jLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(156 + 10));
      vol.JolietRoot = LoadDirectory(image, data, jLba, jLen, null, joliet: true, rockRidge: false, visited, 0);
      vol.Root.Counterpart = vol.JolietRoot;
      vol.JolietRoot.Counterpart = vol.Root;
      Pair(vol.Root, vol.JolietRoot);
    }
    return vol;
  }

  /// <summary>
  /// The El Torito boot catalog and the boot images it names are never zeroed, even when the
  /// files that show them ("boot.cat", the loader) are removed, so the disc still boots.
  /// </summary>
  private static void ProtectBootImages(Volume vol, uint catalogLba) {
    if (catalogLba == 0 || (long)(catalogLba + 1) * SectorSize > vol.Image.Length) return;
    vol.Protected.Add((int)catalogLba);
    var catalog = ReadSector(vol.Image, (int)catalogLba);
    // Entry 0 validates; then initial/section entries (0x88 bootable, 0x00 not) carry the
    // image's start sector at offset 8, section headers are 0x90/0x91, extensions 0x44.
    for (var at = 32; at + 32 <= SectorSize; at += 32) {
      if (catalog[at] is not (0x88 or 0x00)) continue;
      var rba = BinaryPrimitives.ReadUInt32LittleEndian(catalog.AsSpan(at + 8));
      if (rba != 0) vol.Protected.Add((int)rba);
    }
  }

  /// <summary>Matches each primary folder with the Joliet folder of the same name.</summary>
  private static void Pair(Dir primary, Dir joliet) {
    foreach (var rec in primary.Records.Where(r => r.Child != null)) {
      var jname = IsoWriter.TruncateJolietName(rec.Name);
      var match = joliet.Records.FirstOrDefault(j => j.Child != null && j.Child.Counterpart == null && j.Name == jname)
        ?? joliet.Records.FirstOrDefault(j => j.Child != null && j.Child.Counterpart == null
          && string.Equals(IsoWriter.DChars(j.Name, keepDot: false), StripIdentifier(rec.Identifier), StringComparison.Ordinal));
      if (match?.Child == null) continue;
      rec.Child!.Counterpart = match.Child;
      match.Child.Counterpart = rec.Child;
      Pair(rec.Child, match.Child);
    }
  }

  private static Dir LoadDirectory(Stream image, ImageAccessor data, int lba, int length, Dir? parent,
      bool joliet, bool rockRidge, HashSet<int> visited, int depth) {
    if (depth > 64 || !visited.Add(lba))
      throw new InvalidDataException("ISO 9660: the directory hierarchy loops or is too deep to edit.");
    var dir = new Dir { Lba = lba, Length = length, OldLba = lba, OldLength = length, Parent = parent };
    var sectors = (length + SectorSize - 1) / SectorSize;
    for (var s = 0; s < sectors; ++s) {
      var sector = ReadSector(image, lba + s);
      var pos = 0;
      while (pos + 34 <= SectorSize) {
        var len = sector[pos];
        if (len == 0) break;
        if (len < 34 || pos + len > SectorSize)
          throw new InvalidDataException("ISO 9660: a malformed directory record; the image is not edited.");
        var raw = sector.AsSpan(pos, len).ToArray();
        var idLen = raw[32];
        var identifier = raw.AsSpan(33, idLen).ToArray();
        if ((raw[25] & 0x80) != 0 && (raw[25] & 0x02) != 0)
          throw new NotSupportedException("ISO 9660: a multi-extent directory cannot be edited in place.");
        if (raw[26] != 0 || raw[27] != 0)
          throw new NotSupportedException("ISO 9660: an interleaved entry cannot be edited in place.");
        if (idLen == 1 && identifier[0] is 0 or 1) {
          if (identifier[0] == 0 && dir.Dot.Length == 0) dir.Dot = raw;
          else if (identifier[0] == 1 && dir.DotDot.Length == 0) dir.DotDot = raw;
          pos += len;
          continue;
        }
        string name;
        if (joliet) {
          name = Encoding.BigEndianUnicode.GetString(identifier);
        } else {
          name = StripIdentifier(identifier);
          if (rockRidge) {
            var suStart = 33 + idLen + ((idLen & 1) == 0 ? 1 : 0);
            var abs = (long)(lba + s) * SectorSize + pos;
            if (suStart < len && IsoRockRidge.Parse(data, abs + suStart, abs + len, SectorSize) is { } rr) {
              if (rr.Relocated || rr.ChildLink != null)
                throw new NotSupportedException("ISO 9660: the image relocates deep folders (Rock Ridge RE/CL); it is not edited in place.");
              if (rr.Name is { Length: > 0 } rrName) name = rrName;
            }
          }
        }
        var rec = new Rec { Raw = raw, Name = StripVersion(name).TrimEnd('.'), Identifier = identifier };
        if (rec.IsDirectory)
          rec.Child = LoadDirectory(image, data, rec.DataLba, rec.DataLength, dir, joliet, rockRidge, visited, depth + 1);
        if (rec.Child != null) rec.Child.InParent = rec;
        dir.Records.Add(rec);
        pos += len;
      }
    }
    return dir;
  }

  // ── Writing ──────────────────────────────────────────────────────────────

  private static void Commit(Volume vol) {
    var trees = new List<Dir> { vol.Root };
    if (vol.JolietRoot != null) trees.Add(vol.JolietRoot);

    // Directories whose records changed are packed; one that outgrows its extent moves to
    // new sectors, which makes its parent (record) and children ("..") dirty too.
    for (var round = 0; round < 64; ++round) {
      var moved = false;
      foreach (var dir in trees.SelectMany(AllDirectories).Where(d => d.Dirty).ToList()) {
        var needed = PackedLength(dir);
        if (needed <= RoundUp(dir.Length)) continue;
        var sectors = needed / SectorSize;
        if (dir.OldLength > 0 && dir.Lba == dir.OldLba)
          vol.Wipe.Add(((long)dir.OldLba * SectorSize, RoundUp(dir.OldLength)));
        dir.Lba = vol.Allocate(sectors);
        dir.Length = needed;
        if (dir.Parent != null) dir.Parent.Dirty = true;
        foreach (var child in dir.Records.Where(r => r.Child != null)) child.Child!.Dirty = true;
        moved = true;
      }
      if (!moved) break;
    }

    // Point every record at where its directory now is.
    foreach (var dir in trees.SelectMany(AllDirectories)) {
      var parent = dir.Parent ?? dir;
      SetExtent(dir.Dot, dir.Lba, dir.Length);
      SetExtent(dir.DotDot, parent.Lba, parent.Length);
      foreach (var rec in dir.Records.Where(r => r.Child != null))
        SetExtent(rec.Raw, rec.Child!.Lba, rec.Child.Length);
    }

    // Zero what was freed first (nothing is allocated into freed sectors, so no wipe can hit
    // the new layout). File data a remaining record still points at — a hard link or a
    // deduplicated extent — is kept.
    var image = vol.Image;
    var referenced = trees.SelectMany(AllDirectories).SelectMany(d => d.Records)
      .Where(r => !r.IsDirectory).Select(r => r.DataLba).ToHashSet();
    foreach (var (lba, sectors) in vol.FreedData)
      if (!referenced.Contains(lba) && !vol.Protected.Contains(lba))
        vol.Wipe.Add(((long)lba * SectorSize, sectors * SectorSize));
    foreach (var (offset, length) in vol.Wipe) {
      if (offset >= image.Length) continue;
      image.Position = offset;
      image.Write(new byte[(int)Math.Min(length, image.Length - offset)]);
    }

    var requiredLength = (long)vol.VolumeSpace * SectorSize;
    if (image.Length < requiredLength) image.SetLength(requiredLength);
    foreach (var (lba, bytes) in vol.Appended) {
      image.Position = (long)lba * SectorSize;
      image.Write(bytes);
    }
    foreach (var dir in trees.SelectMany(AllDirectories).Where(d => d.Dirty || d.Lba != d.OldLba)) {
      image.Position = (long)dir.Lba * SectorSize;
      image.Write(Pack(dir));
    }

    // Path tables and the volume descriptors' root records and sizes.
    WritePathTables(vol, vol.Pvd, vol.Root, joliet: false);
    if (vol.Svd != null && vol.JolietRoot != null) WritePathTables(vol, vol.Svd, vol.JolietRoot, joliet: true);
    if (image.Length < (long)vol.VolumeSpace * SectorSize) image.SetLength((long)vol.VolumeSpace * SectorSize);
    foreach (var (descriptor, lba, root) in new[] { (vol.Pvd, PvdLba, vol.Root), (vol.Svd, vol.SvdLba, vol.JolietRoot) }) {
      if (descriptor == null || root == null) continue;
      BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(80), (uint)vol.VolumeSpace);
      BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(84), (uint)vol.VolumeSpace);
      SetExtent(descriptor.AsSpan(156), root.Lba, root.Length);
      image.Position = (long)lba * SectorSize;
      image.Write(descriptor);
    }
    image.Flush();
  }

  private static IEnumerable<Dir> AllDirectories(Dir root) {
    var queue = new Queue<Dir>();
    queue.Enqueue(root);
    while (queue.Count > 0) {
      var dir = queue.Dequeue();
      yield return dir;
      foreach (var rec in dir.Records)
        if (rec.Child != null) queue.Enqueue(rec.Child);
    }
  }

  private static int RoundUp(int length) => Math.Max(SectorSize, (length + SectorSize - 1) / SectorSize * SectorSize);

  /// <summary>Records in identifier order (ECMA-119 9.3), "." and ".." first.</summary>
  private static List<byte[]> Ordered(Dir dir) {
    var list = new List<byte[]> { dir.Dot, dir.DotDot };
    list.AddRange(dir.Records.OrderBy(r => r.Identifier, IdentifierComparer.Instance).Select(r => r.Raw));
    return list;
  }

  private static int PackedLength(Dir dir) {
    var size = 0;
    foreach (var raw in Ordered(dir)) {
      if (size % SectorSize + raw.Length > SectorSize) size += SectorSize - size % SectorSize;
      size += raw.Length;
    }
    return RoundUp(size);
  }

  private static byte[] Pack(Dir dir) {
    var bytes = new byte[RoundUp(dir.Length)];
    var pos = 0;
    foreach (var raw in Ordered(dir)) {
      if (pos % SectorSize + raw.Length > SectorSize) pos += SectorSize - pos % SectorSize;
      raw.CopyTo(bytes, pos);
      pos += raw.Length;
    }
    return bytes;
  }

  private static void SetExtent(Span<byte> record, int lba, int length) {
    BinaryPrimitives.WriteUInt32LittleEndian(record[2..], (uint)lba);
    BinaryPrimitives.WriteUInt32BigEndian(record[6..], (uint)lba);
    BinaryPrimitives.WriteUInt32LittleEndian(record[10..], (uint)length);
    BinaryPrimitives.WriteUInt32BigEndian(record[14..], (uint)length);
  }

  /// <summary>
  /// The L and M path tables (ECMA-119 9.4) written from the tree: by level, then parent
  /// number, then identifier. In place while they fit their sectors, else after the volume space.
  /// </summary>
  private static void WritePathTables(Volume vol, byte[] descriptor, Dir root, bool joliet) {
    var ordered = new List<(Dir Dir, byte[] Id, int Parent)>();
    var numbers = new Dictionary<Dir, int>();
    var level = new List<Dir> { root };
    numbers[root] = 1;
    ordered.Add((root, [0], 1));
    while (level.Count > 0) {
      var next = new List<Dir>();
      foreach (var dir in level)
        foreach (var rec in dir.Records.Where(r => r.Child != null).OrderBy(r => r.Identifier, IdentifierComparer.Instance)) {
          ordered.Add((rec.Child!, rec.Identifier, numbers[dir]));
          numbers[rec.Child!] = ordered.Count;
          next.Add(rec.Child!);
        }
      level = next;
    }

    byte[] Table(bool littleEndian) {
      using var ms = new MemoryStream();
      foreach (var (dir, id, parent) in ordered) {
        var entry = new byte[8 + id.Length + (id.Length & 1)];
        entry[0] = (byte)id.Length;
        if (littleEndian) {
          BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(2), (uint)dir.Lba);
          BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), (ushort)parent);
        } else {
          BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(2), (uint)dir.Lba);
          BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(6), (ushort)parent);
        }
        id.CopyTo(entry, 8);
        ms.Write(entry);
      }
      return ms.ToArray();
    }

    var little = Table(littleEndian: true);
    var big = Table(littleEndian: false);
    var oldSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(132));
    var capacity = RoundUp(oldSize);
    var lLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(140));
    var mLba = (int)BinaryPrimitives.ReadUInt32BigEndian(descriptor.AsSpan(148));
    if (little.Length > capacity || lLba == 0 || mLba == 0) {
      var sectors = RoundUp(little.Length) / SectorSize;
      lLba = vol.Allocate(sectors);
      mLba = vol.Allocate(sectors);
      capacity = sectors * SectorSize;
      // The optional copies would describe the old tree; they are dropped.
      BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(144), 0);
      BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(152), 0);
    } else {
      foreach (var (offset, littleEndian) in new[] { (144, true), (152, false) }) {
        var optional = littleEndian
          ? (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(offset))
          : (int)BinaryPrimitives.ReadUInt32BigEndian(descriptor.AsSpan(offset));
        if (optional == 0) continue;
        WriteAt(vol.Image, optional, littleEndian ? little : big, capacity);
      }
    }
    WriteAt(vol.Image, lLba, little, capacity);
    WriteAt(vol.Image, mLba, big, capacity);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(132), (uint)little.Length);
    BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(136), (uint)little.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(140), (uint)lLba);
    BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(148), (uint)mLba);
  }

  private static void WriteAt(Stream image, int lba, byte[] table, int capacity) {
    var buffer = new byte[capacity];
    table.CopyTo(buffer, 0);
    if (image.Length < (long)lba * SectorSize + capacity) image.SetLength((long)lba * SectorSize + capacity);
    image.Position = (long)lba * SectorSize;
    image.Write(buffer);
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  private sealed class IdentifierComparer : IComparer<byte[]> {
    public static readonly IdentifierComparer Instance = new();
    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
  }

  private static bool HasSuspSp(byte[] sector, int pos) {
    var len = sector[pos];
    if (len < 40) return false;
    var idLen = sector[pos + 32];
    var su = pos + 33 + idLen + ((idLen & 1) == 0 ? 1 : 0);
    return su + 7 <= pos + len && sector[su] == 'S' && sector[su + 1] == 'P' && sector[su + 4] == 0xBE && sector[su + 5] == 0xEF;
  }

  private static string StripIdentifier(byte[] identifier) => StripVersion(Encoding.ASCII.GetString(identifier)).TrimEnd('.');

  private static string StripVersion(string s) {
    var semi = s.IndexOf(';');
    return semi >= 0 ? s[..semi] : s;
  }

  private static byte[] ReadSector(Stream image, int lba) {
    var buffer = new byte[SectorSize];
    image.Position = (long)lba * SectorSize;
    image.ReadExactly(buffer);
    return buffer;
  }
}
