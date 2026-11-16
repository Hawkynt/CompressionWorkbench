using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Nsis;

/// <summary>
/// Prepares an NSIS 3 exehead stub (a file of NSIS's <c>Stubs</c> folder) the way makensis does
/// before it appends the data: the <c>.ndata</c> section is sized for the installer's variables,
/// the resource section moves behind it and gains an <c>asInvoker</c> manifest, and the image
/// version and DLL characteristics take makensis's values.
/// </summary>
/// <remarks>
/// The edits are the ones makensis 3.10 makes to its stubs (<c>Source/build.cpp</c>,
/// <c>SetVarsSection</c>, <c>SetManifest</c>, <c>UpdatePEHeader</c>; zlib/libpng licence), as
/// measured on its output. The stub itself is native code and is never shipped here; the caller
/// supplies it.
/// </remarks>
internal static class NsisStubPatcher {
  /// <summary>NSIS_MAX_STRLEN: characters per variable.</summary>
  internal const int MaxStringLength = 1024;

  /// <summary>$0-$9, $R0-$R9 and the twelve built-in variables: what makensis reserves for a script without Var.</summary>
  internal const int BuiltInVariables = 32;

  private const int ResourceDirectoryIndex = 2;
  private const uint DiscardableSection = 0x02000000;
  private const ushort DllCharacteristics = 0x8540; // TERMINAL_SERVER_AWARE | NX_COMPAT | DYNAMIC_BASE | NO_SEH
  private const int ManifestType = 24;
  private const int EnglishUs = 1033;

  // makensis's default manifest for RequestExecutionLevel user: run as the invoking user, no UAC prompt.
  private static readonly byte[] Manifest = Encoding.UTF8.GetBytes(
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
    "<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\">" +
    "<assemblyIdentity version=\"1.0.0.0\" processorArchitecture=\"*\" name=\"Nullsoft.NSIS.exehead\" type=\"win32\"/>" +
    "<description>Nullsoft Install System</description>" +
    "<trustInfo xmlns=\"urn:schemas-microsoft-com:asm.v3\"><security><requestedPrivileges>" +
    "<requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\"/>" +
    "</requestedPrivileges></security></trustInfo>" +
    "<compatibility xmlns=\"urn:schemas-microsoft-com:compatibility.v1\"><application>" +
    "<supportedOS Id=\"{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}\"/><supportedOS Id=\"{1f676c76-80e1-4239-95bb-83d0f6d0da78}\"/>" +
    "<supportedOS Id=\"{4a2f28e3-53b9-4441-ba9c-d69d4a4a6e38}\"/><supportedOS Id=\"{35138b9a-5d96-4fbd-8e2d-a2440225f93a}\"/>" +
    "</application></compatibility></assembly>");

  private sealed record Section(int HeaderOffset, string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer, uint Characteristics);

  /// <summary>A resource directory node: its children by name or id, or the leaf's data and code page.</summary>
  private sealed class Node {
    public readonly SortedDictionary<object, Node> Children = new(KeyOrder.Instance);
    public byte[]? Data;
    public uint CodePage;
  }

  // Named entries first, ordered case-insensitively, then ids ascending (the order the loader's binary search expects).
  private sealed class KeyOrder : IComparer<object> {
    public static readonly KeyOrder Instance = new();
    public int Compare(object? a, object? b) => (a, b) switch {
      (string x, string y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
      (string, _) => -1,
      (_, string) => 1,
      _ => ((int)a!).CompareTo((int)b!),
    };
  }

  /// <summary>True when the stub is a Unicode exehead (its imports are the wide Win32 functions).</summary>
  internal static bool IsUnicode(ReadOnlySpan<byte> stub) => stub.IndexOf("CreateDirectoryW"u8) >= 0;

