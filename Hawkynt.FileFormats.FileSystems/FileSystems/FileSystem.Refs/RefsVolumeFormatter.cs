using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>What a new ReFS volume is made with.</summary>
/// <param name="Bytes">The target's size: from <see cref="RefsVolumeFormatter.Geometry"/> up to one 64 MiB container more (the rest stays unused).</param>
/// <param name="Label">The volume label (up to 32 UTF-16 code units).</param>
/// <param name="Created">The format time; now when null.</param>
/// <param name="Seed">Seeds the volume's identifiers (signature, GUIDs, serial); random when null.</param>
public sealed record RefsFormatOptions(long Bytes, string Label = "", DateTime? Created = null, int? Seed = null);

/// <summary>
/// Creates an empty ReFS 3.7 volume with 4 KiB clusters, laid out as Windows Server 2022 lays out
/// a freshly formatted volume of the same size (measured on volumes it formatted, then mounted
/// read-write and dismounted): the boot sector and its copy, three superblocks, two checkpoints,
/// the metadata log, and every system table — object table, allocators, container table, schema
/// table, upcase table, logfile information, volume information, security descriptors, the
/// metadata directory, the root directory and System Volume Information — each built from its
/// rows, with every page checksum, checkpoint and superblock checksum computed. The volume's
/// identifiers (signature, GUIDs, serial number) and its timestamps are its own.
/// </summary>
/// <remarks>
/// Made: ReFS 3.7, 4096-byte clusters, 15 containers (<see cref="Geometry"/> bytes), layout in docs/REFS-FRESH-VOLUME-ON-DISK.md.
/// Everything else is refused before a byte is written.
/// </remarks>
public static class RefsVolumeFormatter {
  private const int ClusterSize = 4096;
  private const int PageSize = 16384;
  private const int PageClusters = PageSize / ClusterSize;
  private const ulong TotalClusters = 0x3C000;
  private const ulong ClustersPerContainer = 0x4000;
  private const int ContainerShift = 15;

  /// <summary>The one geometry made so far: 0x3C000 clusters of 4 KiB (960 MiB, the volume Windows makes on a 1 GiB disk).</summary>
  public const long Geometry = (long)TotalClusters * ClusterSize;

  /// <summary>Container index → first physical cluster, as the 1 GiB layout maps them (container 4 sits last).</summary>
  private static readonly (ulong Container, ulong Base, uint Class)[] Containers = [
    (2, 0x00000, 0x0001), (3, 0x04000, 0x0001), (4, 0x38000, 0x0001), (5, 0x08000, 0x4001), (6, 0x0C000, 0x4001),
    (7, 0x10000, 0x4001), (8, 0x14000, 0x4001), (9, 0x18000, 0x4001), (0xA, 0x1C000, 0x0001), (0xB, 0x20000, 0x4200),
    (0xC, 0x24000, 0x4200), (0xD, 0x28000, 0x2040), (0xE, 0x2C000, 0x2000), (0xF, 0x30000, 0x2000), (0x10, 0x34000, 0x2000),
  ];

  private const ulong SuperblockLcn = 0x1E;
  private static readonly ulong[] CheckpointLcns = [0x9BC, 0x7350];
  private const ulong LogDataStart = 0xC000;
  private const ulong LogDataEnd = 0x1C000;
  private static readonly ulong[] LogControlLcns = [0x1C000, 0x1C001];

