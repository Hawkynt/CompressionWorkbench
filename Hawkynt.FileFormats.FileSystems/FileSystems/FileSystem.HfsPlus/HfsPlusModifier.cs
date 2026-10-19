#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.HfsPlus;

/// <summary>
/// In-place HFS+/HFSX editor: adds, replaces and removes files in an existing volume, editing the
/// catalog, extents overflow and attributes B-trees, the allocation file and the volume header the
/// way TN1150 lays them out — every other record, date, permission, Finder info, resource fork,
/// extended attribute and journal of the volume is kept as it was.
/// </summary>
/// <remarks>
/// <para>
/// Each operation works on a copy and writes the volume back only when it succeeds, so whatever
/// it refuses leaves the image byte-identical. Refused: a hard link or directory hard link (their
/// link chains and counts are not maintained here), anything inside the metadata directories,
/// a volume whose journal holds transactions not yet replayed, a special file continued in the
/// extents overflow file, and a volume without the free space the edit needs.
/// </para>
/// <para>
/// The B-trees are rewritten as packed trees after each edit (<see cref="HfsPlusBTreeFile"/>),
/// which is how an insertion that splits a node — or a removal that empties one — stays a valid
/// tree, and a B-tree file that needs more nodes grows into free space.
/// </para>
/// </remarks>
public static class HfsPlusModifier {
  private const uint RootParentCnid = 1;
  private const uint RootFolderCnid = 2;
  private const short FolderRecord = 1;
  private const short FileRecord = 2;
  private const short FolderThreadRecord = 3;
  private const short FileThreadRecord = 4;
  private const int CatalogFileRecordSize = 248;
  private const int CatalogFolderRecordSize = 88;
  private const int DataForkOffset = 88;
  private const int ResourceForkOffset = 168;
  private const byte DataFork = 0x00;
  private const byte ResourceFork = 0xFF;
  private const ushort ThreadExistsMask = 0x0002;   // kHFSThreadExistsMask
  private const ushort HasAttributesMask = 0x0004;  // kHFSHasAttributesMask
  private const ushort HasFolderCountMask = 0x0010; // kHFSHasFolderCountMask (HFSX folders)
  private const ushort HasLinkChainMask = 0x0020;   // kHFSHasLinkChainMask
  private const byte CompressedOwnerFlag = 0x20;    // UF_COMPRESSED
  private const uint AttributeInline = 0x10;        // kHFSPlusAttrInlineData
  private const uint AttributeFork = 0x20;          // kHFSPlusAttrForkData
  private const uint AttributeExtents = 0x30;       // kHFSPlusAttrExtents
  private const string DecmpfsName = "com.apple.decmpfs";
  private const string FileMetadataFolder = "\0\0\0\0HFS+ Private Data";
  private const string DirectoryMetadataFolder = ".HFS+ Private Directory Data\r";

  /// <summary>
  /// Adds (or replaces by name) a file. <paramref name="name"/> is a POSIX path ('/' separates
  /// folders, ':' stands for the '/' a catalog name may hold); missing folders on the way are
  /// created. Refused, with the volume untouched, as the class remarks list.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data)
    => AddFile(image, name, data, HfsPlusCompression.None);

  /// <summary>
  /// Adds (or replaces by name) a file, stored with HFS+ transparent compression when
  /// <paramref name="compression"/> asks for it (as <see cref="HfsPlusWriter.TransparentCompression"/>
  /// does: a file whose compressed form would not be smaller stays uncompressed), and with the
  /// given extended attributes — inline when small enough for an attributes node, else in a fork
  /// of their own.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data, HfsPlusCompression compression,
      IReadOnlyDictionary<string, byte[]>? extendedAttributes = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var components = Components(name);
    if (components.Count == 0) throw new ArgumentException("HFS+: an empty name cannot be added.", nameof(name));
    if (extendedAttributes is { } attributes) {
      foreach (var key in attributes.Keys)
        if (key.Length is 0 or > 127 || key == DecmpfsName)
          throw new ArgumentException($"HFS+: '{key}' cannot be an extended attribute name here.", nameof(extendedAttributes));
    }

