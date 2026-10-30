#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Dbx;

/// <summary>What an Outlook Express DBX file holds, named by byte 4 of its signature.</summary>
public enum DbxKind {
  /// <summary>A mail or news folder: messages.</summary>
  Messages = 0xC5,
  /// <summary>Folders.dbx: the folder tree.</summary>
  Folders = 0xC6,
  /// <summary>Pop3uidl.dbx.</summary>
  Pop3Uidl = 0xC7,
  /// <summary>Offline.dbx.</summary>
  Offline = 0x30,
}

/// <summary>One folder of Folders.dbx.</summary>
public sealed record DbxFolder(uint Id, uint ParentId, string Name, string FileName);

/// <summary>One message of a DBX mail folder: its subject and its raw RFC 822 text.</summary>
public sealed record DbxMessage(int Index, string? Subject, byte[] Data);

/// <summary>
/// Reads Outlook Express 5/6 DBX files: a 0x24BC-byte header, a B-tree of index nodes whose values
/// point at "indexed items" (records of numbered attributes), and for mail folders the message
/// texts as chains of data blocks.
/// </summary>
/// <remarks>
/// Layout (little-endian), as Arne Schloh's "Outlook Express DBX file format" describes it and the
/// MIT-licensed DbxOutlookExpress library reads it (see THIRD-PARTY-NOTICE.DbxOutlookExpress.txt):
/// <code>
/// header      CF AD 12 FE kind FD 74 6F 66 E3 D1 11 9A 4E 00 C0 4F A3 09 D4 05 00 00 00 05 00 00 00
///             dword 0x31: item count, dword 0x39: message tree root, dword 0x3B: folder tree root
/// tree node   dword self, ?, dword leftChild, dword parent, … byte@0x11 itemCount,
///             from dword 6: itemCount × { dword value, dword child, dword count }   (0x27C bytes)
/// item        dword self, dword bodyLength, word ?, byte attributeCount, byte ?, then the body:
///             attributeCount × { byte id (bit 7 = value stored inline), 3 bytes value or offset }
///             followed by the data the offsets point into
/// data block  dword self, dword size, dword length, dword next, then length bytes of message text
/// </code>
/// </remarks>
public sealed class DbxReader {

  private const int HeaderSize = 0x24BC;
  private const int TreeNodeSize = 0x27C;
  private const int MessageTreeRootIndex = 0x39;
  private const int FolderTreeRootIndex = 0x3B;
  private const int AttributeMessagePointer = 0x04;
  private const int AttributeSubject = 0x08;
  private const int FolderId = 0x00, FolderParent = 0x01, FolderName = 0x02, FolderFileName = 0x03;

  private static ReadOnlySpan<byte> Signature => [
    0xCF, 0xAD, 0x12, 0xFE, 0x00, 0xFD, 0x74, 0x6F, 0x66, 0xE3, 0xD1, 0x11, 0x9A, 0x4E,
    0x00, 0xC0, 0x4F, 0xA3, 0x09, 0xD4, 0x05, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00,
  ];

  private readonly byte[] _file;

  public DbxReader(byte[] file) {
    ArgumentNullException.ThrowIfNull(file);
    this._file = file;
    if (file.Length < HeaderSize)
      throw new InvalidDataException("DBX: shorter than its 0x24BC-byte header.");
    // Every DBX opens with CF AD 12 FE and its kind byte; the class id that follows is shared by the
    // message, folder and pop3uidl files, while Offline.dbx carries one of its own.
    var checkedLength = file[4] == (byte)DbxKind.Offline ? 4 : Signature.Length;
    for (var i = 0; i < checkedLength; ++i)
      if (i != 4 && file[i] != Signature[i])
        throw new InvalidDataException($"DBX: signature byte {i} is 0x{file[i]:X2}, not 0x{Signature[i]:X2}.");
    this.Kind = file[4] switch {
      0xC5 => DbxKind.Messages,
      0xC6 => DbxKind.Folders,
      0xC7 => DbxKind.Pop3Uidl,
      0x30 => DbxKind.Offline,
      var k => throw new InvalidDataException($"DBX: unknown file kind 0x{k:X2}."),
    };
  }

  /// <summary>What the file holds.</summary>
  public DbxKind Kind { get; }

  /// <summary>The messages of a mail folder, in index-tree order.</summary>
  public IReadOnlyList<DbxMessage> ReadMessages() {
    if (this.Kind != DbxKind.Messages) return [];
    var messages = new List<DbxMessage>();
    foreach (var item in this.TreeValues(this.HeaderDword(MessageTreeRootIndex))) {
      var attributes = this.ReadItem(item);
      var pointer = attributes.TryGetValue(AttributeMessagePointer, out var p) ? p.Value(4) : 0;
      var subject = attributes.TryGetValue(AttributeSubject, out var s) ? s.String() : null;
      messages.Add(new DbxMessage(messages.Count, subject, pointer == 0 ? [] : this.ReadChain(pointer)));
    }
    return messages;
  }

