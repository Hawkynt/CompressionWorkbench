using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Builds volumes holding directory hard links the way Mac OS X 10.5 and later make them for Time
/// Machine — no macOS is at hand to make a real one, so this follows the published behaviour:
/// Apple TN1150 (catalog records, flags, attributes file) and Apple's open-source HFS sources
/// (hfs_link.c, hfs_format.h, fsck_hfs dirhardlink.c, APSL — read for the format, not copied).
/// <c>fsck.hfsplus</c>, which checks every link chain, judges the result in
/// <see cref="HfsPlusDirectoryHardLinkTests"/>.
/// </summary>
/// <remarks>
/// What a directory hard link is on disk:
/// <list type="bullet">
/// <item>The directory itself (the inode) is the folder "dir_&lt;CNID&gt;" inside the root folder
/// ".HFS+ Private Directory Data\r", which is UF_IMMUTABLE with S_ISVTX set. The inode carries
/// kHFSHasLinkChainMask, its link count in permissions.special, and an inline extended attribute
/// "com.apple.system.hfs.firstlink" holding the first link's CNID as NUL-terminated decimal.</item>
/// <item>Each link is a file record of type 'fdrp', creator 'MACS', Finder flag kIsAlias, with
/// kHFSHasLinkChainMask, the inode's CNID in permissions.special, the previous and next link's CNID
/// in permissions.ownerID and groupID, UF_IMMUTABLE, and the private folder's creation date.</item>
/// <item>Every folder above a link up to (not including) the root carries kHFSHasChildLinkMask.</item>
/// </list>
/// </remarks>
internal static class HfsPlusDirectoryLinkVolumes {
  internal const string PrivateFolder = ".HFS+ Private Directory Data\r";
  private const ushort ThreadExists = 0x0002;
  private const ushort HasAttributes = 0x0004;
  private const ushort HasLinkChain = 0x0020;
  private const ushort HasChildLink = 0x0040;
  private const byte Immutable = 0x02;        // UF_IMMUTABLE (ownerFlags)
  private const ushort StickyBit = 0x0200;    // S_ISVTX
  private const ushort IsAlias = 0x8000;      // Finder kIsAlias

