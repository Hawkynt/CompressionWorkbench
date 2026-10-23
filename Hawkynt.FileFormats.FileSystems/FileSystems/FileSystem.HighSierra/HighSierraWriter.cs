using System.Buffers.Binary;
using System.Text;
using static FileSystem.HighSierra.HighSierraLayout;

namespace FileSystem.HighSierra;

/// <summary>
/// Writes a High Sierra volume: system area, one Standard File Structure Volume
/// Descriptor, a terminator, the two mandatory Path Tables (LSB- and MSB-first),
/// the directories and the file data, with 2048-byte logical blocks.
/// </summary>
/// <remarks>
/// Identifiers must fit the working paper's character sets: lower-case letters are
/// folded to capitals (the d-characters have none), anything else outside
/// <c>A-Z 0-9 _</c> — plus one FULL STOP in a file name — is refused rather than
/// rewritten, as are two names that fold to one and trees deeper than eight levels (8.1.1).
/// </remarks>
public sealed class HighSierraWriter {
  /// <summary>Directory levels the paper allows, the root counting as level 1 (8.1.1).</summary>
  public const int MaxLevels = 8;

  private sealed class Node(string id, bool isDir) {
    public string Id { get; } = id;
    public bool IsDir { get; } = isDir;
    public byte[] Data { get; set; } = [];
    public DateTime Time { get; set; }
    public SortedList<string, Node> Children { get; } = new(IdComparer.Instance);
    public Node? Parent { get; set; }
    public uint Lbn { get; set; }
    public uint Length { get; set; }
    public ushort Number { get; set; }
  }

  /// <summary>Orders identifiers as 8.2.1 / 8.3.2 do: SPACE-padded byte comparison, name before extension.</summary>
  private sealed class IdComparer : IComparer<string> {
    public static readonly IdComparer Instance = new();
    public int Compare(string? a, string? b) {
      var (an, ae) = Split(a!);
      var (bn, be) = Split(b!);
      var c = Padded(an, bn);
      return c != 0 ? c : Padded(ae, be);
    }
    private static (string Name, string Ext) Split(string id) {
      var dot = id.IndexOf('.');
      return dot < 0 ? (id, "") : (id[..dot], id[(dot + 1)..]);
    }
    private static int Padded(string a, string b) {
      var n = Math.Max(a.Length, b.Length);
      for (var i = 0; i < n; ++i) {
        var ca = i < a.Length ? a[i] : ' ';
        var cb = i < b.Length ? b[i] : ' ';
        if (ca != cb) return ca.CompareTo(cb);
      }
      return 0;
    }
  }

  private readonly Node _root = new("", isDir: true);

  /// <summary>Volume Identifier (d-characters, 32 bytes).</summary>
  public string VolumeId { get; set; } = "CDROM";

  /// <summary>Recording time used for the volume and for inputs without one.</summary>
  public DateTime Now { get; set; } = DateTime.UtcNow;

  /// <summary>Adds a file; missing parent directories are created.</summary>
  public void AddFile(string path, byte[] data, DateTime? modified = null) {
    ArgumentNullException.ThrowIfNull(data);
    var (parent, leaf) = this.Resolve(path);
    var id = FileIdentifier(leaf, path);
    if (parent.Children.ContainsKey(id))
      throw new NotSupportedException($"High Sierra: '{path}' folds to '{id}', which is already taken in its directory.");
    parent.Children.Add(id, new Node(id, isDir: false) { Data = data, Time = modified ?? this.Now, Parent = parent });
  }

  /// <summary>Adds an (empty) directory; missing parents are created.</summary>
  public void AddDirectory(string path, DateTime? modified = null) {
    var (parent, leaf) = this.Resolve(path);
    var id = DirectoryIdentifier(leaf, path);
    if (parent.Children.TryGetValue(id, out var existing)) {
      if (!existing.IsDir) throw new NotSupportedException($"High Sierra: '{path}' is both a file and a directory.");
      return;
    }
    parent.Children.Add(id, new Node(id, isDir: true) { Time = modified ?? this.Now, Parent = parent });
  }