  /// <summary>The folder tree of Folders.dbx, in index-tree order.</summary>
  public IReadOnlyList<DbxFolder> ReadFolders() {
    if (this.Kind != DbxKind.Folders) return [];
    var folders = new List<DbxFolder>();
    foreach (var item in this.TreeValues(this.HeaderDword(FolderTreeRootIndex))) {
      var a = this.ReadItem(item);
      folders.Add(new DbxFolder(
        a.TryGetValue(FolderId, out var id) ? id.Value(4) : 0,
        a.TryGetValue(FolderParent, out var parent) ? parent.Value(4) : 0,
        a.TryGetValue(FolderName, out var name) ? name.String() ?? "" : "",
        a.TryGetValue(FolderFileName, out var fileName) ? fileName.String() ?? "" : ""));
    }
    return folders;
  }

  private uint HeaderDword(int index) => BinaryPrimitives.ReadUInt32LittleEndian(this._file.AsSpan(index * 4));

  /// <summary>Walks the index tree in order: a node's left child, then each value followed by its child.</summary>
  private List<uint> TreeValues(uint root) {
    var values = new List<uint>();
    var visited = new HashSet<uint>();
    void Walk(uint node, int depth) {
      if (node == 0) return;
      if (depth > 64 || !visited.Add(node))
        throw new InvalidDataException($"DBX: index tree loops or is deeper than 64 at 0x{node:X}.");
      if ((long)node + TreeNodeSize > this._file.Length)
        throw new InvalidDataException($"DBX: index node 0x{node:X} lies outside the file.");
      var n = this._file.AsSpan((int)node, TreeNodeSize);
      if (BinaryPrimitives.ReadUInt32LittleEndian(n) != node)
        throw new InvalidDataException($"DBX: index node 0x{node:X} does not name itself.");
      Walk(BinaryPrimitives.ReadUInt32LittleEndian(n[8..]), depth + 1);
      var count = n[0x11];
      if (24 + count * 12 > TreeNodeSize)
        throw new InvalidDataException($"DBX: index node 0x{node:X} claims {count} items.");
      for (var i = 0; i < count; ++i) {
        var e = n[(24 + i * 12)..];
        values.Add(BinaryPrimitives.ReadUInt32LittleEndian(e));
        Walk(BinaryPrimitives.ReadUInt32LittleEndian(e[4..]), depth + 1);
      }
    }
    Walk(root, 0);
    return values;
  }

  /// <summary>An attribute of an indexed item: inline 3-byte value, or data at an offset into the item body.</summary>
  private readonly record struct Attribute(byte[] Body, bool Inline, int Raw) {
    public uint Value(int width) {
      if (this.Inline) return (uint)this.Raw;
      if (this.Raw + width > this.Body.Length)
        throw new InvalidDataException("DBX: attribute value runs past its item.");
      var v = 0u;
      for (var i = width - 1; i >= 0; --i) v = (v << 8) | this.Body[this.Raw + i];
      return v;
    }

    public string? String() {
      if (this.Inline || this.Raw >= this.Body.Length) return null;
      var end = Array.IndexOf(this.Body, (byte)0, this.Raw);
      return Encoding.Latin1.GetString(this.Body, this.Raw, (end < 0 ? this.Body.Length : end) - this.Raw);
    }
  }

  private Dictionary<int, Attribute> ReadItem(uint address) {
    if (address == 0 || (long)address + 12 > this._file.Length)
      throw new InvalidDataException($"DBX: indexed item 0x{address:X} lies outside the file.");
    var head = this._file.AsSpan((int)address, 12);
    if (BinaryPrimitives.ReadUInt32LittleEndian(head) != address)
      throw new InvalidDataException($"DBX: indexed item 0x{address:X} does not name itself.");
    var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
    var count = head[10];
    if (bodyLength > this._file.Length - address - 12 || count * 4 > bodyLength)
      throw new InvalidDataException($"DBX: indexed item 0x{address:X} overruns the file.");
    var body = this._file.AsSpan((int)address + 12, (int)bodyLength).ToArray();
    var attributes = new Dictionary<int, Attribute>();
    for (var i = 0; i < count; ++i) {
      var id = body[i * 4];
      var raw = body[i * 4 + 1] | (body[i * 4 + 2] << 8) | (body[i * 4 + 3] << 16);
      var inline = (id & 0x80) != 0;
      attributes[id & 0x7F] = new Attribute(body, inline, inline ? raw : count * 4 + raw);
    }
    return attributes;
  }

  private byte[] ReadChain(uint address) {
    using var message = new MemoryStream();
    var visited = new HashSet<uint>();
    while (address != 0) {
      if (!visited.Add(address))
        throw new InvalidDataException($"DBX: message block chain loops at 0x{address:X}.");
      if ((long)address + 16 > this._file.Length)
        throw new InvalidDataException($"DBX: message block 0x{address:X} lies outside the file.");
      var head = this._file.AsSpan((int)address, 16);
      if (BinaryPrimitives.ReadUInt32LittleEndian(head) != address)
        throw new InvalidDataException($"DBX: message block 0x{address:X} does not name itself.");
      var size = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
      var length = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
      if (length > size || (long)address + 16 + length > this._file.Length)
        throw new InvalidDataException($"DBX: message block 0x{address:X} holds {length} of {size} bytes past the file.");
      message.Write(this._file, (int)address + 16, (int)length);
      address = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
    }
    return message.ToArray();
  }
}
