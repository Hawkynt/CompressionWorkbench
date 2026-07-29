#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Fatx;

/// <summary>
/// Builds Microsoft Xbox / Xbox 360 FATX filesystem images from scratch
/// per the reverse-engineered FATX spec (FreeXboxBios / fatx-linux / fatxlinux).
///
/// <para>On-disk layout (little-endian):</para>
/// <list type="bullet">
///   <item>0x000  superblock: "FATX" magic + volume_id + sectors_per_cluster + root_cluster + 4078 reserved bytes.</item>
///   <item>0x1000 FAT region: FAT16 (2 byte/entry) if cluster_count &lt; 0xFFF4 else FAT32 (4 byte/entry). Length rounded up to 4 KiB pages.</item>
///   <item>data region: clusters numbered from 1; cluster N is at <c>fat_end + (N-1)*cluster_size</c>.</item>
///   <item>directory entries (64 bytes each): name_length + attrs + 42-byte ASCII name (0xFF-padded) + first_cluster (u32) + size (u32) + 12 bytes of timestamps. Sentinel 0xFF on name_length terminates the directory; 0xE5 marks deleted.</item>
/// </list>
///
/// <para>Real Xbox volumes use 16 KiB clusters and FAT32, but the format
/// itself permits any power-of-two sectors-per-cluster — the writer
/// auto-picks a small cluster (2 KiB / 4 sectors) for tiny synthetic
/// images so unit tests stay compact, and 16 KiB / 32 sectors for any
/// image &gt; 1 MiB, matching the original Xbox HDD convention.</para>
///
/// <para>FATX dirent names are limited to 42 ASCII bytes — no LFN, no
/// Unicode. Names longer than 42 characters are truncated with a
/// trailing <c>~N</c> alias to keep them unique within the same
/// directory.</para>
/// </summary>
public sealed class FatxWriter {

  private readonly List<(string Name, FilePayload Payload)> _files = [];

  internal const int SectorSize = 512;
  internal const int SuperblockSize = 0x1000;
  internal const int DirRecordSize = 0x40;
  internal const int MaxNameLen = 42;
  private const uint MagicFatx = 0x58544146; // 'F','A','T','X' little-endian

