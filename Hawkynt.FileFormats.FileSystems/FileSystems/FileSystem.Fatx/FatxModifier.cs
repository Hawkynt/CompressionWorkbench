#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fatx;

/// <summary>
/// In-place R/W modifier for Microsoft Xbox / Xbox 360 FATX volumes.
///
/// <para>Modifies an existing image (produced by <see cref="FatxWriter"/> or by a
/// real Xbox console) by directly editing the FAT region and the root directory
/// cluster. Sub-directory entries are out of scope for v1: the modifier only
/// adds/removes files at the FATX volume root.</para>
///
/// <para><b>Add</b> walks the FAT looking for the first free cluster run long
/// enough to hold the requested payload, allocates it (linking each cluster to
/// the next, terminating with the FAT16/FAT32 EoC sentinel), writes the file
/// bytes into the data region cluster-by-cluster, then writes a 64-byte FATX
/// dirent into the first reusable slot in the root cluster. Reusable slots are
/// either a 0xE5 tombstone (previously deleted entry) or the leading 0xFF
/// terminator that the writer planted past the last valid record. Unused tail
/// bytes of the dirent's 42-byte name field stay 0xFF, matching what the
/// writer emits and what real Xbox firmware writes.</para>
///
/// <para><b>Remove</b> finds the dirent by name in the root cluster, sets
/// <c>name_length = 0xE5</c> per the FATX tombstone convention, walks the
/// file's FAT chain freeing each cluster (entry → 0), and securely zeros the
/// data bytes inside each freed cluster so no forensic recovery of the
/// previous content is possible.</para>
///
/// <para>The 12-byte timestamp tail of new dirents stays zeroed — the real
/// Xbox kernel re-stamps timestamps on mount, so this is benign in practice
/// and matches what <see cref="FatxWriter"/> already produces.</para>
///
/// <para><b>FAT width.</b> The FAT16/FAT32 threshold is recomputed from the
/// image geometry exactly the same way <see cref="FatxReader"/> does so the
/// reader and modifier always agree on which width to use. Both branches are
/// exercised by the test suite.</para>
/// </summary>
public static class FatxModifier {

  internal const int SuperblockSize = 0x1000;
  internal const int SectorSize = 512;
  internal const int DirRecordSize = 0x40;
  internal const int MaxNameLen = 42;
  private const uint MagicFatx = 0x58544146; // 'F','A','T','X' little-endian

  // ── Geometry ─────────────────────────────────────────────────────────────

  /// <summary>Recovered FATX geometry — everything the modifier needs to
  /// locate FAT entries, root-directory slots, and data clusters.</summary>
  private readonly record struct FatxGeom(
      uint SectorsPerCluster,
      int ClusterSize,
      uint RootDirCluster,
      int FatType,
      long FatRegionStart,
      long DataRegionStart,
      long ClusterCount,
      uint EocSentinel) {

    public long ClusterOffset(uint cluster) =>
      this.DataRegionStart + (long)(cluster - 1) * this.ClusterSize;

    public long FatEntryOffset(uint cluster) =>
      this.FatRegionStart + (long)cluster * (this.FatType == 16 ? 2 : 4);

    public bool IsEoc(uint v) =>
      this.FatType == 16 ? v >= 0xFFF8 : v >= 0xFFFFFFF8;
  }