  /// <summary>
  /// Turns the folder <paramref name="inode"/> (a child of the private folder, renamed to
  /// "dir_&lt;CNID&gt;") into the target of the empty files at <paramref name="links"/>, which
  /// become its directory hard links, chained in the order given. Returns the inode's CNID.
  /// </summary>
  internal static uint Link(byte[] image, string inode, params string[] links) {
    var volume = HfsPlusVolumeEditor.Open(image);
    var catalog = volume.Catalog;

    var privateIndex = Find(volume, 2, PrivateFolder);
    var privateRecord = catalog.Records[privateIndex].ToArray();
    var privateBody = HfsPlusBTreeFile.DataOffset(privateRecord);
    var privateCnid = U32(privateRecord, privateBody + 8);
    var metadataCreated = U32(privateRecord, privateBody + 12);
    privateRecord[privateBody + 32 + 9] |= Immutable;
    W16(privateRecord, privateBody + 42, (ushort)(R16(privateRecord, privateBody + 42) | StickyBit));
    catalog.Replace(privateIndex, privateRecord);

    // Where each link is, found before the rename below can move a path that runs through the inode.
    var linkKeys = links.Select(path => {
      var parent = FolderCnid(volume, ParentOf(path));
      var leaf = path[(path.LastIndexOf('/') + 1)..];
      var record = volume.Catalog.Records[Find(volume, parent, leaf)];
      return (Parent: parent, Name: leaf, Cnid: U32(record, HfsPlusBTreeFile.DataOffset(record) + 8));
    }).ToArray();
    var linkIds = linkKeys.Select(static k => k.Cnid).ToArray();

    // The inode: renamed to dir_<CNID>, flagged, counting its links.
    var inodeIndex = Find(volume, privateCnid, inode);
    var inodeRecord = catalog.Records[inodeIndex];
    var inodeBody = inodeRecord.AsSpan(HfsPlusBTreeFile.DataOffset(inodeRecord)).ToArray();
    var inodeCnid = U32(inodeBody, 8);
    var name = "dir_" + inodeCnid.ToString(CultureInfo.InvariantCulture);
    W16(inodeBody, 2, (ushort)(R16(inodeBody, 2) | HasLinkChain | HasAttributes));
    BinaryPrimitives.WriteUInt32BigEndian(inodeBody.AsSpan(44), (uint)links.Length);
    var thread = catalog.Search(Key(inodeCnid, ""));
    var inodeThread = catalog.Records[thread];
    catalog.RemoveAll(r => ReferenceEquals(r, inodeRecord) || ReferenceEquals(r, inodeThread));
    catalog.Insert([.. Key(privateCnid, name), .. inodeBody]);
    catalog.Insert(Thread(3, inodeCnid, privateCnid, name));

    // The links, a doubly linked chain through ownerID/groupID.
    for (var i = 0; i < links.Length; i++) {
      var index = Find(volume, linkKeys[i].Parent, linkKeys[i].Name);
      var record = catalog.Records[index].ToArray();
      var body = HfsPlusBTreeFile.DataOffset(record);
      W16(record, body + 2, ThreadExists | HasLinkChain);
      BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(body + 12), metadataCreated);
      BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(body + 32), i > 0 ? linkIds[i - 1] : 0u);
      BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(body + 36), i + 1 < links.Length ? linkIds[i + 1] : 0u);
      record[body + 32 + 9] = Immutable;
      W16(record, body + 42, 0x8124);  // S_IFREG | 0444, as hfs_link.c creates links
      BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(body + 44), inodeCnid);
      "fdrpMACS"u8.CopyTo(record.AsSpan(body + 48));
      W16(record, body + 56, IsAlias);
      catalog.Replace(index, record);
      MarkAncestors(volume, linkKeys[i].Parent, privateCnid);
    }

    var firstLink = Encoding.ASCII.GetBytes(linkIds[0].ToString(CultureInfo.InvariantCulture) + "\0");
    var attribute = Encoding.BigEndianUnicode.GetBytes("com.apple.system.hfs.firstlink");
    var attributeRecord = new byte[14 + attribute.Length + 16 + firstLink.Length + (firstLink.Length & 1)];
    BinaryPrimitives.WriteUInt16BigEndian(attributeRecord, (ushort)(12 + attribute.Length));
    BinaryPrimitives.WriteUInt32BigEndian(attributeRecord.AsSpan(4), inodeCnid);
    BinaryPrimitives.WriteUInt16BigEndian(attributeRecord.AsSpan(12), (ushort)(attribute.Length / 2));
    attribute.CopyTo(attributeRecord, 14);
    BinaryPrimitives.WriteUInt32BigEndian(attributeRecord.AsSpan(14 + attribute.Length), 0x10);
    BinaryPrimitives.WriteUInt32BigEndian(attributeRecord.AsSpan(14 + attribute.Length + 12), (uint)firstLink.Length);
    firstLink.CopyTo(attributeRecord, 14 + attribute.Length + 16);
    volume.EnsureAttributes().Insert(attributeRecord);

    volume.Flush();
    return inodeCnid;
  }

  /// <summary>
  /// Sets kHFSHasChildLinkMask on <paramref name="folder"/> and every folder above it, stopping at
  /// the root or the private folder, which do not carry it (fsck_hfs dirhardlink.c).
  /// </summary>
  private static void MarkAncestors(HfsPlusVolumeEditor volume, uint folder, uint privateCnid) {
    for (var at = folder; at != 2 && at != privateCnid;) {
      var thread = volume.Catalog.Records[volume.Catalog.Search(Key(at, ""))];
      var data = HfsPlusBTreeFile.DataOffset(thread);
      var parent = U32(thread, data + 4);
      var name = Encoding.BigEndianUnicode.GetString(thread, data + 10, R16(thread, data + 8) * 2);
      var index = Find(volume, parent, name);
      var record = volume.Catalog.Records[index].ToArray();
      var body = HfsPlusBTreeFile.DataOffset(record);
      W16(record, body + 2, (ushort)(R16(record, body + 2) | HasChildLink));
      volume.Catalog.Replace(index, record);
      at = parent;
    }
  }

  private static string ParentOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

  private static uint FolderCnid(HfsPlusVolumeEditor volume, string path) {
    var cnid = 2u;
    foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
      var record = volume.Catalog.Records[Find(volume, cnid, part)];
      cnid = U32(record, HfsPlusBTreeFile.DataOffset(record) + 8);
    }
    return cnid;
  }

  private static int Find(HfsPlusVolumeEditor volume, uint parent, string name) {
    var index = volume.Catalog.Search(Key(parent, name));
    if (index < 0) throw new InvalidOperationException($"no catalog record ({parent}, {name})");
    return index;
  }

  internal static byte[] Key(uint parent, string name) {
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    var key = new byte[8 + nameBytes.Length];
    BinaryPrimitives.WriteUInt16BigEndian(key, (ushort)(6 + nameBytes.Length));
    BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(2), parent);
    BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(6), (ushort)(nameBytes.Length / 2));
    nameBytes.CopyTo(key, 8);
    return key;
  }

  private static byte[] Thread(short type, uint cnid, uint parent, string name) {
    var key = Key(cnid, "");
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    var record = new byte[key.Length + 10 + nameBytes.Length];
    key.CopyTo(record, 0);
    BinaryPrimitives.WriteInt16BigEndian(record.AsSpan(key.Length), type);
    BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(key.Length + 4), parent);
    BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(key.Length + 8), (ushort)(nameBytes.Length / 2));
    nameBytes.CopyTo(record, key.Length + 10);
    return record;
  }

  private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
  private static ushort R16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));
  private static void W16(byte[] b, int at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), value);

  // ── Volumes ─────────────────────────────────────────────────────────

  internal static readonly byte[] Readme = "a file every snapshot shares\n"u8.ToArray();
  internal static readonly byte[] Photo = Enumerable.Range(0, 9000).Select(static i => (byte)(i * 7)).ToArray();

  /// <summary>
  /// A Time Machine-like volume: "Backups/2026-01-01/Users" and "Backups/2026-01-02/Users" are
  /// both links to one directory holding "readme.txt" and "Pictures/photo.raw"; "plain.txt" is
  /// an ordinary file. Returns the image and the inode's CNID.
  /// </summary>
  internal static byte[] Snapshots(out uint inode, bool caseSensitive = false) {
    var w = new HfsPlusWriter(caseSensitive: caseSensitive);
    w.AddFile(PrivateFolder + "/users/readme.txt", Readme);
    w.AddFile(PrivateFolder + "/users/Pictures/photo.raw", Photo);
    w.AddFile("Backups/2026-01-01/Users", []);
    w.AddFile("Backups/2026-01-02/Users", []);
    w.AddFile("plain.txt", "plain"u8.ToArray());
    var image = w.Build(4096);
    inode = Link(image, "users", "Backups/2026-01-01/Users", "Backups/2026-01-02/Users");
    return image;
  }
}