  /// <summary>Returns the patched stub image, a multiple of 512 bytes long.</summary>
  /// <exception cref="ArgumentException">Not a PE32 NSIS 3 Unicode stub whose last two sections are .ndata and .rsrc.</exception>
  internal static byte[] Prepare(ReadOnlySpan<byte> stub) {
    if (stub.Length < 0x200 || stub[0] != 'M' || stub[1] != 'Z')
      throw new ArgumentException("The NSIS stub is not a PE file.", nameof(stub));
    if (!IsUnicode(stub))
      throw new ArgumentException("Only the Unicode NSIS 3 stubs (Stubs/*-x86-unicode) are supported.", nameof(stub));
    var image = stub.ToArray();
    var pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
    if (pe < 0 || pe > image.Length - 0xF8 || BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(pe)) != 0x00004550)
      throw new ArgumentException("The NSIS stub has no PE header.", nameof(stub));
    var opt = pe + 24;
    if (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(opt)) != 0x10B)
      throw new ArgumentException("The NSIS stub is not a 32-bit PE.", nameof(stub));
    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6));
    var table = opt + BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20));
    var sectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(opt + 32));
    var fileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(opt + 36));
    var sections = new List<Section>();
    for (var i = 0; i < sectionCount; ++i) {
      var h = table + 40 * i;
      var s = image.AsSpan(h);
      sections.Add(new(h, Encoding.ASCII.GetString(s[..8]).TrimEnd('\0'),
        BinaryPrimitives.ReadUInt32LittleEndian(s[8..]), BinaryPrimitives.ReadUInt32LittleEndian(s[12..]),
        BinaryPrimitives.ReadUInt32LittleEndian(s[16..]), BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
        BinaryPrimitives.ReadUInt32LittleEndian(s[36..])));
    }
    if (sections.Count < 2 || sections[^2].Name != ".ndata" || sections[^1].Name != ".rsrc")
      throw new ArgumentException("The NSIS stub does not end in the .ndata and .rsrc sections of an exehead.", nameof(stub));
    var ndata = sections[^2];
    var rsrc = sections[^1];
    if (rsrc.RawPointer + rsrc.RawSize > image.Length || rsrc.RawPointer % 512 != 0)
      throw new ArgumentException("The NSIS stub's resource section lies outside the file.", nameof(stub));

    var resources = ReadDirectory(image.AsSpan((int)rsrc.RawPointer, (int)rsrc.RawSize), 0, rsrc.VirtualAddress, 0);
    var manifest = new Node();
    manifest.Children[EnglishUs] = new Node { Data = Manifest };
    var manifestType = new Node();
    manifestType.Children[1] = manifest;
    resources.Children[ManifestType] = manifestType;

    // .ndata holds the variables: NSIS_MAX_STRLEN UTF-16 characters each.
    var ndataSize = (uint)(BuiltInVariables * MaxStringLength * 2);
    var rsrcVa = Align(ndata.VirtualAddress + ndataSize, sectionAlignment);
    var section = WriteDirectory(resources, rsrcVa);
    var rawSize = Align((uint)section.Length, fileAlignment);

    var result = new byte[rsrc.RawPointer + rawSize];
    image.AsSpan(0, (int)rsrc.RawPointer).CopyTo(result);
    section.CopyTo(result.AsSpan((int)rsrc.RawPointer));

    var span = result.AsSpan();
    BinaryPrimitives.WriteUInt32LittleEndian(span[(ndata.HeaderOffset + 8)..], ndataSize);
    BinaryPrimitives.WriteUInt32LittleEndian(span[(ndata.HeaderOffset + 36)..], ndata.Characteristics & ~DiscardableSection);
    BinaryPrimitives.WriteUInt32LittleEndian(span[(rsrc.HeaderOffset + 8)..], (uint)section.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(span[(rsrc.HeaderOffset + 12)..], rsrcVa);
    BinaryPrimitives.WriteUInt32LittleEndian(span[(rsrc.HeaderOffset + 16)..], rawSize);
    BinaryPrimitives.WriteUInt16LittleEndian(span[(opt + 0x28)..], 6); // MajorImageVersion
    BinaryPrimitives.WriteUInt16LittleEndian(span[(opt + 0x2A)..], 0); // MinorImageVersion
    BinaryPrimitives.WriteUInt32LittleEndian(span[(opt + 0x38)..], Align(rsrcVa + (uint)section.Length, sectionAlignment)); // SizeOfImage
    BinaryPrimitives.WriteUInt32LittleEndian(span[(opt + 0x40)..], 0); // CheckSum: makensis leaves it 0
    BinaryPrimitives.WriteUInt16LittleEndian(span[(opt + 0x46)..], DllCharacteristics);
    var directory = opt + 96 + 8 * ResourceDirectoryIndex;
    BinaryPrimitives.WriteUInt32LittleEndian(span[directory..], rsrcVa);
    BinaryPrimitives.WriteUInt32LittleEndian(span[(directory + 4)..], (uint)section.Length);
    if (result.Length % 512 != 0)
      throw new ArgumentException("The NSIS stub's file alignment is not a divisor of 512.", nameof(stub));
    return result;
  }

  private static uint Align(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;

  private static Node ReadDirectory(ReadOnlySpan<byte> section, int offset, uint sectionVa, int depth) {
    if (depth > 2 || offset < 0 || offset + 16 > section.Length)
      throw new ArgumentException("The NSIS stub's resource directory is malformed.");
    var node = new Node();
    var count = BinaryPrimitives.ReadUInt16LittleEndian(section[(offset + 12)..]) + BinaryPrimitives.ReadUInt16LittleEndian(section[(offset + 14)..]);
    for (var i = 0; i < count; ++i) {
      var e = offset + 16 + 8 * i;
      if (e + 8 > section.Length)
        throw new ArgumentException("The NSIS stub's resource directory is malformed.");
      var name = BinaryPrimitives.ReadUInt32LittleEndian(section[e..]);
      var child = BinaryPrimitives.ReadUInt32LittleEndian(section[(e + 4)..]);
      object key;
      if ((name & 0x80000000) != 0) {
        var p = (int)(name & 0x7FFFFFFF);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(section[p..]);
        key = Encoding.Unicode.GetString(section.Slice(p + 2, length * 2));
      } else
        key = (int)name;
      if ((child & 0x80000000) != 0)
        node.Children[key] = ReadDirectory(section, (int)(child & 0x7FFFFFFF), sectionVa, depth + 1);
      else {
        var leaf = (int)child;
        var rva = BinaryPrimitives.ReadUInt32LittleEndian(section[leaf..]);
        var size = BinaryPrimitives.ReadInt32LittleEndian(section[(leaf + 4)..]);
        var at = (long)rva - sectionVa;
        if (at < 0 || size < 0 || at + size > section.Length)
          throw new ArgumentException("A resource of the NSIS stub lies outside its resource section.");
        node.Children[key] = new Node { Data = section.Slice((int)at, size).ToArray(), CodePage = BinaryPrimitives.ReadUInt32LittleEndian(section[(leaf + 8)..]) };
      }
    }
    return node;
  }

  // Directory tables breadth-first, then the data entries, the names, and the data, 8-byte aligned.
  private static byte[] WriteDirectory(Node root, uint sectionVa) {
    var tables = new List<Node>();
    var tableOffset = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
    var queue = new Queue<Node>([root]);
    var position = 0;
    while (queue.Count > 0) {
      var n = queue.Dequeue();
      tables.Add(n);
      tableOffset[n] = position;
      position += 16 + 8 * n.Children.Count;
      foreach (var child in n.Children.Values)
        if (child.Data == null)
          queue.Enqueue(child);
    }
    var leaves = new List<Node>();
    Collect(root, leaves);
    var entryOffset = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
    foreach (var leaf in leaves) {
      entryOffset[leaf] = position;
      position += 16;
    }
    var nameOffset = new Dictionary<string, int>();
    foreach (var n in tables)
      foreach (var key in n.Children.Keys)
        if (key is string s && !nameOffset.ContainsKey(s)) {
          nameOffset[s] = position;
          position += 2 + 2 * s.Length;
        }
    position = (position + 7) & ~7;
    var dataOffset = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
    foreach (var leaf in leaves) {
      dataOffset[leaf] = position;
      position = (position + leaf.Data!.Length + 7) & ~7;
    }

    var output = new byte[position];
    var span = output.AsSpan();
    foreach (var n in tables) {
      var o = tableOffset[n];
      var named = n.Children.Keys.Count(k => k is string);
      BinaryPrimitives.WriteUInt16LittleEndian(span[(o + 12)..], (ushort)named);
      BinaryPrimitives.WriteUInt16LittleEndian(span[(o + 14)..], (ushort)(n.Children.Count - named));
      var i = 0;
      foreach (var (key, child) in n.Children) {
        var e = o + 16 + 8 * i++;
        var id = key is string s ? (uint)nameOffset[s] | 0x80000000 : (uint)(int)key;
        var target = child.Data == null ? (uint)tableOffset[child] | 0x80000000 : (uint)entryOffset[child];
        BinaryPrimitives.WriteUInt32LittleEndian(span[e..], id);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(e + 4)..], target);
      }
    }
    foreach (var leaf in leaves) {
      var e = entryOffset[leaf];
      BinaryPrimitives.WriteUInt32LittleEndian(span[e..], sectionVa + (uint)dataOffset[leaf]);
      BinaryPrimitives.WriteInt32LittleEndian(span[(e + 4)..], leaf.Data!.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(span[(e + 8)..], leaf.CodePage);
      leaf.Data.CopyTo(span[dataOffset[leaf]..]);
    }
    foreach (var (name, o) in nameOffset) {
      BinaryPrimitives.WriteUInt16LittleEndian(span[o..], (ushort)name.Length);
      Encoding.Unicode.GetBytes(name).CopyTo(span[(o + 2)..]);
    }
    return output;

    static void Collect(Node n, List<Node> leaves) {
      foreach (var child in n.Children.Values)
        if (child.Data != null)
          leaves.Add(child);
        else
          Collect(child, leaves);
    }
  }
}