  /// <summary>Adds a file to the image. Path separators ('/' or '\') split
  /// the name into nested directories; each subdirectory becomes its own
  /// cluster chain with proper FATX dirents.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, FilePayload.FromBytes(data)));
  }

  /// <summary>
  /// Adds a file whose bytes are produced on demand. <paramref name="size" /> must
  /// match what <paramref name="openStream" /> yields; the cluster layout is
  /// settled from it before a byte is read.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    this._files.Add((name, FilePayload.FromStream(size, openStream)));
  }

  /// <summary>Builds a complete FATX image containing the previously-added
  /// files.</summary>
  /// <param name="sectorsPerCluster">Sectors per cluster (must be a power of two &gt;= 1).
  /// 0 = auto-pick (4 sectors / 2 KiB for tiny images, 32 sectors / 16 KiB
  /// for anything &gt; 1 MiB matching the original Xbox HDD layout).</param>
  /// <param name="volumeId">32-bit volume identifier stored in the superblock.</param>
  public byte[] Build(int sectorsPerCluster = 0, uint volumeId = 0x12345678) {
    // ── Phase 1: tree + planning ────────────────────────────────────────
    var root = this.BuildTree();
    var totalBytes = SumPayload(root);

    // Auto-pick the cluster size that minimises wasted slack across the actual
    // file-set plus the per-size FAT-table overhead, via the shared layout
    // optimizer. The reader is cluster-size-agnostic (it reads sectors_per_cluster
    // straight from the superblock), so every candidate round-trips; the legal
    // window is power-of-two clusters from 2 KiB (4 sectors) up to 64 KiB
    // (128 sectors), bracketing the canonical 16 KiB Xbox HDD cluster. Tiny
    // synthetic images naturally land on the smallest cluster (least slack),
    // large workloads on a bigger one (less FAT overhead) — matching the old
    // payload-threshold heuristic at the extremes but optimal in between.
    if (sectorsPerCluster <= 0)
      sectorsPerCluster = SelectOptimalSectorsPerCluster(totalBytes);
    if (sectorsPerCluster < 1 || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0)
      throw new ArgumentException(
        $"FATX: sectors_per_cluster must be a power of two (got {sectorsPerCluster}).",
        nameof(sectorsPerCluster));

    var clusterSize = sectorsPerCluster * SectorSize;

    // ── Phase 2: allocate clusters greedily, starting at cluster 1 (root).
    // Each directory chain is contiguous; each file chain is contiguous.
    // We need the cluster_count to decide FAT width — the FAT16/32 threshold
    // is 0xFFF4 per the reader's heuristic. So we plan with an unconstrained
    // counter first, decide the width, and only then materialise.
    var nextCluster = 1u; // FATX root lives at cluster 1, not 2 like FAT12/16/32
    PlanClusters(root, ref nextCluster, clusterSize);
    var lastUsedCluster = nextCluster - 1;
    // Spec heuristic: clusterCount < 0xFFF4 → FAT16, else FAT32.
    var clusterCount = (long)lastUsedCluster;
    var fatType = clusterCount < 0xFFF4 ? 16 : 32;
    var entryBytes = fatType == 16 ? 2 : 4;

    // FAT region: (clusterCount + 2) entries rounded up to 4 KiB pages.
    // The reader also rounds the FAT region this way (see FatxReader.DataRegionStart).
    var fatRaw = (clusterCount + 2) * entryBytes;
    var fatRounded = (fatRaw + 0xFFFL) & ~0xFFFL;
    var dataRegionStart = SuperblockSize + fatRounded;
    var imageSize = dataRegionStart + clusterCount * clusterSize;

    // FAT32 with no data clusters (empty image) still needs the FAT16/32
    // threshold check to come out the same in the reader. Reader computes
    // clusterCount from `(image_size - SuperblockSize) / clusterSize`, which
    // includes the FAT region bytes — round the image up to the next cluster
    // boundary after the data so the reader's heuristic agrees with ours.
    // (Reader: `dataBytes = image.Length - SuperblockSize; clusterCount = dataBytes / clusterBytes`.)
    var readerDataBytes = imageSize - SuperblockSize;
    var readerClusterCount = readerDataBytes / clusterSize;
    while ((readerClusterCount < 0xFFF4) != (fatType == 16)) {
      // The reader's heuristic disagrees with ours — pad the image with one
      // extra cluster of slack until they line up. In practice this branch
      // never fires for sane inputs because the FAT region itself shifts
      // readerClusterCount well below clusterCount, but the guard makes the
      // writer round-trip-safe by construction.
      imageSize += clusterSize;
      readerDataBytes = imageSize - SuperblockSize;
      readerClusterCount = readerDataBytes / clusterSize;
      // Safety: bail if we ever cross over the threshold the wrong way.
      if (imageSize > 0x7FFFFFFFL) throw new InvalidOperationException("FATX: image too large during FAT-type alignment.");
    }

    this.DeclaredImageBytes = imageSize;
    // Only the superblock and FAT are materialised: every cluster payload --
    // directory blobs included -- lives in the data region past them and is
    // placed by seek. Allocating the whole image capped FATX at the array limit.
    var prefixBytes = this._deferPayloads ? dataRegionStart : imageSize;
    if (prefixBytes > Array.MaxLength)
      throw new InvalidOperationException(
        $"FATX: a {imageSize:N0}-byte image exceeds the array limit; write it to a seekable stream instead.");
    this._payloads = new DeferredPayloads();
    var image = new byte[prefixBytes];

    // ── Phase 3: superblock ─────────────────────────────────────────────
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x00), MagicFatx);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x04), volumeId);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x08), (uint)sectorsPerCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x0C), root.StartCluster);
    // Bytes 0x10..0x0FFF are reserved (zero) per spec.

    // ── Phase 4: FAT chain entries ──────────────────────────────────────
    var fatOffset = (int)SuperblockSize;
    var eoc = fatType == 16 ? 0xFFFFu : 0xFFFFFFFFu; // EoC sentinel: any value >= 0xFFF8 / 0xFFFFFFF8.
    // Entries 0 and 1 are reserved in standard FAT, but FATX's reader uses
    // cluster numbers starting at 1 and indexes the FAT at `cluster*width`,
    // so entry 0 is the "first reserved" and the rest are real. We leave
    // entries 0..first_real-1 as zero and write the chain links from there.
    WriteChains(image.AsSpan(fatOffset), root, fatType, eoc);

    // ── Phase 5: directory + file payload ───────────────────────────────
    this.WritePayload(image, root, dataRegionStart, clusterSize, fatType);

    return image;
  }

  /// <summary>
  /// Power-of-two sectors-per-cluster candidates (512-byte sectors): 2 KiB …
  /// 64 KiB clusters. Every value is legal FATX geometry; the reader honours
  /// whatever <c>sectors_per_cluster</c> the superblock records.
  /// </summary>
  private static readonly int[] SectorsPerClusterCandidates = [4, 8, 16, 32, 64, 128];

  /// <summary>
  /// Picks the sectors-per-cluster value that minimises file-tail slack plus the
  /// FAT-table footprint for the current file-set, delegating the search to
  /// <see cref="Compression.Core.Layout.LayoutOptimizerAdapter"/>. Returns the
  /// number of 512-byte sectors per cluster (a power of two).
  /// </summary>
  private int SelectOptimalSectorsPerCluster(long totalBytes) {
    var fileSizes = this._files.Select(f => f.Payload.Size).ToList();
    // Empty volumes have no slack signal — keep the canonical small cluster.
    if (totalBytes <= 0) return SectorsPerClusterCandidates[0];

    // Optimise over cluster sizes (bytes), then map the winner back to sectors.
    var clusterCandidates = SectorsPerClusterCandidates.Select(spc => spc * SectorSize).ToArray();
    var bestClusterBytes = Compression.Core.Layout.LayoutOptimizerAdapter.SelectAllocationUnit(
      clusterCandidates,
      fileSizes,
      fixedOverhead: clusterBytes => {
        // FAT footprint grows as the data-cluster count grows (smaller clusters
        // ⇒ more entries ⇒ a bigger FAT). Approximate cluster count from the
        // payload, width from the FAT16/32 threshold, rounded to 4 KiB pages.
        var dataClusters = Compression.Core.Layout.FilesystemLayoutOptimizer.DataClusters(fileSizes, clusterBytes);
        var entryBytes = dataClusters < 0xFFF4 ? 2L : 4L;
        var fatRaw = (dataClusters + 2) * entryBytes;
        return (fatRaw + 0xFFFL) & ~0xFFFL;
      });
    return bestClusterBytes / SectorSize;
  }

  // ── tree ─────────────────────────────────────────────────────────────

  private sealed class DirNode(string name) {
    public string Name { get; } = name;
    public List<DirNode> Dirs { get; } = [];
    public List<FileNode> Files { get; } = [];
    private Dictionary<string, DirNode> Index { get; } = new(StringComparer.OrdinalIgnoreCase);
    public uint StartCluster { get; set; }
    public uint ClusterCount { get; set; }
    public byte[] DirentBlob { get; set; } = [];
    public DirNode GetOrAddDir(string childName) {
      if (this.Index.TryGetValue(childName, out var existing)) return existing;
      var created = new DirNode(childName);
      this.Index[childName] = created;
      this.Dirs.Add(created);
      return created;
    }
  }

  private sealed class FileNode(string name, FilePayload payload) {
    public string Name { get; } = name;
    public FilePayload Payload { get; } = payload;
    public uint StartCluster { get; set; }
    public uint ClusterCount { get; set; }
  }

  private DirNode BuildTree() {
    var root = new DirNode("");
    foreach (var (name, data) in this._files) {
      var parts = name.Replace('\\', '/').Trim('/').Split('/');
      var dir = root;
      for (var i = 0; i < parts.Length - 1; i++)
        dir = dir.GetOrAddDir(SanitiseName(parts[i]));
      dir.Files.Add(new FileNode(SanitiseName(parts[^1]), data));
    }
    return root;
  }

  /// <summary>Truncates names to 42 ASCII bytes with a <c>~N</c> tail if
  /// needed; non-ASCII chars are replaced with <c>_</c>. Names are not
  /// case-normalised so the reader sees them as written.</summary>
  private static string SanitiseName(string name) {
    if (string.IsNullOrEmpty(name)) return "_";
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
      sb.Append(c is >= (char)0x20 and < (char)0x7F ? c : '_');
    var s = sb.ToString();
    if (s.Length <= MaxNameLen) return s;
    // Truncate with a tilde tail so collisions become diagnosable.
    return s[..(MaxNameLen - 2)] + "~1";
  }

  // ── planning ─────────────────────────────────────────────────────────

  private static long SumPayload(DirNode node) {
    var total = 0L;
    foreach (var f in node.Files) total += f.Payload.Size;
    foreach (var d in node.Dirs) total += SumPayload(d);
    return total;
  }

  /// <summary>Recursively allocates contiguous cluster runs for every
  /// directory and file in the tree, starting at <paramref name="next"/>.
  /// Also builds each directory's dirent blob so the patch in
  /// <see cref="WritePayload"/> only has to memcpy bytes that are already
  /// in their final form (start_cluster + size known by then).</summary>
  private static void PlanClusters(DirNode node, ref uint next, int clusterSize) {
    // Directory cluster allocation first — directories are siblings before
    // their files in the layout, so the reader can walk a directory chain
    // without seeking back over a file's clusters.
    var direntBytes = BuildDirentBlob(node, clusterSize);
    var dirClusters = direntBytes.Length == 0 ? 1u : (uint)((direntBytes.Length + clusterSize - 1) / clusterSize);
    node.StartCluster = next;
    node.ClusterCount = dirClusters;
    next += dirClusters;

    // Now files.
    foreach (var f in node.Files) {
      var clusters = f.Payload.Size == 0 ? 0u : (uint)((f.Payload.Size + clusterSize - 1) / clusterSize);
      if (clusters == 0) {
        f.StartCluster = 0; // zero-length files conventionally point nowhere
        f.ClusterCount = 0;
      } else {
        f.StartCluster = next;
        f.ClusterCount = clusters;
        next += clusters;
      }
    }

    // Now recurse into subdirectories.
    foreach (var d in node.Dirs)
      PlanClusters(d, ref next, clusterSize);

    // Now that every child has a final start_cluster + size, patch them
    // into the dirent blob we sized above. We rebuild the blob from
    // scratch with the now-known cluster numbers; the byte size cannot
    // change because the same name/attr layout is used.
    node.DirentBlob = BuildDirentBlobPatched(node, direntBytes.Length);
  }

  /// <summary>Computes the byte size of a directory's dirent blob.
  /// Each child takes exactly one 64-byte slot; we add a trailing
  /// "end of directory" sentinel (name_length = 0xFF) so the reader
  /// stops scanning at the right boundary.</summary>
  private static byte[] BuildDirentBlob(DirNode node, int clusterSize) {
    // Files first, then subdirectories — matches typical FATX writer
    // convention (file_attribute 0x20 before dir_attribute 0x10).
    var slotCount = node.Files.Count + node.Dirs.Count;
    // Round up to a whole cluster so the chain layout is exact; the
    // reader stops on the 0xFF sentinel before the trailing zeros.
    var minBytes = (slotCount + 1) * DirRecordSize;
    var clusters = minBytes == 0 ? 1 : (minBytes + clusterSize - 1) / clusterSize;
    return new byte[clusters * clusterSize];
  }

  /// <summary>Builds the directory's dirent blob with finalised start_cluster
  /// + size fields. <paramref name="length"/> is the byte length agreed by
  /// the planning pass — we use it to keep allocation deterministic.</summary>
  private static byte[] BuildDirentBlobPatched(DirNode node, int length) {
    var blob = new byte[length];
    // Pre-fill the entire cluster with 0xFF so unused dirent slots are
    // marked "end of directory" (the reader treats 0xFF on name_length as
    // a terminator). This matches the Xbox kernel's behaviour, which
    // writes free clusters as all-0xFF.
    blob.AsSpan().Fill(0xFF);

    var off = 0;
    foreach (var f in node.Files)
      off += WriteDirent(blob.AsSpan(off), f.Name, attr: 0x20, firstCluster: f.StartCluster, size: (uint)Math.Min(f.Payload.Size, uint.MaxValue));
    foreach (var d in node.Dirs)
      off += WriteDirent(blob.AsSpan(off), d.Name, attr: 0x10, firstCluster: d.StartCluster, size: 0);
    // The remaining tail stays 0xFF, which the reader interprets as
    // "end of directory" the moment it hits the first 0xFF name_length.
    return blob;
  }

  /// <summary>Writes one 64-byte FATX directory entry. Returns the slot size (always 64).</summary>
  private static int WriteDirent(Span<byte> dst, string name, byte attr, uint firstCluster, uint size) {
    // Slot layout per FATX spec:
    //   +0x00  name_length        u8   (0..42)
    //   +0x01  attributes         u8   (0x10 = directory, 0x20 = archive)
    //   +0x02  name               42 bytes (ASCII, padded 0xFF)
    //   +0x2C  first_cluster      u32 LE
    //   +0x30  size               u32 LE
    //   +0x34..0x3F  timestamps   (12 bytes — set to zero for WORM)
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var nameLen = Math.Min(nameBytes.Length, MaxNameLen);
    dst[0] = (byte)nameLen;
    dst[1] = attr;
    // Name field is 42 bytes; reader reads only the leading nameLen
    // bytes but unused tail bytes are conventionally 0xFF.
    dst.Slice(2, MaxNameLen).Fill(0xFF);
    nameBytes.AsSpan(0, nameLen).CopyTo(dst.Slice(2, nameLen));
    BinaryPrimitives.WriteUInt32LittleEndian(dst[0x2C..], firstCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(dst[0x30..], size);
    // Timestamps left at 0xFF tail from the caller's pre-fill — fine,
    // the reader doesn't validate them. But for cleanliness zero them:
    dst.Slice(0x34, 12).Clear();
    return DirRecordSize;
  }

  // ── FAT chains ───────────────────────────────────────────────────────

  /// <summary>Writes FAT chain links for every allocated cluster run in the
  /// tree. <paramref name="fatBytes"/> is the FAT region; entries are at
  /// <c>cluster * width</c>.</summary>
  private static void WriteChains(Span<byte> fatBytes, DirNode root, int fatType, uint eoc) {
    WriteRun(fatBytes, root.StartCluster, root.ClusterCount, fatType, eoc);
    WriteRunsRecursive(fatBytes, root, fatType, eoc);
  }

  private static void WriteRunsRecursive(Span<byte> fatBytes, DirNode node, int fatType, uint eoc) {
    foreach (var f in node.Files)
      if (f.ClusterCount > 0)
        WriteRun(fatBytes, f.StartCluster, f.ClusterCount, fatType, eoc);
    foreach (var d in node.Dirs) {
      WriteRun(fatBytes, d.StartCluster, d.ClusterCount, fatType, eoc);
      WriteRunsRecursive(fatBytes, d, fatType, eoc);
    }
  }

  private static void WriteRun(Span<byte> fatBytes, uint start, uint count, int fatType, uint eoc) {
    for (uint c = 0; c < count; c++) {
      var cluster = start + c;
      var nextVal = c + 1 < count ? cluster + 1 : eoc;
      var width = fatType == 16 ? 2 : 4;
      var pos = (int)(cluster * width);
      if (pos + width > fatBytes.Length) return;
      if (fatType == 16)
        BinaryPrimitives.WriteUInt16LittleEndian(fatBytes[pos..], (ushort)nextVal);
      else
        BinaryPrimitives.WriteUInt32LittleEndian(fatBytes[pos..], nextVal);
    }
  }

  // ── payload ──────────────────────────────────────────────────────────

  private void WritePayload(byte[] image, DirNode node, long dataRegionStart, int clusterSize, int fatType) {
    // Directory blob first.
    var dirOff = dataRegionStart + (long)(node.StartCluster - 1) * clusterSize;
    if (this._deferPayloads) {
      this._payloads!.Add(dirOff, node.DirentBlob);
    } else {
      var copy = (int)Math.Min(node.DirentBlob.Length, image.Length - dirOff);
      if (copy > 0) Buffer.BlockCopy(node.DirentBlob, 0, image, (int)dirOff, copy);
    }

    // Files.
    foreach (var f in node.Files) {
      if (f.ClusterCount == 0) continue;
      var fileOff = dataRegionStart + (long)(f.StartCluster - 1) * clusterSize;
      if (this._deferPayloads) {
        this._payloads!.Add(fileOff, f.Payload);
        continue;
      }
      var bytes = f.Payload.ToArray();
      var fcopy = (int)Math.Min(bytes.Length, image.Length - fileOff);
      if (fcopy > 0) Buffer.BlockCopy(bytes, 0, image, (int)fileOff, fcopy);
    }

    // Subdirectories.
    foreach (var d in node.Dirs)
      this.WritePayload(image, d, dataRegionStart, clusterSize, fatType);
  }

  /// <summary>Declared size of the image the last build laid out.</summary>
  private long DeclaredImageBytes { get; set; }

  /// <summary>When set, cluster payloads are collected instead of copied into the buffer.</summary>
  private bool _deferPayloads;

  private DeferredPayloads? _payloads;

  /// <summary>
  /// Writes the image into <paramref name="output" />: the superblock and FAT,
  /// then every cluster payload at its offset. Only a non-seekable target has to
  /// materialise the image, so a seekable one is bounded by the disk rather than
  /// by what a byte[] can address.
  /// </summary>
  public void WriteTo(Stream output, int sectorsPerCluster = 0, uint volumeId = 0x12345678) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek) {
      var full = this.Build(sectorsPerCluster, volumeId);
      output.Write(full, 0, full.Length);
      return;
    }

    var basePosition = output.Position;
    this._deferPayloads = true;
    byte[] prefix;
    try {
      prefix = this.Build(sectorsPerCluster, volumeId);
    } finally {
      this._deferPayloads = false;
    }
    output.Write(prefix, 0, prefix.Length);
    output.SetLength(basePosition + this.DeclaredImageBytes);
    this._payloads!.FlushTo(output, basePosition);
    output.Position = basePosition + this.DeclaredImageBytes;
    output.Flush();
  }
}