  private static FatxGeom ParseGeometry(byte[] image) {
    if (image.Length < SuperblockSize)
      throw new InvalidDataException("FATX: image smaller than superblock (4 KiB).");
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0));
    if (magic != MagicFatx)
      throw new InvalidDataException($"FATX: bad magic 0x{magic:X8} (expected 'FATX' 0x{MagicFatx:X8}).");
    var spc = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x08));
    var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x0C));
    if (spc == 0 || (spc & (spc - 1)) != 0)
      throw new InvalidDataException($"FATX: invalid sectors_per_cluster {spc} (must be power of two).");

    var clusterSize = (int)spc * SectorSize;
    // FAT-width selection mirrors FatxReader.Parse: divide the raw post-
    // superblock region by cluster size. This is intentionally an
    // overestimate (the FAT itself eats some of those bytes) — both sides
    // agree on the heuristic so they always pick the same width.
    var rawDataBytes = (long)image.Length - SuperblockSize;
    var rawClusterCount = rawDataBytes / clusterSize;
    var fatType = rawClusterCount < 0xFFF4 ? 16 : 32;
    var entryBytes = fatType == 16 ? 2 : 4;
    var fatRaw = (Math.Max(1, rawClusterCount) + 2) * entryBytes;
    var fatRounded = (fatRaw + 0xFFFL) & ~0xFFFL;
    var dataRegionStart = SuperblockSize + fatRounded;
    var eoc = fatType == 16 ? 0xFFFFu : 0xFFFFFFFFu;
    // Real usable data clusters: how many full cluster-sized slots actually
    // fit in the image past the data-region boundary. The FAT may have entries
    // beyond this — the modifier just refuses to allocate them.
    var actualDataBytes = Math.Max(0, image.Length - dataRegionStart);
    var clusterCount = actualDataBytes / clusterSize;

    return new FatxGeom(
      SectorsPerCluster: spc,
      ClusterSize: clusterSize,
      RootDirCluster: rootCluster,
      FatType: fatType,
      FatRegionStart: SuperblockSize,
      DataRegionStart: dataRegionStart,
      ClusterCount: clusterCount,
      EocSentinel: eoc);
  }

  private static uint ReadFatEntry(byte[] image, FatxGeom g, uint cluster) {
    var pos = g.FatEntryOffset(cluster);
    var width = g.FatType == 16 ? 2 : 4;
    if (pos + width > image.Length) return g.EocSentinel;
    return g.FatType == 16
      ? BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)pos))
      : BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)pos));
  }

  private static void WriteFatEntry(byte[] image, FatxGeom g, uint cluster, uint value) {
    var pos = g.FatEntryOffset(cluster);
    var width = g.FatType == 16 ? 2 : 4;
    if (pos + width > image.Length)
      throw new InvalidOperationException(
        $"FATX: FAT entry for cluster {cluster} at 0x{pos:X} overflows image (size {image.Length}).");
    if (g.FatType == 16)
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)pos), (ushort)value);
    else
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)pos), value);
  }

  // ── Name sanitisation (must match FatxWriter.SanitiseName) ──────────────

  /// <summary>Truncates names to 42 ASCII bytes with a <c>~1</c> tail if needed;
  /// non-ASCII chars are replaced with <c>_</c>. Identical to the writer's
  /// behaviour so previously-added files can be found by their on-disk name.</summary>
  internal static string SanitiseName(string name) {
    if (string.IsNullOrEmpty(name)) return "_";
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
      sb.Append(c is >= (char)0x20 and < (char)0x7F ? c : '_');
    var s = sb.ToString();
    if (s.Length <= MaxNameLen) return s;
    return s[..(MaxNameLen - 2)] + "~1";
  }

  // ── Public surface ──────────────────────────────────────────────────────

  /// <summary>
  /// Adds a single file to the root directory of the FATX image. The image is
  /// mutated in place. Throws if no free cluster run of the required length
  /// exists, or if no reusable dirent slot is left in the root cluster.
  /// </summary>
  /// <param name="image">In-memory FATX image (modified in place).</param>
  /// <param name="name">Leaf filename. Subdirectory paths are not supported by v1 of the modifier.</param>
  /// <param name="data">File payload bytes. May be empty.</param>
  public static void AddFile(byte[] image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var g = ParseGeometry(image);
    var sanitised = SanitiseName(LeafOf(name));

    // Adding a name that is already there replaces it. Without this the old
    // record stayed alongside the new one and the volume listed the file twice,
    // with the older copy's clusters still marked in use — which nothing
    // noticed while a freshly written volume had no free cluster to add into.
    RemoveFile(image, sanitised);

    // 1. Find a free dirent slot in the root cluster (single-cluster scope).
    var slotOffset = FindFreeDirentSlot(image, g)
      ?? throw new InvalidOperationException(
        "FATX modifier: no free dirent slot in root cluster — extending the root chain is out of scope for v1.");

    // 2. Allocate a free cluster run (or none for zero-byte files).
    uint startCluster = 0;
    uint clustersNeeded = 0;
    if (data.Length > 0) {
      clustersNeeded = (uint)((data.Length + g.ClusterSize - 1) / g.ClusterSize);
      startCluster = AllocateChain(image, g, clustersNeeded);

      // 3. Write the file payload across the allocated chain.
      for (var c = 0u; c < clustersNeeded; c++) {
        var cluster = startCluster + c;
        var dataOff = g.ClusterOffset(cluster);
        if (dataOff + g.ClusterSize > image.Length)
          throw new InvalidOperationException(
            $"FATX modifier: data cluster {cluster} at 0x{dataOff:X} overflows image (size {image.Length}).");
        var srcStart = (int)c * g.ClusterSize;
        var take = Math.Min(g.ClusterSize, data.Length - srcStart);
        Buffer.BlockCopy(data, srcStart, image, (int)dataOff, take);
        // Zero any cluster-tip slack so previous tombstoned bytes don't bleed
        // through. The allocate step already only picked entries the FAT
        // marked free (value == 0), but the data cluster may still contain
        // stale bytes from a previous removal that didn't wipe data.
        if (take < g.ClusterSize)
          image.AsSpan((int)dataOff + take, g.ClusterSize - take).Clear();
      }
    }

    // 4. Write the dirent into the chosen slot.
    WriteDirent(image.AsSpan(slotOffset, DirRecordSize),
      name: sanitised, attr: 0x20, firstCluster: startCluster, size: (uint)data.Length);
  }

  /// <summary>
  /// Removes a single file from the root directory of the FATX image. The
  /// dirent is tombstoned with <c>name_length = 0xE5</c>, the file's FAT chain
  /// is freed, and every freed data cluster is zeroed so the previous bytes
  /// are not forensically recoverable. Returns true if a matching dirent was
  /// found and tombstoned; false otherwise.
  /// </summary>
  public static bool RemoveFile(byte[] image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var g = ParseGeometry(image);
    var sanitised = SanitiseName(LeafOf(name));

    var direntOffset = FindDirentByName(image, g, sanitised);
    if (direntOffset < 0) return false;

    // Read the chain head + size before mutating the dirent.
    var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(direntOffset + 0x2C));
    var size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(direntOffset + 0x30));

    // Walk the chain, freeing every cluster + zeroing its data bytes. The
    // FATX convention is to write 0 into the FAT slot to mark it free.
    FreeChain(image, g, firstCluster, wipeData: true);

    // Tombstone the dirent. Per FATX spec the tombstone is name_length = 0xE5;
    // FatxReader treats 0xE5 as "skip, but keep scanning" (vs 0xFF which
    // terminates). Leaving everything else intact gives forensic tools a
    // chance to recover names if anyone needs to audit deletions later — but
    // since we already wiped the data clusters, no content can be recovered
    // from the freed bytes. Zero size & first_cluster anyway so a stale
    // dirent never confuses a future Add that re-uses the slot.
    image[direntOffset] = 0xE5;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(direntOffset + 0x2C), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(direntOffset + 0x30), 0);
    _ = size; // documented but unused (data already wiped via chain walk)
    return true;
  }

  // ── Allocation ──────────────────────────────────────────────────────────

  /// <summary>Finds and reserves a contiguous free run of <paramref name="count"/>
  /// clusters in the FAT, writes the chain links + EoC sentinel, returns the
  /// first cluster's number. Throws if no such run exists.</summary>
  private static uint AllocateChain(byte[] image, FatxGeom g, uint count) {
    if (count == 0) return 0;

    // Cluster numbering starts at 1 in FATX. Walk the FAT looking for the
    // first cluster N where N..N+count-1 are all marked free (entry = 0).
    var maxCluster = (uint)g.ClusterCount;
    var first = 1u;
    while (first + count - 1 <= maxCluster) {
      if (IsFree(image, g, first)) {
        var ok = true;
        for (var k = 1u; k < count; k++) {
          if (!IsFree(image, g, first + k)) {
            ok = false;
            first += k; // skip past the occupied slot
            break;
          }
        }
        if (ok) {
          // Reserve the run: link each cluster to the next, last → EoC.
          for (var k = 0u; k < count; k++) {
            var cluster = first + k;
            var next = k + 1 < count ? cluster + 1 : g.EocSentinel;
            WriteFatEntry(image, g, cluster, next);
          }
          return first;
        }
        continue;
      }
      first++;
    }
    throw new InvalidOperationException(
      $"FATX modifier: no free run of {count} contiguous clusters available " +
      $"(image has {maxCluster} total clusters).");
  }

  private static bool IsFree(byte[] image, FatxGeom g, uint cluster) {
    // Cluster 0 doesn't exist in FATX; cluster 1 is the root dir (always
    // allocated). Treat both as "not free" to keep the allocator safe.
    if (cluster < 2) return false;
    return ReadFatEntry(image, g, cluster) == 0;
  }

  /// <summary>Walks the chain starting at <paramref name="startCluster"/>,
  /// freeing each FAT entry and (when <paramref name="wipeData"/> is true)
  /// zeroing the cluster's data bytes. Stops on EoC or a cycle.</summary>
  private static void FreeChain(byte[] image, FatxGeom g, uint startCluster, bool wipeData) {
    if (startCluster < 2 || g.IsEoc(startCluster)) return;
    var seen = new HashSet<uint>();
    var cluster = startCluster;
    while (cluster >= 2 && !g.IsEoc(cluster) && seen.Add(cluster)) {
      var next = ReadFatEntry(image, g, cluster);
      WriteFatEntry(image, g, cluster, 0);
      if (wipeData) {
        var dataOff = g.ClusterOffset(cluster);
        if (dataOff >= 0 && dataOff + g.ClusterSize <= image.Length)
          image.AsSpan((int)dataOff, g.ClusterSize).Clear();
      }
      cluster = next;
    }
  }

  // ── Directory scanning ──────────────────────────────────────────────────

  /// <summary>Finds the first reusable dirent slot in the root cluster.
  /// Returns the byte offset of the slot in <paramref name="image"/>, or
  /// <c>null</c> if no slot is available. A slot is reusable when its
  /// <c>name_length</c> is 0xFF (end-of-dir terminator — the writer plants
  /// these past the last real record) or 0xE5 (tombstoned by a previous
  /// remove).</summary>
  private static int? FindFreeDirentSlot(byte[] image, FatxGeom g) {
    var rootOffset = g.ClusterOffset(g.RootDirCluster);
    if (rootOffset < 0 || rootOffset + g.ClusterSize > image.Length) return null;
    for (var off = 0; off < g.ClusterSize; off += DirRecordSize) {
      var nameLen = image[(int)rootOffset + off];
      if (nameLen == 0xFF || nameLen == 0xE5 || nameLen == 0x00) return (int)rootOffset + off;
    }
    return null;
  }

  /// <summary>Scans the root cluster for a dirent with a matching ASCII name.
  /// Case-insensitive (FATX names are case-preserving but case-insensitive).
  /// Returns the dirent's byte offset, or -1 if not found. Skips tombstoned
  /// (0xE5) records; stops at the 0xFF end-of-directory sentinel.</summary>
  private static int FindDirentByName(byte[] image, FatxGeom g, string sanitisedName) {
    var rootOffset = g.ClusterOffset(g.RootDirCluster);
    if (rootOffset < 0 || rootOffset + g.ClusterSize > image.Length) return -1;
    for (var off = 0; off < g.ClusterSize; off += DirRecordSize) {
      var slot = (int)rootOffset + off;
      var nameLen = image[slot];
      if (nameLen == 0xFF || nameLen == 0x00) return -1; // end of directory
      if (nameLen == 0xE5) continue;                     // tombstone
      if (nameLen > MaxNameLen) continue;                // malformed
      var raw = image.AsSpan(slot + 2, nameLen);
      var diskName = Encoding.ASCII.GetString(raw);
      if (string.Equals(diskName, sanitisedName, StringComparison.OrdinalIgnoreCase))
        return slot;
    }
    return -1;
  }

  // ── Dirent emission (must match FatxWriter.WriteDirent layout) ──────────

  private static void WriteDirent(Span<byte> dst, string name, byte attr, uint firstCluster, uint size) {
    // Slot layout per FATX spec — identical to FatxWriter.WriteDirent:
    //   +0x00  name_length        u8   (0..42)
    //   +0x01  attributes         u8   (0x10 = directory, 0x20 = archive)
    //   +0x02  name               42 bytes (ASCII, padded 0xFF)
    //   +0x2C  first_cluster      u32 LE
    //   +0x30  size               u32 LE
    //   +0x34..0x3F  timestamps   (12 bytes — zeroed; real Xbox re-stamps)
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var nameLen = Math.Min(nameBytes.Length, MaxNameLen);
    dst[0] = (byte)nameLen;
    dst[1] = attr;
    dst.Slice(2, MaxNameLen).Fill(0xFF);
    nameBytes.AsSpan(0, nameLen).CopyTo(dst.Slice(2, nameLen));
    BinaryPrimitives.WriteUInt32LittleEndian(dst[0x2C..], firstCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(dst[0x30..], size);
    dst.Slice(0x34, 12).Clear();
  }

  /// <summary>Returns the trailing path component, normalising '/' and '\\'
  /// separators. Subdirectory adds aren't supported by v1, so any caller that
  /// passes a path with separators is implicitly treating only the leaf as
  /// the added file's root-level name.</summary>
  private static string LeafOf(string name) {
    var trimmed = name.Replace('\\', '/').Trim('/');
    var slash = trimmed.LastIndexOf('/');
    return slash < 0 ? trimmed : trimmed[(slash + 1)..];
  }
}