  private (Node Parent, string Leaf) Resolve(string path) {
    ArgumentNullException.ThrowIfNull(path);
    var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) throw new NotSupportedException("High Sierra: an empty path cannot be stored.");
    if (parts.Length > MaxLevels)
      throw new NotSupportedException($"High Sierra allows {MaxLevels} directory levels; '{path}' needs more.");
    var node = this._root;
    foreach (var part in parts[..^1]) {
      var id = DirectoryIdentifier(part, path);
      if (!node.Children.TryGetValue(id, out var next)) {
        next = new Node(id, isDir: true) { Time = this.Now, Parent = node };
        node.Children.Add(id, next);
      } else if (!next.IsDir) {
        throw new NotSupportedException($"High Sierra: '{part}' in '{path}' is a file, not a directory.");
      }
      node = next;
    }
    return (node, parts[^1]);
  }

  private static string DirectoryIdentifier(string name, string path) {
    var id = name.ToUpperInvariant();
    if (id.Length == 0 || id.Length > 31 || !id.All(IsDCharacter))
      throw new NotSupportedException($"High Sierra directory identifiers are 1-31 d-characters (A-Z, 0-9, _); '{name}' in '{path}' is not.");
    return id;
  }

  private static string FileIdentifier(string name, string path) {
    var id = name.ToUpperInvariant();
    var dot = id.IndexOf('.');
    var ok = id.Length is > 0 and <= 30
      && id.Count(c => c == '.') <= 1
      && id.All(c => c == '.' || IsDCharacter(c))
      && !(dot == 0 && id.Length == 1);
    if (!ok)
      throw new NotSupportedException($"High Sierra file identifiers are d-characters (A-Z, 0-9, _) with at most one FULL STOP, 30 characters at most; '{name}' in '{path}' is not.");
    return dot < 0 ? id + "." : id;
  }

  /// <summary>Builds the volume image.</summary>
  public byte[] Build() {
    // Breadth-first directory order is the Path Table order (8.2.1): by level, then by
    // parent number, then by identifier — which a sorted BFS from the root yields.
    this._root.Time = this.Now;
    var dirs = new List<Node>();
    var queue = new Queue<Node>();
    queue.Enqueue(this._root);
    while (queue.Count > 0) {
      var d = queue.Dequeue();
      d.Number = checked((ushort)(dirs.Count + 1));
      dirs.Add(d);
      foreach (var c in d.Children.Values)
        if (c.IsDir) queue.Enqueue(c);
    }

    var pathTableSize = dirs.Sum(d => PathTableRecordLength(d == this._root ? 1 : d.Id.Length));
    var pathTableSectors = (uint)((pathTableSize + SectorSize - 1) / SectorSize);

    uint next = FirstDescriptorSector + 2;            // PVD at 16, terminator at 17
    var pathL = next; next += pathTableSectors;
    var pathM = next; next += pathTableSectors;

    foreach (var d in dirs) {
      d.Length = DirectoryBytes(d);
      d.Lbn = next;
      next += (uint)(d.Length / SectorSize);
    }
    foreach (var d in dirs)
      foreach (var f in d.Children.Values.Where(c => !c.IsDir)) {
        f.Lbn = f.Data.Length == 0 ? 0 : next;
        f.Length = (uint)f.Data.Length;
        next += (uint)((f.Data.LongLength + SectorSize - 1) / SectorSize);
      }

    var image = new byte[(long)next * SectorSize];
    this.WriteDescriptor(image.AsSpan(FirstDescriptorSector * SectorSize, SectorSize), next, (uint)pathTableSize, pathL, pathM);
    var term = image.AsSpan((FirstDescriptorSector + 1) * SectorSize, SectorSize);
    WriteBoth32(term[VdLbn..], FirstDescriptorSector + 1);
    term[VdType] = TypeTerminator;
    StandardId.CopyTo(term[VdId..]);
    term[VdVersion] = 1;

    WritePathTable(image.AsSpan((int)(pathL * SectorSize)), dirs, bigEndian: false);
    WritePathTable(image.AsSpan((int)(pathM * SectorSize)), dirs, bigEndian: true);

    foreach (var d in dirs) {
      var span = image.AsSpan((int)(d.Lbn * SectorSize), (int)d.Length);
      var off = 0;
      foreach (var (id, node) in DirectoryRecords(d)) {
        var len = DirectoryRecordLength(id.Length);
        if (off / SectorSize != (off + len - 1) / SectorSize) off = (off / SectorSize + 1) * SectorSize;
        WriteRecord(span.Slice(off, len), node, id);
        off += len;
      }
      foreach (var f in d.Children.Values.Where(c => !c.IsDir && c.Data.Length > 0))
        f.Data.CopyTo(image.AsSpan((int)(f.Lbn * SectorSize)));
    }
    return image;
  }

  /// <summary>Self, parent, then the children in identifier order.</summary>
  private static IEnumerable<(byte[] Id, Node Node)> DirectoryRecords(Node d) {
    yield return ([0], d);
    yield return ([1], d.Parent ?? d);
    foreach (var c in d.Children.Values)
      yield return (Encoding.ASCII.GetBytes(c.IsDir ? c.Id : c.Id + ";1"), c);
  }

  /// <summary>Whole sectors a directory takes, no record crossing a sector boundary (8.3.1).</summary>
  private static uint DirectoryBytes(Node d) {
    var off = 0;
    foreach (var (id, _) in DirectoryRecords(d)) {
      var len = DirectoryRecordLength(id.Length);
      if (off / SectorSize != (off + len - 1) / SectorSize) off = (off / SectorSize + 1) * SectorSize;
      off += len;
    }
    return (uint)((off + SectorSize - 1) / SectorSize * SectorSize);
  }

  private static void WriteRecord(Span<byte> rec, Node node, byte[] id) {
    rec.Clear();
    rec[DrLength] = (byte)rec.Length;
    WriteBoth32(rec[DrExtent..], node.Lbn);
    WriteBoth32(rec[DrDataLength..], node.Length);
    EncodeRecordDate(rec[DrDate..], node.Time);
    rec[DrFlags] = node.IsDir ? FlagDirectory : (byte)0;
    WriteBoth16(rec[DrVolumeSequence..], 1);
    rec[DrNameLength] = (byte)id.Length;
    id.CopyTo(rec[DrName..]);
  }

  private static void WritePathTable(Span<byte> dest, List<Node> dirs, bool bigEndian) {
    var off = 0;
    foreach (var d in dirs) {
      var id = d.Parent is null ? new byte[] { 0 } : Encoding.ASCII.GetBytes(d.Id);
      var rec = dest.Slice(off, PathTableRecordLength(id.Length));
      if (bigEndian) {
        BinaryPrimitives.WriteUInt32BigEndian(rec, d.Lbn);
        BinaryPrimitives.WriteUInt16BigEndian(rec[6..], d.Parent?.Number ?? 1);
      } else {
        BinaryPrimitives.WriteUInt32LittleEndian(rec, d.Lbn);
        BinaryPrimitives.WriteUInt16LittleEndian(rec[6..], d.Parent?.Number ?? 1);
      }
      rec[4] = 0;                       // Extended Attribute Record length
      rec[5] = (byte)id.Length;
      id.CopyTo(rec[8..]);
      off += rec.Length;
    }
  }

  private void WriteDescriptor(Span<byte> vd, uint volumeBlocks, uint pathTableSize, uint pathL, uint pathM) {
    WriteBoth32(vd[VdLbn..], FirstDescriptorSector);
    vd[VdType] = TypeStandard;
    StandardId.CopyTo(vd[VdId..]);
    vd[VdVersion] = 1;
    WritePadded(vd.Slice(PvdSystemId, 32), "");
    var volumeId = new string(this.VolumeId.ToUpperInvariant().Where(IsDCharacter).Take(32).ToArray());
    WritePadded(vd.Slice(PvdVolumeId, 32), volumeId);
    WriteBoth32(vd[PvdVolumeSpaceSize..], volumeBlocks);
    WriteBoth16(vd[PvdVolumeSetSize..], 1);
    WriteBoth16(vd[PvdVolumeSetSequence..], 1);
    WriteBoth16(vd[PvdLogicalBlockSize..], SectorSize);
    WriteBoth32(vd[PvdPathTableSize..], pathTableSize);
    BinaryPrimitives.WriteUInt32LittleEndian(vd[PvdPathTableL..], pathL);
    BinaryPrimitives.WriteUInt32BigEndian(vd[PvdPathTableM..], pathM);
    WriteRecord(vd.Slice(PvdRootRecord, 34), this._root, [0]);
    WritePadded(vd.Slice(PvdVolumeSetId, 128), "");
    WritePadded(vd.Slice(PvdPublisherId, 128), "");
    WritePadded(vd.Slice(PvdPreparerId, 128), "");
    WritePadded(vd.Slice(PvdApplicationId, 128), "");
    WritePadded(vd.Slice(PvdCopyrightFile, 32), "");
    WritePadded(vd.Slice(PvdAbstractFile, 32), "");
    EncodeVolumeDate(vd.Slice(PvdCreation, 16), this.Now);
    EncodeVolumeDate(vd.Slice(PvdModification, 16), this.Now);
    EncodeVolumeDate(vd.Slice(PvdExpiration, 16), null);
    EncodeVolumeDate(vd.Slice(PvdEffective, 16), null);
    vd[PvdFileStructureVersion] = 1;
  }
}