    Edit(image, volume => {
      var parent = RootFolderCnid;
      for (var i = 0; i < components.Count - 1; i++) parent = FindOrCreateFolder(volume, parent, components[i]);
      var leaf = components[^1];
      RequireNotMetadata(volume, parent, leaf);
      if (FindChild(volume, parent, leaf) is { } existing) {
        if (Type(existing.Record) != FileRecord)
          throw new NotSupportedException($"HFS+: '{name}' is a folder; a file cannot replace it.");
        DeleteFile(volume, parent, existing.Index, wipe: true, name);
      }
      CreateFile(volume, parent, leaf, data, compression, extendedAttributes);
      return true;
    });
  }

  /// <summary>
  /// Removes the named file: its catalog records, its extended attributes (a compressed file's
  /// decmpfs attribute among them), its extents overflow records, and every block of both forks
  /// and of any attribute fork, which are wiped when asked and freed. Returns false when no such
  /// file exists. <paramref name="name"/> is a POSIX path. Refused, with the volume untouched, as
  /// the class remarks list.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    var components = Components(name);
    if (components.Count == 0) return false;
    return Edit(image, volume => {
      var parent = RootFolderCnid;
      for (var i = 0; i < components.Count - 1; i++) {
        if (FindChild(volume, parent, components[i]) is not { } folder) return false;
        if (IsDirectoryLink(folder.Record))
          throw new NotSupportedException($"HFS+: '{name}' lies in a directory hard link; its directory is shared and not edited in place.");
        if (Type(folder.Record) != FolderRecord) return false;
        parent = Cnid(folder.Record);
      }
      if (FindChild(volume, parent, components[^1]) is not { } file) return false;
      if (Type(file.Record) != FileRecord)
        throw new NotSupportedException($"HFS+: '{name}' is a folder; only files are removed in place.");
      RequireNotMetadata(volume, parent, components[^1]);
      DeleteFile(volume, parent, file.Index, wipeData, name);
      return true;
    });
  }

  /// <summary>Runs <paramref name="edit"/> on a copy of the volume and writes it back when it returns true.</summary>
  private static bool Edit(Stream image, Func<HfsPlusVolumeEditor, bool> edit) {
    var copy = ReadAll(image);
    var volume = HfsPlusVolumeEditor.Open(copy);
    if (!edit(volume)) return false;
    volume.Flush();
    WriteAll(image, copy);
    return true;
  }

  /// <summary>The catalog names of a POSIX path's components; "." is skipped and ".." refused.</summary>
  private static List<string> Components(string path) {
    var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(static p => p != ".").ToList();
    if (parts.Contains(".."))
      throw new ArgumentException($"HFS+: '{path}' climbs out of a folder; give a path from the volume root.", nameof(path));
    return parts.Select(HfsPlusName.ToCatalog).ToList();
  }

  // ── Catalog lookups ─────────────────────────────────────────────────────

  /// <summary>Whether a catalog record is a directory hard link (xnu: type 'fdrp', creator 'MACS', a link chain).</summary>
  private static bool IsDirectoryLink(byte[] record) {
    var body = record.AsSpan(HfsPlusBTreeFile.DataOffset(record));
    return BinaryPrimitives.ReadInt16BigEndian(body) == FileRecord
           && BinaryPrimitives.ReadUInt32BigEndian(body[48..]) == 0x66647270 && BinaryPrimitives.ReadUInt32BigEndian(body[52..]) == 0x4D414353
           && (BinaryPrimitives.ReadUInt16BigEndian(body[2..]) & HasLinkChainMask) != 0;
  }

  private static short Type(byte[] record) => BinaryPrimitives.ReadInt16BigEndian(record.AsSpan(HfsPlusBTreeFile.DataOffset(record)));
  private static uint Cnid(byte[] record) => BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(HfsPlusBTreeFile.DataOffset(record) + 8));

  /// <summary>The folder or file record named <paramref name="name"/> in <paramref name="parent"/>, as the volume's key order matches names.</summary>
  private static (int Index, byte[] Record)? FindChild(HfsPlusVolumeEditor volume, uint parent, string name) {
    var at = volume.Catalog.Search(CatalogKey(parent, name));
    if (at < 0) return null;
    var record = volume.Catalog.Records[at];
    return Type(record) is FolderRecord or FileRecord ? (at, record) : null;
  }

  /// <summary>The thread record of <paramref name="cnid"/>: its key is (cnid, empty name).</summary>
  private static int FindThread(HfsPlusVolumeEditor volume, uint cnid) => volume.Catalog.Search(CatalogKey(cnid, ""));

  /// <summary>The index of the folder record of <paramref name="folder"/>, found through its thread.</summary>
  private static int FolderRecordIndex(HfsPlusVolumeEditor volume, uint folder) {
    var thread = FindThread(volume, folder);
    if (thread < 0) throw new InvalidDataException($"HFS+: folder {folder} has no thread record.");
    var record = volume.Catalog.Records[thread];
    var data = HfsPlusBTreeFile.DataOffset(record);
    if (BinaryPrimitives.ReadInt16BigEndian(record.AsSpan(data)) != FolderThreadRecord)
      throw new InvalidDataException($"HFS+: catalog node {folder} is not a folder.");
    var parent = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(data + 4));
    var nameLength = BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(data + 8));
    var name = Encoding.BigEndianUnicode.GetString(record, data + 10, nameLength * 2);
    var at = volume.Catalog.Search(CatalogKey(parent, name));
    if (at < 0 || Type(volume.Catalog.Records[at]) != FolderRecord) throw new InvalidDataException($"HFS+: folder {folder}'s thread names no folder record.");
    return at;
  }

  /// <summary>Refuses anything in, or named like, a metadata directory: those hold the targets of hard links.</summary>
  private static void RequireNotMetadata(HfsPlusVolumeEditor volume, uint parent, string name) {
    if (parent == RootFolderCnid && (name == FileMetadataFolder || name == DirectoryMetadataFolder))
      throw new NotSupportedException("HFS+: the metadata directories are not edited in place.");
    for (var (at, hops) = (parent, 0); at != RootFolderCnid && hops < 65536; hops++) {
      var thread = FindThread(volume, at);
      if (thread < 0) return;
      var record = volume.Catalog.Records[thread];
      var data = HfsPlusBTreeFile.DataOffset(record);
      var up = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(data + 4));
      var nameLength = BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(data + 8));
      var folderName = Encoding.BigEndianUnicode.GetString(record, data + 10, nameLength * 2);
      if (up == RootFolderCnid && folderName is FileMetadataFolder or DirectoryMetadataFolder)
        throw new NotSupportedException("HFS+: the metadata directories are not edited in place.");
      if (up == at) return;
      at = up;
    }
  }

  // ── Folders ─────────────────────────────────────────────────────────────

  private static uint FindOrCreateFolder(HfsPlusVolumeEditor volume, uint parent, string name) {
    if (FindChild(volume, parent, name) is { } existing) {
      if (IsDirectoryLink(existing.Record))
        throw new NotSupportedException($"HFS+: '{HfsPlusName.FromCatalog(name)}' is a directory hard link; its directory is shared and not edited in place.");
      if (Type(existing.Record) != FolderRecord) throw new NotSupportedException($"HFS+: '{HfsPlusName.FromCatalog(name)}' is a file, not a folder.");
      return Cnid(existing.Record);
    }
    RequireNotMetadata(volume, parent, name);
    var cnid = volume.NextCatalogId();
    var now = HfsPlusVolumeEditor.HfsTimestamp(DateTime.UtcNow);
    var body = new byte[CatalogFolderRecordSize];
    BinaryPrimitives.WriteInt16BigEndian(body, FolderRecord);
    var parentRecord = volume.Catalog.Records[FolderRecordIndex(volume, parent)];
    var parentFlags = BinaryPrimitives.ReadUInt16BigEndian(parentRecord.AsSpan(HfsPlusBTreeFile.DataOffset(parentRecord) + 2));
    // An HFSX volume whose folders keep a subfolder count does so for the new one too.
    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), (ushort)(parentFlags & HasFolderCountMask));
    BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8), cnid);
    for (var d = 12; d <= 24; d += 4) BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(d), now);
    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(32 + 10), HfsPlusWriter.DirectoryMode);
    volume.Catalog.Insert([.. CatalogKey(parent, name), .. body]);
    volume.Catalog.Insert(Thread(FolderThreadRecord, cnid, parent, name));
    AdjustFolder(volume, parent, valence: +1, folders: +1);
    volume.WriteHeader(36, volume.ReadHeader(36) + 1);  // folderCount
    return cnid;
  }

  /// <summary>Changes a folder's valence and, where it keeps one, its subfolder count; stamps its content date.</summary>
  private static void AdjustFolder(HfsPlusVolumeEditor volume, uint folder, int valence, int folders) {
    var index = FolderRecordIndex(volume, folder);
    var record = volume.Catalog.Records[index].ToArray();
    var data = HfsPlusBTreeFile.DataOffset(record);
    var span = record.AsSpan(data);
    BinaryPrimitives.WriteUInt32BigEndian(span[4..], checked((uint)(BinaryPrimitives.ReadUInt32BigEndian(span[4..]) + valence)));
    if (folders != 0 && (BinaryPrimitives.ReadUInt16BigEndian(span[2..]) & HasFolderCountMask) != 0 && span.Length >= 88)
      BinaryPrimitives.WriteUInt32BigEndian(span[84..], checked((uint)(BinaryPrimitives.ReadUInt32BigEndian(span[84..]) + folders)));
    BinaryPrimitives.WriteUInt32BigEndian(span[16..], HfsPlusVolumeEditor.HfsTimestamp(DateTime.UtcNow));  // contentModDate
    volume.Catalog.Replace(index, record);
  }

  // ── Files ───────────────────────────────────────────────────────────────

  private static void CreateFile(HfsPlusVolumeEditor volume, uint parent, string name, byte[] data,
      HfsPlusCompression compression, IReadOnlyDictionary<string, byte[]>? extendedAttributes) {
    var cnid = volume.NextCatalogId();
    var encoding = compression == HfsPlusCompression.None || data.Length == 0 ? null : HfsPlusDecmpfs.Encode(data, compression);
    var payload = encoding is null ? data : encoding.ResourceFork ?? [];
    var blocks = (uint)((payload.LongLength + volume.BlockSize - 1) / volume.BlockSize);
    var extents = volume.Allocate(blocks) ?? throw new NotSupportedException($"HFS+: the volume has no room for {blocks} more blocks.");
    var at = 0L;
    foreach (var (start, count) in extents) {
      var bytes = (int)Math.Min((long)count * volume.BlockSize, payload.Length - at);
      var target = volume.Image.AsSpan((int)((long)start * volume.BlockSize), (int)((long)count * volume.BlockSize));
      target.Clear();
      payload.AsSpan((int)at, bytes).CopyTo(target);
      at += bytes;
    }

    var hasAttributes = encoding is not null || extendedAttributes is { Count: > 0 };
    var now = HfsPlusVolumeEditor.HfsTimestamp(DateTime.UtcNow);
    var body = new byte[CatalogFileRecordSize];
    BinaryPrimitives.WriteInt16BigEndian(body, FileRecord);
    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), (ushort)(ThreadExistsMask | (hasAttributes ? HasAttributesMask : 0)));
    BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8), cnid);
    for (var d = 12; d <= 24; d += 4) BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(d), now);
    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(32 + 10), HfsPlusWriter.RegularFileMode);
    if (encoding is not null) body[32 + 9] = CompressedOwnerFlag;
    WriteFork(body.AsSpan(encoding is null ? DataForkOffset : ResourceForkOffset, 80), payload.LongLength, volume.BlockSize, extents);
    volume.Catalog.Insert([.. CatalogKey(parent, name), .. body]);
    volume.Catalog.Insert(Thread(FileThreadRecord, cnid, parent, name));

    // Runs past the eighth go to the extents overflow file, eight per record, keyed by the
    // fork block each record starts at (TN1150 HFSPlusExtentKey).
    var forkBlock = (uint)extents.Take(8).Sum(static e => (long)e.BlockCount);
    for (var first = 8; first < extents.Count; first += 8) {
      var group = extents.Skip(first).Take(8).ToList();
      volume.Extents.Insert(ExtentRecord(encoding is null ? DataFork : ResourceFork, cnid, forkBlock, group));
      forkBlock += (uint)group.Sum(static e => (long)e.BlockCount);
    }

    if (encoding is not null) InsertAttribute(volume, cnid, DecmpfsName, encoding.Attribute, inlineLimit: HfsPlusDecmpfs.MaxAttributeSize);
    foreach (var (attribute, value) in extendedAttributes ?? new Dictionary<string, byte[]>())
      InsertAttribute(volume, cnid, attribute, value, inlineLimit: MaxInlineAttributeSize(volume));

    AdjustFolder(volume, parent, valence: +1, folders: 0);
    volume.WriteHeader(32, volume.ReadHeader(32) + 1);  // fileCount
    if (extents.Count > 0) volume.WriteHeader(52, extents[^1].StartBlock + extents[^1].BlockCount);  // nextAllocation
  }

  private static void DeleteFile(HfsPlusVolumeEditor volume, uint parent, int index, bool wipe, string shown) {
    var record = volume.Catalog.Records[index];
    var data = HfsPlusBTreeFile.DataOffset(record);
    var body = record.AsSpan(data);
    var cnid = BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
    var type = BinaryPrimitives.ReadUInt32BigEndian(body[48..]);
    var creator = BinaryPrimitives.ReadUInt32BigEndian(body[52..]);
    // TN1150 hard link ('hlnk'/'hfs+') and xnu directory hard link ('fdrp'/'MACS' with a link chain).
    if (type == 0x686C6E6B && creator == 0x6866732B)
      throw new NotSupportedException($"HFS+: '{shown}' is a hard link; removing it in place would leave its shared data's link count wrong.");
    if (type == 0x66647270 && creator == 0x4D414353 && (BinaryPrimitives.ReadUInt16BigEndian(body[2..]) & HasLinkChainMask) != 0)
      throw new NotSupportedException($"HFS+: '{shown}' is a directory hard link; removing it in place would leave its directory's link chain wrong.");

    // Each fork's blocks: the eight its record holds and the extents overflow records keyed to it.
    // Blocks that do not add up to the fork's totalBlocks mean records are missing or stray, and
    // freeing what is found would leave the rest allocated to nobody — refused.
    var blocks = new List<(uint, uint)>();
    foreach (var (forkOffset, forkType) in new[] { (DataForkOffset, DataFork), (ResourceForkOffset, ResourceFork) }) {
      var fork = ForkExtents(body[forkOffset..]);
      fork.AddRange(volume.Extents.Records
        .Where(r => r[2] == forkType && BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4)) == cnid)
        .SelectMany(ExtentRecordExtents));
      if (fork.Sum(static e => (long)e.Item2) != BinaryPrimitives.ReadUInt32BigEndian(body[(forkOffset + 12)..]))
        throw new InvalidDataException($"HFS+: '{shown}' has a fork whose extents do not add up to its block count; it is not removed.");
      blocks.AddRange(fork);
    }
    if (volume.Attributes is { } attributes)
      blocks.AddRange(attributes.Records.Where(r => BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4)) == cnid).SelectMany(AttributeExtentsOf));

    var thread = FindThread(volume, cnid);
    var threadRecord = thread >= 0 ? volume.Catalog.Records[thread] : null;
    volume.Catalog.RemoveAll(r => ReferenceEquals(r, record) || ReferenceEquals(r, threadRecord));
    volume.Extents.RemoveAll(r => BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4)) == cnid);
    volume.Attributes?.RemoveAll(r => BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4)) == cnid);
    volume.Free(blocks, wipe);

    AdjustFolder(volume, parent, valence: -1, folders: 0);
    var files = volume.ReadHeader(32);
    if (files > 0) volume.WriteHeader(32, files - 1);
  }

  /// <summary>The extents an HFSPlusForkData holds itself.</summary>
  private static List<(uint, uint)> ForkExtents(ReadOnlySpan<byte> fork) => ExtentArray(fork[16..]);

  /// <summary>An array of eight extent descriptors (startBlock u32, blockCount u32), up to the first empty one.</summary>
  private static List<(uint, uint)> ExtentArray(ReadOnlySpan<byte> descriptors) {
    var extents = new List<(uint, uint)>(8);
    for (var k = 0; k < 8; k++) {
      var count = BinaryPrimitives.ReadUInt32BigEndian(descriptors[(4 + k * 8)..]);
      if (count == 0) break;
      extents.Add((BinaryPrimitives.ReadUInt32BigEndian(descriptors[(k * 8)..]), count));
    }
    return extents;
  }

  private static IEnumerable<(uint, uint)> ExtentRecordExtents(byte[] record)
    => ExtentArray(record.AsSpan(HfsPlusBTreeFile.DataOffset(record)));

  /// <summary>The blocks an attribute record owns: a fork attribute's eight extents or an extents record's.</summary>
  private static IEnumerable<(uint, uint)> AttributeExtentsOf(byte[] record) {
    var data = HfsPlusBTreeFile.DataOffset(record);
    return BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(data)) switch {
      AttributeFork => ForkExtents(record.AsSpan(data + 8)),
      AttributeExtents => ExtentArray(record.AsSpan(data + 8)),
      _ => [],
    };
  }

  // ── Extended attributes ─────────────────────────────────────────────────

  /// <summary>
  /// The largest attribute value xnu keeps inline for the volume's attribute node size: half a
  /// node less the node descriptor, three offsets, the largest key and the data header.
  /// </summary>
  private static int MaxInlineAttributeSize(HfsPlusVolumeEditor volume) {
    var nodeSize = volume.Attributes?.NodeSize ?? 4096;
    return ((nodeSize - 14 - 6) / 2 - 268 - 16) & ~1;
  }

  private static void InsertAttribute(HfsPlusVolumeEditor volume, uint cnid, string name, byte[] value, int inlineLimit) {
    var attributes = volume.EnsureAttributes();
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    if (value.Length <= inlineLimit) {
      var record = new byte[AttributeKey(cnid, nameBytes, 0).Length + 16 + value.Length + (value.Length & 1)];
      var key = AttributeKey(cnid, nameBytes, 0);
      key.CopyTo(record, 0);
      BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(key.Length), AttributeInline);
      BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(key.Length + 12), (uint)value.Length);
      value.CopyTo(record, key.Length + 16);
      attributes.Insert(record);
      return;
    }

    // A value too large for a node lives in a fork of its own: a fork-data record with the first
    // eight extents, extents records for the rest (TN1150 HFSPlusAttrForkData / HFSPlusAttrExtents).
    var blocks = (uint)((value.LongLength + volume.BlockSize - 1) / volume.BlockSize);
    var extents = volume.Allocate(blocks) ?? throw new NotSupportedException($"HFS+: the volume has no room for a {value.Length}-byte attribute.");
    var at = 0;
    foreach (var (start, count) in extents) {
      var target = volume.Image.AsSpan((int)((long)start * volume.BlockSize), (int)((long)count * volume.BlockSize));
      target.Clear();
      var bytes = (int)Math.Min(target.Length, value.Length - at);
      value.AsSpan(at, bytes).CopyTo(target);
      at += bytes;
    }
    var forkKey = AttributeKey(cnid, nameBytes, 0);
    var forkRecord = new byte[forkKey.Length + 8 + 80];
    forkKey.CopyTo(forkRecord, 0);
    BinaryPrimitives.WriteUInt32BigEndian(forkRecord.AsSpan(forkKey.Length), AttributeFork);
    WriteFork(forkRecord.AsSpan(forkKey.Length + 8, 80), value.Length, volume.BlockSize, extents);
    attributes.Insert(forkRecord);
    var forkBlock = (uint)extents.Take(8).Sum(static e => (long)e.BlockCount);
    for (var first = 8; first < extents.Count; first += 8) {
      var group = extents.Skip(first).Take(8).ToList();
      var key = AttributeKey(cnid, nameBytes, forkBlock);
      var record = new byte[key.Length + 8 + 64];
      key.CopyTo(record, 0);
      BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(key.Length), AttributeExtents);
      for (var k = 0; k < group.Count; k++) {
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(key.Length + 8 + k * 8), group[k].StartBlock);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(key.Length + 12 + k * 8), group[k].BlockCount);
      }
      attributes.Insert(record);
      forkBlock += (uint)group.Sum(static e => (long)e.BlockCount);
    }
  }

  /// <summary>TN1150 HFSPlusAttrKey: keyLength, pad, fileID, startBlock, name length, UTF-16BE name.</summary>
  private static byte[] AttributeKey(uint cnid, byte[] name, uint startBlock) {
    var key = new byte[14 + name.Length];
    BinaryPrimitives.WriteUInt16BigEndian(key, (ushort)(12 + name.Length));
    BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(4), cnid);
    BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(8), startBlock);
    BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(12), (ushort)(name.Length / 2));
    name.CopyTo(key, 14);
    return key;
  }

  // ── Record builders ─────────────────────────────────────────────────────

  /// <summary>TN1150 HFSPlusCatalogKey: keyLength, parentID, name length, UTF-16BE name.</summary>
  private static byte[] CatalogKey(uint parent, string name) {
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    var key = new byte[8 + nameBytes.Length];
    BinaryPrimitives.WriteUInt16BigEndian(key, (ushort)(6 + nameBytes.Length));
    BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(2), parent);
    BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(6), (ushort)(nameBytes.Length / 2));
    nameBytes.CopyTo(key, 8);
    return key;
  }

  /// <summary>A folder or file thread record: key (cnid, ""), data recordType, reserved, parentID, name.</summary>
  private static byte[] Thread(short type, uint cnid, uint parent, string name) {
    var key = CatalogKey(cnid, "");
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    var record = new byte[key.Length + 10 + nameBytes.Length];
    key.CopyTo(record, 0);
    BinaryPrimitives.WriteInt16BigEndian(record.AsSpan(key.Length), type);
    BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(key.Length + 4), parent);
    BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(key.Length + 8), (ushort)(nameBytes.Length / 2));
    nameBytes.CopyTo(record, key.Length + 10);
    return record;
  }

  /// <summary>An extents overflow record: key (keyLength 10, forkType, pad, fileID, startBlock), eight extents.</summary>
  private static byte[] ExtentRecord(byte forkType, uint cnid, uint startBlock, List<(uint StartBlock, uint BlockCount)> extents) {
    var record = new byte[12 + 64];
    BinaryPrimitives.WriteUInt16BigEndian(record, 10);
    record[2] = forkType;
    BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4), cnid);
    BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(8), startBlock);
    for (var k = 0; k < extents.Count; k++) {
      BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(12 + k * 8), extents[k].StartBlock);
      BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(16 + k * 8), extents[k].BlockCount);
    }
    return record;
  }

  /// <summary>An HFSPlusForkData: logicalSize, clumpSize, totalBlocks and the first eight extents.</summary>
  private static void WriteFork(Span<byte> fork, long logicalSize, uint clumpSize, List<(uint StartBlock, uint BlockCount)> extents) {
    fork.Clear();
    BinaryPrimitives.WriteUInt64BigEndian(fork, (ulong)logicalSize);
    BinaryPrimitives.WriteUInt32BigEndian(fork[8..], clumpSize);
    BinaryPrimitives.WriteUInt32BigEndian(fork[12..], (uint)extents.Sum(static e => (long)e.BlockCount));
    for (var k = 0; k < Math.Min(8, extents.Count); k++) {
      BinaryPrimitives.WriteUInt32BigEndian(fork[(16 + k * 8)..], extents[k].StartBlock);
      BinaryPrimitives.WriteUInt32BigEndian(fork[(20 + k * 8)..], extents[k].BlockCount);
    }
  }

  // ── Stream helpers ──────────────────────────────────────────────────────

  private static byte[] ReadAll(Stream image) {
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    return ms.ToArray();
  }

  private static void WriteAll(Stream image, byte[] data) {
    image.Position = 0;
    image.Write(data);
    image.SetLength(data.Length);
  }
}