  /// <summary>Writes a new, empty volume over <paramref name="target"/>; refuses (writing nothing) any other geometry.</summary>
  public static void Format(Stream target, RefsFormatOptions options) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);
    if (!target.CanSeek || !target.CanWrite) throw new IOException("ReFS: the target must be seekable and writable.");
    if (options.Bytes < Geometry || options.Bytes >= Geometry + (long)(ClustersPerContainer * ClusterSize))
      throw new NotSupportedException($"ReFS: only a volume of 15 containers (0x3C000 clusters of 4 KiB) is made so far, from {Geometry} bytes up to one container more; the rest of a larger target stays unused, as Windows leaves it.");
    if (options.Label.Length > 32) throw new ArgumentException("ReFS: the label is longer than 32 characters.", nameof(options));

    var random = options.Seed is { } seed ? new Random(seed) : new Random();
    var identity = new Identity(random, options.Created ?? DateTime.UtcNow);
    var volume = new VolumeBuilder(identity, options.Label);
    volume.Build();
    // A target that held anything starts again from zeros.
    target.SetLength(0);
    target.SetLength(options.Bytes);
    volume.WriteTo(target);
    target.Flush();
  }

  /// <summary>The volume's own identifiers: page signature (every page header +0x0C), volume GUID, log GUID, serial and the format time.</summary>
  private sealed class Identity {
    public Identity(Random random, DateTime created) {
      this.VolumeGuid = new byte[16];
      random.NextBytes(this.VolumeGuid);
      // Every page, superblock, checkpoint and log block carries the XOR of the volume GUID's four
      // dwords (measured: 9BB8118C-E5DC-3E49-89B8-2F01000E4265 gives 0xA142D2F7).
      for (var i = 0; i < 16; i += 4) this.Signature ^= BinaryPrimitives.ReadUInt32LittleEndian(this.VolumeGuid.AsSpan(i));
      this.LogGuid = new byte[16];
      random.NextBytes(this.LogGuid);
      var serial = new byte[8];
      random.NextBytes(serial);
      this.Serial = BinaryPrimitives.ReadUInt64LittleEndian(serial);
      this.FileTime = (ulong)created.ToFileTimeUtc();
    }

    public uint Signature { get; }
    public byte[] VolumeGuid { get; }
    public byte[] LogGuid { get; }
    public ulong Serial { get; }
    public ulong FileTime { get; }
  }

  /// <summary>One row of an MSB+ node: key, value, row flags, and whether the key is stored as the value's first bytes.</summary>
  private sealed record Row(byte[] Key, byte[] Value, ushort Flags = 0, bool KeyInValue = false, uint Tag = 0xFFFF0000u);

  /// <summary>
  /// One table's page, as Windows lays it out: the page header (signature, clock at 0x10, a
  /// per-table count at 0x18, self addresses, table id at 0x48), the node offset at 0x50, for a
  /// root the table descriptor at 0x54 and its table header (measured per table), and the node.
  /// </summary>
  private sealed class Page {
    public required ulong Table { get; init; }
    public required ulong Physical { get; init; }
    public required bool Virtual { get; init; }
    public ulong Clock { get; init; } = 1;
    public ulong Field18 { get; init; }
    public required int NodeOffset { get; init; }
    public int NodeEnd { get; init; } = PageSize;
    public uint NodeFlags { get; init; } = 0x200;
    /// <summary>Bytes 0x54 up to the node: the descriptor and table header; empty for a leaf below a root.</summary>
    public byte[] Header { get; init; } = [];
    public List<Row> Rows { get; } = [];
    public byte[] Bytes { get; set; } = [];
  }

  private sealed class VolumeBuilder(Identity identity, string label) {
    private readonly Dictionary<ulong, Page> _pages = [];
    private readonly List<(long Offset, byte[] Bytes)> _writes = [];

    private static ulong ToVirtual(ulong physical) {
      foreach (var (container, start, _) in Containers)
        if (physical >= start && physical < start + ClustersPerContainer)
          return (container << ContainerShift) | (physical - start);
      throw new InvalidOperationException($"ReFS: cluster 0x{physical:X} lies in no container.");
    }

    public void Build() {
      // Page placement measured on the 1 GiB layout (physical first cluster of each page).
      var smallAllocator = this.Add(SystemRoot(0x22, 0x24, virtualAddress: false, nodeFlags: 0x600, schema: 0xE010, kind: 0xE010,
        header: Fields((0x80, 0x400CUL), (0x88, 0x4000UL))));
      var containerTable = this.Add(SystemRoot(0xB, 0x28, virtualAddress: false, nodeFlags: 0x200, schema: 0xE0C0,
        header: Fields((0x78, 0x10UL), (0x80, 0x00007FFFFFFFFF00UL), (0x88, ClustersPerContainer), (0xA0, (ulong)Containers.Length))));
      var containerTableCopy = this.Add(SystemRoot(0xC, 0x2C, virtualAddress: false, nodeFlags: 0x200, schema: 0xE0C0,
        header: Fields((0x78, 0x10UL), (0x80, 0x00007FFFFFFFFF00UL), (0x88, ClustersPerContainer), (0xA0, (ulong)Containers.Length))));
      var security = this.Add(ObjectRoot(0x530, 0x30, clock: 0xB, field18: 0, schema: 0x140, nodeOffset: 0x138));
      var volumeInfo = this.Add(ObjectRoot(0x500, 0x34, clock: 0xA, field18: 0, schema: 0x150, nodeOffset: 0x138));
      var systemDirectory = this.Add(DirectoryRoot(0x520, 0x1A04, clock: 0x8, nodeOffsetFlags: 0x0001));
      var svi = this.Add(DirectoryRoot(0x701, 0x200C, clock: 0xB, nodeOffsetFlags: 0));
      var parentChild = this.Add(SystemRoot(0x3, 0x2200, virtualAddress: true, nodeFlags: 0x200, schema: 0xE040, clock: 0xC));
      var empty541 = this.Add(ObjectRoot(0x541, 0x2400, clock: 0x8, field18: 4, schema: 0x160, nodeOffset: 0x138));
      var volumeInfoCopy = this.Add(ObjectRoot(0x501, 0x2404, clock: 0xA, field18: 0, schema: 0x150, nodeOffset: 0x138));
      var root = this.Add(DirectoryRoot(0x600, 0x2604, clock: 0xB, nodeOffsetFlags: 0));
      var objects = this.Add(SystemRoot(0x2, 0x2A04, virtualAddress: true, nodeFlags: 0x200, schema: 0xE030, clock: 0xC,
        header: Fields((0x78, 1UL), (0x88, 0x701UL))));
      var objectsCopy = this.Add(SystemRoot(0x4, 0x3200, virtualAddress: true, nodeFlags: 0x200, schema: 0xE030, clock: 0xC,
        header: Fields((0x78, 1UL), (0x88, 0x701UL))));
      var empty540 = this.Add(ObjectRoot(0x540, 0x3400, clock: 0x8, field18: 4, schema: 0x160, nodeOffset: 0x138));
      var tableE = this.Add(SystemRoot(0xE, 0x1C004, virtualAddress: true, nodeFlags: 0x200, schema: 0xE100));
      var schemaRoot = this.Add(InnerRoot(0x1, 0x1C014, schema: 0xE060, nodeOffset: 0x140, nodeEnd: 0x140 + 0xCA8));
      var schemaRootCopy = this.Add(InnerRoot(0x6, 0x1C018, schema: 0xE060, nodeOffset: 0x140, nodeEnd: 0x140 + 0xCA8));
      var upcaseCopy = this.Add(InnerRoot(0x8, 0x1C01C, schema: 0xE090, nodeOffset: 0xC0, nodeEnd: PageSize, clock: 0));
      var upcase = this.Add(InnerRoot(0x7, 0x1C020, schema: 0xE090, nodeOffset: 0xC0, nodeEnd: PageSize, clock: 0));
      // The upcase leaves of the two copies interleave: copy, copy, primary, primary, then alternating.
      var upcaseLeafPlacement = new (ulong Table, ulong Physical)[] {
        (8, 0x1C024), (8, 0x1C028), (7, 0x1C02C), (7, 0x1C030), (8, 0x1C034), (7, 0x1C038), (8, 0x1C03C), (7, 0x1C040),
        (8, 0x1C044), (7, 0x1C048), (8, 0x1C04C), (7, 0x1C050), (8, 0x1C054), (7, 0x1C058), (8, 0x1C05C), (7, 0x1C060),
        (8, 0x1C064), (7, 0x1C068), (8, 0x1C06C), (7, 0x1C070),
      };
      var logInfoCopy = this.Add(LogRoot(0xA, 0x1C074));
      var logInfo = this.Add(LogRoot(0x9, 0x1C078));
      var emptyD = this.Add(ObjectRoot(0xD, 0x1C07C, clock: 0, field18: 4, schema: 0xE0D0, nodeOffset: 0x138, kind: 0));
      var schemaLeaf = this.Add(Leaf(0x1, 0x1C080, clock: 1));
      var schemaLeafCopy = this.Add(Leaf(0x6, 0x1C084, clock: 1));
      var tableF = this.Add(SystemRoot(0xF, 0x20008, virtualAddress: true, nodeFlags: 0x600, schema: 0xE080));
      var blockRefcount = this.Add(SystemRoot(0x5, 0x2000C, virtualAddress: true, nodeFlags: 0x600, schema: 0xE0B0));
      var containerAllocator = this.Add(SystemRoot(0x20, 0x23000, virtualAddress: true, nodeFlags: 0x600, schema: 0xE010, kind: 0xE010, clock: 0xD,
        header: Fields((0x80, 0x8000UL))));
      var mediumAllocator = this.Add(SystemRoot(0x21, 0x23800, virtualAddress: true, nodeFlags: 0x600, schema: 0xE010, kind: 0xE010, clock: 0xD,
        header: Fields((0x78, 2UL), (0x80, TotalClusters), (0x98, 0x1C0E4UL), (0xA0, 0x10000UL), (0xA8, 0x500UL), (0xB8, 0x8000UL),
          (0xC0, 0x80000029UL), (0xC8, 0x0000000340000000UL), (0xD0, 0x1290000UL))));

      // Rows.
      foreach (var (container, start, cls) in Containers) {
        var value = new byte[0xA0];
        W64(value, 0, container);
        W32(value, 0x0C, 1);
        W32(value, 0x10, 1);
        W32(value, 0x14, cls);
        W32(value, 0x18, (uint)ClustersPerContainer);
        value.AsSpan(0x20, 0x10).Fill(0xFF);
        W32(value, 0x4C, 1);
        W64(value, 0x90, start);
        W64(value, 0x98, ClustersPerContainer);
        containerTable.Rows.Add(new Row(value[..0x10], value, KeyInValue: true));
        containerTableCopy.Rows.Add(new Row(value[..0x10], value.ToArray(), KeyInValue: true));
      }

      foreach (var (key, hex) in RefsFormatConstants.Schemas) {
        schemaLeaf.Rows.Add(new Row(U32Key(key), Convert.FromHexString(hex)));
        schemaLeafCopy.Rows.Add(new Row(U32Key(key), Convert.FromHexString(hex)));
      }

      foreach (var (key, hex) in RefsFormatConstants.SecurityDescriptors)
        security.Rows.Add(new Row(Convert.FromHexString(key), Convert.FromHexString(hex)));

      var labelBytes = Encoding.Unicode.GetBytes(label);
      foreach (var info in new[] { volumeInfo, volumeInfoCopy }) {
        foreach (var (flags, key, value) in RefsFormatConstants.VolumeInformation) {
          var bytes = BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(key)) == 0x510 ? labelBytes : this.Times(value);
          info.Rows.Add(new Row(Convert.FromHexString(key), bytes, flags));
        }
      }
      foreach (var (flags, key, value) in RefsFormatConstants.SystemDirectory)
        systemDirectory.Rows.Add(new Row(Convert.FromHexString(key), this.Times(value), flags));
      foreach (var (flags, key, value) in RefsFormatConstants.RootDirectory)
        root.Rows.Add(new Row(Convert.FromHexString(key), this.Times(value), flags));
      foreach (var (flags, key, value) in RefsFormatConstants.SystemVolumeInformation)
        svi.Rows.Add(new Row(Convert.FromHexString(key), this.Times(value), flags));

      // The root directory's children in the parent-child table: the metadata directory and SVI.
      foreach (var child in new ulong[] { 0x520, 0x701 }) {
        var value = new byte[0x20];
        W64(value, 0x08, 0x600);
        W64(value, 0x18, child);
        parentChild.Rows.Add(new Row(value.ToArray(), value, KeyInValue: true));
      }

      // Table F: one range covering the volume.
      {
        var value = new byte[0x18];
        W64(value, 0x08, TotalClusters);
        W32(value, 0x10, 0x0002FFFF);
        W32(value, 0x14, 0x00000100);
        tableF.Rows.Add(new Row(value[..0x10], value, KeyInValue: true));
      }

      // Logfile information: name and the log's place.
      foreach (var info in new[] { logInfo, logInfoCopy }) {
        info.Rows.Add(new Row(U32Key(0), Encoding.ASCII.GetBytes("Logfile Information Table")));
        var value = new byte[0x30];
        W64(value, 0x00, LogDataStart);
        W64(value, 0x08, LogDataEnd);
        W64(value, 0x10, LogDataEnd - LogDataStart);
        W64(value, 0x18, LogControlLcns[0]);
        W64(value, 0x20, LogControlLcns[1]);
        info.Rows.Add(new Row(U32Key(1), value));
      }

      // The upcase table: a name row, the entry count, then the 64 Ki code units in rows of 341 bytes.
      var upcaseRows = UpcaseRows();
      var leafRowCounts = new[] { 44, 42, 42, 42, 42, 42, 42, 42, 42, 7 };
      foreach (var (table, rootPage) in new[] { (8UL, upcaseCopy), (7UL, upcase) }) {
        var placements = upcaseLeafPlacement.Where(p => p.Table == table).Select(p => p.Physical).ToList();
        var at = 0;
        for (var i = 0; i < placements.Count; ++i) {
          var leaf = this.Add(Leaf(table, placements[i], clock: 0));
          leaf.Rows.AddRange(upcaseRows.Skip(at).Take(leafRowCounts[i]));
          at += leafRowCounts[i];
        }
      }

      // Object table rows reference every object table; their page references are filled in
      // once the pages are built.
      this._objectPages = new (ulong Oid, Page Page, ulong Trailer, ulong Lsn)[] {
        (0x7, upcase, 0, 0), (0x8, upcaseCopy, 0, 0), (0x9, logInfo, 0, 0), (0xA, logInfoCopy, 0, 0), (0xD, emptyD, 0, 0x1),
        (0x500, volumeInfo, 0, 0x10000000E), (0x501, volumeInfoCopy, 0, 0x10000000E), (0x520, systemDirectory, 0x500, 0x10000000A),
        (0x530, security, 0, 0x10000000E), (0x540, empty540, 0, 0x10000000A), (0x541, empty541, 0, 0x10000000A),
        (0x600, root, 1, 0x10000000E), (0x701, svi, 1, 0x10000000E),
      };
      this._objects = objects;
      this._objectsCopy = objectsCopy;
      this._roots = [
        objects, mediumAllocator, containerAllocator, schemaRoot, parentChild, objectsCopy, blockRefcount,
        containerTable, containerTableCopy, schemaRootCopy, tableE, tableF, smallAllocator,
      ];
      this._allocators = (smallAllocator, containerAllocator, mediumAllocator);
      this._innerChildren = new Dictionary<Page, List<(byte[] Separator, Page Child)>> {
        [schemaRoot] = [([], schemaLeaf)],
        [schemaRootCopy] = [([], schemaLeafCopy)],
      };
      foreach (var (table, rootPage) in new[] { (8UL, upcaseCopy), (7UL, upcase) }) {
        var children = new List<(byte[], Page)>();
        var leaves = upcaseLeafPlacement.Where(p => p.Table == table).Select(p => this._pages[p.Physical]).ToList();
        for (var i = 0; i < leaves.Count; ++i)
          children.Add((i == leaves.Count - 1 ? [] : leaves[i].Rows[^1].Key, leaves[i]));
        this._innerChildren[rootPage] = children;
      }
    }

    private (ulong Oid, Page Page, ulong Trailer, ulong Lsn)[] _objectPages = [];
    private Page _objects = null!;
    private Page _objectsCopy = null!;
    private Page[] _roots = [];
    private (Page Small, Page Container, Page Medium) _allocators;
    private Dictionary<Page, List<(byte[] Separator, Page Child)>> _innerChildren = [];

    private byte[] Times(string hex) => Convert.FromHexString(hex.Replace("{T}", Convert.ToHexString(BitConverter.GetBytes(identity.FileTime))));

    private Page Add(Page page) {
      this._pages.Add(page.Physical, page);
      return page;
    }

    public void WriteTo(Stream target) {
      // Allocators first: their rows depend on which clusters every structure takes.
      this.FillAllocators();
      // Pages bottom-up: leaves, inner roots referencing them, object tables, then the rest.
      foreach (var page in this._pages.Values.Where(p => p.NodeFlags == 0)) this.Pack(page);
      foreach (var (rootPage, children) in this._innerChildren) {
        foreach (var (separator, child) in children)
          rootPage.Rows.Add(new Row(separator, this.Reference(child), Flags: (ushort)(separator.Length == 0 ? 2 : 0), KeyInValue: separator.Length == 0));
        this.Pack(rootPage);
      }
      foreach (var page in this._pages.Values.Where(p => p.Bytes.Length == 0 && p != this._objects && p != this._objectsCopy)) this.Pack(page);
      foreach (var table in new[] { this._objects, this._objectsCopy }) {
        foreach (var (oid, page, trailer, lsn) in this._objectPages) {
          var key = new byte[16];
          W64(key, 8, oid);
          var value = new byte[trailer == 0 ? 0xF0 : 0xF8];
          W64(value, 0x00, 2);
          W32(value, 0x08, 0x18);
          W32(value, 0x0C, 0x30);
          W32(value, 0x10, 0xC8);
          W32(value, 0x14, trailer == 0 ? 0u : 8u);
          W64(value, 0x18, lsn);
          this.Reference(page).CopyTo(value, 0x20);
          if (trailer != 0) W64(value, 0xC8, trailer);
          table.Rows.Add(new Row(key, value));
        }
        this.Pack(table);
      }

      foreach (var page in this._pages.Values) this.Place(page);
      this.WriteLog();
      this.WriteCheckpoints();
      this.WriteSuperblocks();
      this.WriteBoot();
      target.Position = 0;
      foreach (var (offset, bytes) in this._writes) {
        target.Position = offset;
        target.Write(bytes);
      }
    }

    /// <summary>A page reference to <paramref name="page"/>: its four addresses, CRC64 descriptor and digest (0x30 bytes).</summary>
    private byte[] Reference(Page page) {
      var reference = new byte[0x30];
      for (var i = 0; i < PageClusters; ++i) {
        var physical = page.Physical + (ulong)i;
        W64(reference, i * 8, page.Virtual ? ToVirtual(physical) : physical);
      }
      reference[0x22] = 2;
      reference[0x23] = 8;
      W32(reference, 0x24, 8);
      RefsChecksum.RefreshPageReference(reference, page.Bytes);
      return reference;
    }

    private void Pack(Page page) {
      var bytes = new byte[PageSize];
      "MSB+"u8.CopyTo(bytes);
      W32(bytes, 0x04, 2);
      W32(bytes, 0x0C, identity.Signature);
      W64(bytes, 0x10, page.Clock);
      W64(bytes, 0x18, page.Field18);
      for (var i = 0; i < PageClusters; ++i) {
        var physical = page.Physical + (ulong)i;
        W64(bytes, 0x20 + i * 8, page.Virtual ? ToVirtual(physical) : physical);
      }
      W64(bytes, 0x48, page.Table);
      W32(bytes, 0x50, (uint)(page.NodeOffset - 0x50));
      page.Header.CopyTo(bytes, 0x54);
      var node = page.NodeOffset;
      var indexEnd = page.NodeEnd - node;
      var cursor = 0x28;
      var index = new List<uint>();
      foreach (var row in page.Rows) {
        var serialized = Serialize(row);
        serialized.CopyTo(bytes, node + cursor);
        index.Add(row.Tag | (uint)cursor);
        cursor += serialized.Length;
      }
      var indexStart = indexEnd - index.Count * 4;
      if (cursor > indexStart) throw new InvalidOperationException($"ReFS: table 0x{page.Table:X} does not fit its page.");
      for (var i = 0; i < index.Count; ++i) W32(bytes, node + indexStart + i * 4, index[i]);
      W32(bytes, node + 0x00, 0x28);
      W32(bytes, node + 0x04, (uint)cursor);
      W32(bytes, node + 0x08, (uint)(indexStart - cursor));
      W32(bytes, node + 0x0C, page.NodeFlags);
      W32(bytes, node + 0x10, (uint)indexStart);
      W32(bytes, node + 0x14, (uint)index.Count);
      W32(bytes, node + 0x20, (uint)indexEnd);
      if (page.Header.Length >= 0x24) {
        // The descriptor's leaf-page and row counts.
        var leaves = this._innerChildren.TryGetValue(page, out var children) ? (ulong)children.Count : 0;
        var rows = this._innerChildren.TryGetValue(page, out children) ? (ulong)children.Sum(c => c.Child.Rows.Count) : (ulong)page.Rows.Count;
        W64(bytes, 0x68, leaves);
        W64(bytes, 0x70, rows);
      }
      page.Bytes = bytes;
    }

    private static byte[] Serialize(Row row) {
      var keyOffset = 0x10;
      var valueOffset = row.KeyInValue ? keyOffset : Align8(keyOffset + row.Key.Length);
      var size = Align8(valueOffset + row.Value.Length);
      var bytes = new byte[size];
      W32(bytes, 0, (uint)size);
      W16(bytes, 4, (ushort)keyOffset);
      W16(bytes, 6, (ushort)row.Key.Length);
      W16(bytes, 8, row.Flags);
      W16(bytes, 10, (ushort)valueOffset);
      W16(bytes, 12, (ushort)row.Value.Length);
      if (!row.KeyInValue) row.Key.CopyTo(bytes, keyOffset);
      row.Value.CopyTo(bytes, valueOffset);
      return bytes;
    }

    private void Place(Page page) => this._writes.Add(((long)page.Physical * ClusterSize, page.Bytes));

    /// <summary>Clusters every structure takes, by physical LCN.</summary>
    private HashSet<ulong> Used() {
      var used = new HashSet<ulong>();
      void Range(ulong start, ulong count) { for (var i = 0UL; i < count; ++i) used.Add(start + i); }
      Range(0, 0x24);                // the boot area: boot sector, superblock and the clusters up to the first table
      Range(TotalClusters - 4, 4);   // the superblock copies and the boot sector copy at the volume's end
      foreach (var lcn in CheckpointLcns) Range(lcn, PageClusters);
      Range(LogDataStart, LogDataEnd - LogDataStart);
      Range(LogControlLcns[0], 4);   // the log's control area: two control blocks in a page
      foreach (var page in this._pages.Values) Range(page.Physical, PageClusters);
      return used;
    }

    private void FillAllocators() {
      var used = this.Used();
      var (small, container, medium) = this._allocators;
      // The Small Allocator: the boot tables (0x24..0x30) and container 5's band, physically addressed.
      small.Rows.Add(this.BitmapRow(used, 0x24, 0xC, 0x118, shortBitmap: true));
      small.Rows.Add(this.BitmapRow(used, 0x8000, 0x4000, 0x118, shortBitmap: false, bitmapFlags: 0x0001));
      // The Container Allocator: the band at 0x20000 holding the allocator and refcount pages.
      container.Rows.Add(this.BitmapRow(used, 0x20000, 0x4000, 0x118, shortBitmap: false, tail: 0x60));
      container.Rows.Add(this.BitmapRow(used, 0x24000, 0x4000, 0x118, shortBitmap: false));
      // The Medium Allocator: the whole volume; bands owned by the other tiers are compact rows.
      medium.Rows.Add(this.BitmapRow(used, 0x00000, 0x4000, 0x218, shortBitmap: false, tail: 0x60));
      medium.Rows.Add(this.BitmapRow(used, 0x04000, 0x4000, 0x218, shortBitmap: false, tail: 0x04));
      medium.Rows.Add(CompactRow(0x08000, 0x4000, allocated: true));
      medium.Rows.Add(CompactRow(0x0C000, 0x10000, allocated: true));
      medium.Rows.Add(this.BitmapRow(used, 0x1C000, 0x4000, 0x218, shortBitmap: false, tail: 0x7C));
      medium.Rows.Add(CompactRow(0x20000, 0x4000, allocated: true));
      medium.Rows.Add(CompactRow(0x24000, 0x4000, allocated: true));
      medium.Rows.Add(this.BitmapRow(used, 0x28000, 0x4000, 0x218, shortBitmap: false, bitmapFlags: 0x0005));
      medium.Rows.Add(CompactRow(0x2C000, 0xC000, allocated: false));
      medium.Rows.Add(this.BitmapRow(used, 0x38000, 0x4000, 0x218, shortBitmap: false, tail: 0x04));
      // Free counts in the allocator headers (u64 at 0x88).
      foreach (var allocator in new[] { small, container, medium })
        W64(allocator.Header, 0x88 - 0x54, allocator.Rows.Aggregate(0UL, (sum, r) => sum + FreeOf(r.Value)));
    }

    /// <summary>A row's free clusters: a bitmap row's count; a compact row's whole length when free (+0x16 zero), none when allocated.</summary>
    private static ulong FreeOf(byte[] value) {
      var length = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(8));
      return R16(value, 0x12) == 2
        ? (R16(value, 0x16) == 0 ? length : 0)
        : R16(value, 0x10);
    }

    /// <summary>A bitmap row [start, length]: free count (+0x10), flags 1 (or <paramref name="bitmapFlags"/>), a measured header word and tail word, then one bit per cluster.</summary>
    private Row BitmapRow(HashSet<ulong> used, ulong start, ulong length, ushort headerWord, bool shortBitmap, ushort bitmapFlags = 0x0001, ushort tail = 0) {
      var bitmapBytes = shortBitmap ? Align8((int)((length + 7) / 8)) : 0x800;
      var value = new byte[0x18 + bitmapBytes];
      W64(value, 0, start);
      W64(value, 8, length);
      var allocated = 0;
      for (var i = 0UL; i < length; ++i) {
        if (!used.Contains(start + i)) continue;
        value[0x18 + (int)(i >> 3)] |= (byte)(1 << (int)(i & 7));
        ++allocated;
      }
      // A short bitmap's bits past the row's length, up to the next 16, are set, as Windows writes them.
      if (shortBitmap)
        for (var i = length; i < (length + 15) / 16 * 16; ++i)
          value[0x18 + (int)(i >> 3)] |= (byte)(1 << (int)(i & 7));
      W16(value, 0x10, (ushort)(length - (ulong)allocated));
      W16(value, 0x12, allocated == 0 && bitmapFlags == 0x0005 ? (ushort)0x0005 : (ushort)0x0001);
      W16(value, 0x14, headerWord);
      W16(value, 0x16, tail);
      return new Row(value[..0x10], value, KeyInValue: true);
    }

    private static Row CompactRow(ulong start, ulong length, bool allocated) {
      var value = new byte[0x18];
      W64(value, 0, start);
      W64(value, 8, length);
      W16(value, 0x10, allocated ? (ushort)0 : (ushort)length);
      W16(value, 0x12, 2);
      W16(value, 0x14, 0x200);
      W16(value, 0x16, allocated ? (ushort)0xFFFF : (ushort)0);
      return new Row(value[..0x10], value, KeyInValue: true);
    }

    private void WriteLog() {
      for (var i = 0; i < LogControlLcns.Length; ++i) {
        var block = new byte[ClusterSize];
        "MLog"u8.CopyTo(block);
        W32(block, 0x04, identity.Signature);
        W32(block, 0x08, 1);
        W32(block, 0x0C, 0x1000);
        identity.LogGuid.CopyTo(block, 0x10);
        W64(block, 0x20, 2);
        W32(block, 0x38, 1);
        W32(block, 0x3C, 1);
        W32(block, 0x54, 0x78);
        W64(block, 0x98, 0xE48);
        W64(block, 0xA0, 0x38);
        W64(block, 0xA8, 1);
        W64(block, 0xB0, i == 0 ? 0x11UL : 0x10UL);
        W64(block, 0xB8, LogDataStart);
        W64(block, 0xC0, LogDataEnd);
        W64(block, 0xC8, 0x10000000E);
        W64(block, 0xD0, 0x10000000E);
        identity.LogGuid.CopyTo(block, 0xD8);
        W32(block, 0xE8, 2);
        W32(block, 0xEC, 1);
        W32(block, 0xF0, 1);
        W32(block, 0xF4, i == 0 ? 0xEu : 0xDu);
        W64(block, 0x80, FoldQwords(block));
        this._writes.Add(((long)LogControlLcns[i] * ClusterSize, block));
      }
    }

    /// <summary>A control block's checksum: the XOR of all its dwords with the checksum field zero (measured on Windows' control blocks).</summary>
    private static ulong FoldQwords(byte[] block) {
      uint result = 0;
      for (var i = 0; i < block.Length; i += 4) result ^= BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i));
      return result;
    }

    private void WriteCheckpoints() {
      for (var c = 0; c < CheckpointLcns.Length; ++c) {
        var bytes = new byte[PageSize];
        "CHKP"u8.CopyTo(bytes);
        W32(bytes, 0x04, 2);
        W32(bytes, 0x0C, identity.Signature);
        var clock = c == 0 ? 0xEUL : 0xFUL;
        W64(bytes, 0x10, clock);
        W64(bytes, 0x20, CheckpointLcns[c]);
        W16(bytes, 0x54, 3);
        W16(bytes, 0x56, 7);
        W32(bytes, 0x58, 0xD0);
        W32(bytes, 0x5C, 0x68);
        W64(bytes, 0x60, clock);
        W64(bytes, 0x68, c == 0 ? 0xAUL : 0xBUL);
        W64(bytes, 0x70, 0x10000000E);
        W32(bytes, 0x78, 2);
        W32(bytes, 0x88, 0x680);
        W32(bytes, 0x8C, 0x20);
        W32(bytes, 0x90, (uint)this._roots.Length);
        for (var r = 0; r < this._roots.Length; ++r) {
          var offset = 0x138 + r * 0x68;
          W32(bytes, 0x94 + r * 4, (uint)offset);
          this.Reference(this._roots[r]).CopyTo(bytes, offset);
        }
        // Self checksum descriptor at 0xD0: the page's own LCN, then a CRC32 (type 1, 4 bytes) at 0xF4.
        W64(bytes, 0xD0, CheckpointLcns[c]);
        bytes[0xF2] = 1;
        bytes[0xF3] = 8;
        W32(bytes, 0xF4, 4);
        RefsChecksum.RefreshSelfChecksum(bytes, ClusterSize, 0xD0, 0x68);
        this._writes.Add(((long)CheckpointLcns[c] * ClusterSize, bytes));
      }
    }

    private void WriteSuperblocks() {
      foreach (var lcn in new[] { SuperblockLcn, TotalClusters - 3, TotalClusters - 2 }) {
        var bytes = new byte[ClusterSize];
        "SUPB"u8.CopyTo(bytes);
        W32(bytes, 0x04, 2);
        W32(bytes, 0x0C, identity.Signature);
        W64(bytes, 0x20, lcn);
        identity.VolumeGuid.CopyTo(bytes, 0x50);
        W64(bytes, 0x68, 1);
        W32(bytes, 0x70, 0xC0);
        W32(bytes, 0x74, 2);
        W32(bytes, 0x78, 0xD0);
        W32(bytes, 0x7C, 0x68);
        W64(bytes, 0xC0, CheckpointLcns[0]);
        W64(bytes, 0xC8, CheckpointLcns[1]);
        W64(bytes, 0xD0, lcn);
        bytes[0xF2] = 1;
        bytes[0xF3] = 8;
        W32(bytes, 0xF4, 4);
        RefsChecksum.RefreshSelfChecksum(bytes, ClusterSize, 0xD0, 0x68);
        this._writes.Add(((long)lcn * ClusterSize, bytes));
      }
    }

    private void WriteBoot() {
      var vbr = new byte[512];
      "ReFS"u8.CopyTo(vbr.AsSpan(3));
      "FSRS"u8.CopyTo(vbr.AsSpan(0x10));
      W16(vbr, 0x14, 0x200);
      W64(vbr, 0x18, (ulong)(Geometry / 512));
      W32(vbr, 0x20, 512);
      W32(vbr, 0x24, ClusterSize / 512);
      vbr[0x28] = 3;
      vbr[0x29] = 7;
      W32(vbr, 0x2C, 0x26);
      W64(vbr, 0x38, identity.Serial);
      W64(vbr, 0x40, ClustersPerContainer * ClusterSize);
      W16(vbr, 0x16, RefsVolumeHeader.ComputeVbrChecksum(vbr));
      this._writes.Add((0, vbr));
      this._writes.Add((Geometry - 512, vbr));
    }
  }

  // Page kinds.

  private static Page SystemRoot(ulong table, ulong physical, bool virtualAddress, uint nodeFlags, uint schema, ushort kind = 0, ulong clock = 1, byte[]? header = null)
    => new() {
      Table = table, Physical = physical, Virtual = virtualAddress, Clock = clock, NodeOffset = 0x278, NodeFlags = nodeFlags,
      Header = Descriptor(0x278, kind, schema, header ?? [], extra: 0),
    };

  private static Page ObjectRoot(ulong table, ulong physical, ulong clock, ulong field18, uint schema, int nodeOffset, ushort kind = 1)
    => new() {
      Table = table, Physical = physical, Virtual = true, Clock = clock, Field18 = field18, NodeOffset = nodeOffset,
      Header = Descriptor(nodeOffset, kind, schema, [], extra: 0),
    };

  private static Page DirectoryRoot(ulong table, ulong physical, ulong clock, ushort nodeOffsetFlags)
    => new() {
      Table = table, Physical = physical, Virtual = true, Clock = clock, Field18 = 6, NodeOffset = 0xB8,
      Header = Descriptor(0xB8, 1, 0x130, [], extra: nodeOffsetFlags),
    };

  private static Page InnerRoot(ulong table, ulong physical, uint schema, int nodeOffset, int nodeEnd, ulong clock = 1)
    => new() {
      Table = table, Physical = physical, Virtual = true, Clock = clock, NodeOffset = nodeOffset, NodeEnd = nodeEnd, NodeFlags = 0x301,
      Header = Descriptor(nodeOffset, 0, schema, [], extra: 0),
    };

  private static Page LogRoot(ulong table, ulong physical)
    => new() {
      Table = table, Physical = physical, Virtual = true, Clock = 0, Field18 = 2, NodeOffset = 0xC0,
      Header = Descriptor(0xC0, 0, 0xE090, [], extra: 0),
    };

  private static Page Leaf(ulong table, ulong physical, ulong clock)
    => new() { Table = table, Physical = physical, Virtual = true, Clock = clock, NodeOffset = 0x58, NodeFlags = 0, Header = new byte[4] };

  /// <summary>
  /// The table descriptor at 0x54 — [u16 0x28, u16 kind, u32 1, u32 schema, u32 schema, u16 2,
  /// u16 extra, …, leaf-page count u64 at 0x68, row count u64 at 0x70] — and the table header
  /// that follows it up to the node, given as (page offset, u64) fields.
  /// </summary>
  private static byte[] Descriptor(int nodeOffset, ushort kind, uint schema, byte[] header, ushort extra) {
    var bytes = new byte[nodeOffset - 0x54];
    W16(bytes, 0x00, 0x28);
    W16(bytes, 0x02, kind);
    W32(bytes, 0x04, 1);
    W32(bytes, 0x08, schema);
    W32(bytes, 0x0C, schema);
    W16(bytes, 0x10, 2);
    W16(bytes, 0x12, extra);
    header.CopyTo(bytes, 0);
    if (header.Length > 0) {
      // Header fields are page offsets ≥ 0x78; restore the descriptor the copy overwrote.
      W16(bytes, 0x00, 0x28);
      W16(bytes, 0x02, kind);
      W32(bytes, 0x04, 1);
      W32(bytes, 0x08, schema);
      W32(bytes, 0x0C, schema);
      W16(bytes, 0x10, 2);
      W16(bytes, 0x12, extra);
    }
    return bytes;
  }

  /// <summary>Table-header fields as a byte image relative to 0x54 (offsets given as page offsets).</summary>
  private static byte[] Fields(params (int PageOffset, ulong Value)[] fields) {
    var bytes = new byte[0x224];
    foreach (var (offset, value) in fields) W64(bytes, offset - 0x54, value);
    return bytes;
  }

  /// <summary>The upcase table rows: name, entry count, then the 0x10000 two-byte entries in rows of 341 bytes.</summary>
  private static List<Row> UpcaseRows() {
    var table = new byte[0x20000];
    for (var c = 0; c < 0x10000; ++c) W16(table, c * 2, (ushort)char.ToUpperInvariant((char)c));
    foreach (var (unit, upper) in RefsFormatConstants.UpcaseExceptions) W16(table, unit * 2, upper);
    var rows = new List<Row> {
      new(U32Key(0), Encoding.ASCII.GetBytes("Upcase Table")),
      new(U32Key(1), [0, 0, 1, 0]),
    };
    var key = 2u;
    for (var at = 0; at < table.Length; at += 341)
      rows.Add(new Row(U32Key(key++), table.AsSpan(at, Math.Min(341, table.Length - at)).ToArray()));
    return rows;
  }

  private static byte[] U32Key(uint key) {
    var bytes = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, key);
    return bytes;
  }

  private static int Align8(int value) => (value + 7) & ~7;
  private static ushort R16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
  private static void W16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), v);
  private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);
  private static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o, 8), v);
}
