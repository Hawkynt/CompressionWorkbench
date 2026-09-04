#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// Subdirectory-aware defragmentation for the planner-driven FAT path. After a
/// defragment of an image containing a nested directory tree:
/// <list type="bullet">
///   <item>every nested file must still round-trip at its exact path with intact
///   content (the parent dirent's start-cluster pointer is repatched when a
///   subdirectory's data is relocated);</item>
///   <item>a subdirectory whose cluster chain was fragmented into several
///   non-adjacent runs must be coalesced into a single contiguous run — the
///   "fuse the parts of one directory together" requirement;</item>
///   <item>the subdirectory's own '.' self-pointer and each child subdirectory's
///   '..' parent pointer must follow the move so the on-disk tree stays valid.</item>
/// </list>
/// </summary>
[TestFixture]
public class FatDefragSubdirectoryTests {

  // ── FAT12 geometry helpers (mirror the writer's default floppy layout) ───
  //
  // These read straight off the BPB so the tests work regardless of the exact
  // image size the writer chose. Everything here is deliberately independent of
  // the production extent map / block mover so a defect in those does not also
  // corrupt the test's own bookkeeping.

  private sealed class FatGeometry {
    public int BytesPerSector;
    public int SectorsPerCluster;
    public int ReservedSectors;
    public int FatCount;
    public int RootEntryCount;
    public int FatSize;
    public int RootDirSectors;
    public int FirstDataSector;
    public int ClusterSize;
    public long FatBase;
    public long FirstDataByte;

    public static FatGeometry Read(byte[] image) {
      var g = new FatGeometry {
        BytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11)),
        SectorsPerCluster = image[13],
        ReservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14)),
        FatCount = image[16],
        RootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(17)),
        FatSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22)),
      };
      g.RootDirSectors = (g.RootEntryCount * 32 + g.BytesPerSector - 1) / g.BytesPerSector;
      g.FirstDataSector = g.ReservedSectors + g.FatCount * g.FatSize + g.RootDirSectors;
      g.ClusterSize = g.SectorsPerCluster * g.BytesPerSector;
      g.FatBase = (long)g.ReservedSectors * g.BytesPerSector;
      g.FirstDataByte = (long)g.FirstDataSector * g.BytesPerSector;
      return g;
    }

    public long ClusterOffset(int cluster) => this.FirstDataByte + (long)(cluster - 2) * this.ClusterSize;

    public int ReadFat12(byte[] image, int cluster) {
      var pos = (int)this.FatBase + cluster * 3 / 2;
      var val = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pos));
      return (cluster & 1) != 0 ? val >> 4 : val & 0xFFF;
    }

    public void WriteFat12(byte[] image, int cluster, int value) {
      for (var fatIdx = 0; fatIdx < this.FatCount; fatIdx++) {
        var fatBase = (int)this.FatBase + fatIdx * this.FatSize * this.BytesPerSector;
        var pos = fatBase + cluster * 3 / 2;
        if ((cluster & 1) == 0) {
          image[pos] = (byte)(value & 0xFF);
          image[pos + 1] = (byte)((image[pos + 1] & 0xF0) | ((value >> 8) & 0x0F));
        } else {
          image[pos] = (byte)((image[pos] & 0x0F) | ((value << 4) & 0xF0));
          image[pos + 1] = (byte)((value >> 4) & 0xFF);
        }
      }
    }
  }

  /// <summary>
  /// Reads the FAT12 cluster chain starting at <paramref name="start"/>.
  /// </summary>
  private static List<int> Chain(byte[] image, FatGeometry g, int start) {
    var chain = new List<int>();
    var seen = new HashSet<int>();
    var c = start;
    while (c >= 2 && c < 0xFF8 && seen.Add(c)) {
      chain.Add(c);
      c = g.ReadFat12(image, c);
    }
    return chain;
  }

  /// <summary>
  /// Finds a directory entry by 8.3 short name anywhere in the tree and returns
  /// its start cluster. Walks the fixed root then recurses subdirectories.
  /// </summary>
  private static int FindStartCluster(byte[] image, FatGeometry g, string shortName) {
    var found = -1;
    var seenDirs = new HashSet<int>();

    void Walk(byte[] dir) {
      var entries = dir.Length / 32;
      for (var i = 0; i < entries && found < 0; i++) {
        var off = i * 32;
        var first = dir[off];
        if (first == 0x00) break;
        if (first == 0xE5) continue;
        if (first == (byte)'.') continue;          // '.' / '..' self/parent entries
        var attr = dir[off + 11];
        if ((attr & 0x3F) == 0x0F) continue;     // LFN
        if ((attr & 0x08) != 0) continue;          // volume label
        var name = Encoding.ASCII.GetString(dir, off, 8).TrimEnd();
        var ext = Encoding.ASCII.GetString(dir, off + 8, 3).TrimEnd();
        var full = ext.Length > 0 ? $"{name}.{ext}" : name;
        var start = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(off + 26));
        if (full.Equals(shortName, StringComparison.OrdinalIgnoreCase)) { found = start; return; }
        if ((attr & 0x10) != 0 && start >= 2 && seenDirs.Add(start)) {
          // recurse into the subdir's clusters
          var sub = ReadChainBytes(image, g, start);
          Walk(sub);
        }
      }
    }

    var rootOff = (g.ReservedSectors + g.FatCount * g.FatSize) * g.BytesPerSector;
    var rootBytes = g.RootDirSectors * g.BytesPerSector;
    Walk(image.AsSpan(rootOff, rootBytes).ToArray());
    return found;
  }

  private static byte[] ReadChainBytes(byte[] image, FatGeometry g, int start) {
    using var ms = new MemoryStream();
    foreach (var c in Chain(image, g, start)) {
      var off = g.ClusterOffset(c);
      ms.Write(image, (int)off, g.ClusterSize);
    }
    return ms.ToArray();
  }

  private static int FindFreeCluster(byte[] image, FatGeometry g, int totalDataClusters, HashSet<int> avoid) {
    for (var c = 2; c <= totalDataClusters + 1; c++) {
      if (avoid.Contains(c)) continue;
      if (g.ReadFat12(image, c) == 0) return c;
    }
    return -1;
  }

  /// <summary>
  /// Relocates the SECOND cluster of the chain starting at <paramref name="start"/>
  /// to a free cluster that is NOT adjacent to the first, splitting the chain
  /// into two non-contiguous runs. Copies the cluster bytes, repoints the FAT
  /// links, and frees the old slot. The directory/file content is preserved but
  /// its on-disk layout is now fragmented.
  /// </summary>
  private static void FragmentSecondCluster(byte[] image, FatGeometry g, int start, int totalDataClusters) {
    var chain = Chain(image, g, start);
    Assert.That(chain.Count, Is.GreaterThanOrEqualTo(2),
      "fragmentation helper needs a chain of at least two clusters");

    var oldSecond = chain[1];
    var avoid = new HashSet<int>(chain);
    // Pick a genuinely-free cluster (FAT entry 0) that is not adjacent to the
    // first cluster, so the result is two non-contiguous runs. Search from the
    // low end and verify freeness so we never land on another owner's cluster.
    var newSecond = -1;
    for (var c = 2; c <= totalDataClusters + 1; c++) {
      if (avoid.Contains(c)) continue;
      if (Math.Abs(c - chain[0]) <= 1) continue;
      if (g.ReadFat12(image, c) == 0) { newSecond = c; break; }
    }
    Assert.That(newSecond, Is.GreaterThanOrEqualTo(2), "need a free cluster to fragment into");

    // copy the bytes
    Array.Copy(image, g.ClusterOffset(oldSecond), image, g.ClusterOffset(newSecond), g.ClusterSize);

    // repoint: first -> newSecond -> (whatever oldSecond pointed at)
    var afterSecond = g.ReadFat12(image, oldSecond);
    g.WriteFat12(image, chain[0], newSecond);
    g.WriteFat12(image, newSecond, afterSecond);
    g.WriteFat12(image, oldSecond, 0); // free the old slot
    // zero old cluster bytes so a stale copy can't masquerade as live data
    Array.Clear(image, (int)g.ClusterOffset(oldSecond), g.ClusterSize);
  }

  private static int TotalDataClusters(FatGeometry g, byte[] image) {
    var totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(19));
    var ts = totalSectors == 0 ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(32)) : totalSectors;
    return (ts - g.FirstDataSector) / g.SectorsPerCluster;
  }

  private static Dictionary<string, byte[]> ExtractAll(MemoryStream ms) {
    ms.Position = 0;
    var r = new FatReader(ms);
    return r.Entries.Where(e => !e.IsDirectory)
                    .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
  }

  /// <summary>How many on-disk runs the chain for <paramref name="start"/> breaks into.</summary>
  private static int RunCount(byte[] image, FatGeometry g, int start) {
    var chain = Chain(image, g, start);
    if (chain.Count == 0) return 0;
    var runs = 1;
    for (var i = 1; i < chain.Count; i++)
      if (chain[i] != chain[i - 1] + 1) runs++;
    return runs;
  }

  // ── Tests ────────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void NestedTree_RoundTrips_AfterConsolidateAtStart() {
    // A nested tree with files at several depths. Fragmentation is introduced
    // by splitting a couple of multi-cluster nested files' chains in two, then
    // defragmenting and confirming every nested file still round-trips.
    var extra = new byte[900];
    for (var i = 0; i < extra.Length; i++) extra[i] = (byte)(i * 5 + 1);
    var util = new byte[1300];
    for (var i = 0; i < util.Length; i++) util[i] = (byte)(i * 11 + 7);

    var w = new FatWriter();
    w.AddFile("readme.txt", Encoding.ASCII.GetBytes("root readme payload"));
    w.AddFile("docs/intro.txt", Encoding.ASCII.GetBytes("introduction text here"));
    w.AddFile("docs/api/reference.txt", Encoding.ASCII.GetBytes("deep reference contents"));
    w.AddFile("docs/api/extra.txt", extra);  // multi-cluster
    w.AddFile("src/main.txt", Encoding.ASCII.GetBytes("primary source body"));
    w.AddFile("src/lib/util.txt", util);     // multi-cluster
    var image = w.BuildAutoSized();

    // Fragment the two multi-cluster nested files so their chains are split.
    var g0 = FatGeometry.Read(image);
    var tdc = TotalDataClusters(g0, image);
    FragmentSecondCluster(image, g0, FindStartCluster(image, g0, "EXTRA.TXT"), tdc);
    FragmentSecondCluster(image, g0, FindStartCluster(image, g0, "UTIL.TXT"), tdc);

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
    });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count), "file count unchanged after defrag");
    foreach (var (path, data) in before) {
      Assert.That(after, Contains.Key(path), $"nested file {path} still present at its path");
      Assert.That(after[path], Is.EqualTo(data), $"nested file {path} content intact");
    }
  }

  [Test, Category("Fusion")]
  public void FragmentedDirectory_IsFusedIntoOneRun_AndChildrenStillRead() {
    // Build a subdirectory big enough to span at least two clusters, then split
    // its cluster chain so it occupies two non-adjacent runs.
    var w = new FatWriter();
    for (var i = 0; i < 40; i++)
      w.AddFile($"data/file{i:D2}.txt", Encoding.ASCII.GetBytes($"payload number {i}"));
    var image = w.BuildAutoSized();

    var g = FatGeometry.Read(image);
    Assert.That(g.FatSize, Is.GreaterThan(0));
    var totalClusters = TotalDataClusters(g, image);

    var dirStart = FindStartCluster(image, g, "DATA");
    Assert.That(dirStart, Is.GreaterThanOrEqualTo(2), "DATA subdirectory located");
    Assert.That(Chain(image, g, dirStart).Count, Is.GreaterThanOrEqualTo(2),
      "DATA must span multiple clusters for the fragmentation to be meaningful");

    FragmentSecondCluster(image, g, dirStart, totalClusters);

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Pre-condition: the directory really is fragmented now.
    Assert.That(RunCount(image, g, dirStart), Is.GreaterThan(1),
      "directory chain is fragmented before defrag");

    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
    });

    // Re-read the image bytes and re-locate the directory (its start cluster may
    // have moved during consolidation).
    ms.Position = 0;
    var defragged = ms.ToArray();
    var g2 = FatGeometry.Read(defragged);
    var newDirStart = FindStartCluster(defragged, g2, "DATA");
    Assert.That(newDirStart, Is.GreaterThanOrEqualTo(2), "DATA still present after defrag");

    Assert.That(RunCount(defragged, g2, newDirStart), Is.EqualTo(1),
      "fragmented directory fused into a single contiguous cluster run");

    // Children must still read at their exact paths with intact content.
    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count), "all children survive the fuse");
    foreach (var (path, data) in before) {
      Assert.That(after, Contains.Key(path), $"{path} present after directory fuse");
      Assert.That(after[path], Is.EqualTo(data), $"{path} content intact after directory fuse");
    }
  }

  [Test, Category("Fusion")]
  public void FragmentedFileInSubdirectory_BecomesContiguous_AndRoundTrips() {
    var w = new FatWriter();
    w.AddFile("sub/keep.txt", Encoding.ASCII.GetBytes("sibling"));
    w.AddFile("sub/big.bin", new byte[2000]); // spans several clusters
    var payload = new byte[2000];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7 + 3);
    var w2 = new FatWriter();
    w2.AddFile("sub/keep.txt", Encoding.ASCII.GetBytes("sibling"));
    w2.AddFile("sub/big.bin", payload);
    var image = w2.BuildAutoSized();

    var g = FatGeometry.Read(image);
    var totalClusters = TotalDataClusters(g, image);
    var fileStart = FindStartCluster(image, g, "BIG.BIN");
    Assert.That(fileStart, Is.GreaterThanOrEqualTo(2), "nested file located");
    Assert.That(Chain(image, g, fileStart).Count, Is.GreaterThanOrEqualTo(2),
      "nested file must span multiple clusters");

    FragmentSecondCluster(image, g, fileStart, totalClusters);

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    Assert.That(RunCount(image, g, fileStart), Is.GreaterThan(1),
      "nested file is fragmented before defrag");

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
    });

    ms.Position = 0;
    var defragged = ms.ToArray();
    var g2 = FatGeometry.Read(defragged);
    var newFileStart = FindStartCluster(defragged, g2, "BIG.BIN");
    Assert.That(newFileStart, Is.GreaterThanOrEqualTo(2), "nested file present after defrag");
    Assert.That(RunCount(defragged, g2, newFileStart), Is.EqualTo(1),
      "fragmented nested file became contiguous");

    var after = ExtractAll(ms);
    Assert.That(after["sub/big.bin"], Is.EqualTo(payload), "fragmented nested file round-trips byte-for-byte");
    Assert.That(after["sub/keep.txt"], Is.EqualTo(Encoding.ASCII.GetBytes("sibling")), "sibling intact");
  }

  [Test, Category("Validity")]
  public void FusedDirectory_HasConsistentDotAndDotDotPointers() {
    // After moving a subdirectory, its own '.' entry (self pointer) and the
    // '..' entry of every child subdirectory must reference the directory's NEW
    // start cluster, otherwise the on-disk tree is internally inconsistent even
    // though our reader (which navigates via the parent dirent) still reads it.
    var w = new FatWriter();
    for (var i = 0; i < 30; i++)
      w.AddFile($"parent/file{i:D2}.txt", Encoding.ASCII.GetBytes($"p{i}"));
    w.AddFile("parent/child/leaf.txt", Encoding.ASCII.GetBytes("leaf body"));
    var image = w.BuildAutoSized();

    var g = FatGeometry.Read(image);
    var totalClusters = TotalDataClusters(g, image);
    var parentStart = FindStartCluster(image, g, "PARENT");
    Assert.That(Chain(image, g, parentStart).Count, Is.GreaterThanOrEqualTo(2),
      "PARENT must span multiple clusters");

    FragmentSecondCluster(image, g, parentStart, totalClusters);

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
    });

    ms.Position = 0;
    var defragged = ms.ToArray();
    var g2 = FatGeometry.Read(defragged);
    var newParentStart = FindStartCluster(defragged, g2, "PARENT");
    var childStart = FindStartCluster(defragged, g2, "CHILD");
    Assert.That(newParentStart, Is.GreaterThanOrEqualTo(2));
    Assert.That(childStart, Is.GreaterThanOrEqualTo(2));

    // '.' inside PARENT points at PARENT's new start cluster.
    var parentBytes = ReadChainBytes(defragged, g2, newParentStart);
    var dotSelf = BinaryPrimitives.ReadUInt16LittleEndian(parentBytes.AsSpan(26));
    Assert.That(dotSelf, Is.EqualTo(newParentStart),
      "PARENT '.' self-pointer follows the directory to its new start cluster");

    // '..' inside CHILD points back at PARENT's new start cluster.
    var childBytes = ReadChainBytes(defragged, g2, childStart);
    var dotDot = BinaryPrimitives.ReadUInt16LittleEndian(childBytes.AsSpan(32 + 26));
    Assert.That(dotDot, Is.EqualTo(newParentStart),
      "CHILD '..' parent-pointer follows PARENT to its new start cluster");
  }

  [Test, Category("RebuildPath")]
  public void RebuildFallback_PreservesNestedTree() {
    // The rebuild fallback path (DefragRebuilder.Rebuild driven by the FAT
    // reader+writer delegates) must also preserve a nested directory tree:
    // it reads every live file at its full path and re-packs with the
    // subdir-aware writer.
    var w = new FatWriter();
    w.AddFile("readme.txt", Encoding.ASCII.GetBytes("root readme"));
    w.AddFile("docs/intro.txt", Encoding.ASCII.GetBytes("intro body"));
    w.AddFile("docs/api/reference.txt", Encoding.ASCII.GetBytes("reference body"));
    w.AddFile("src/lib/util.txt", Encoding.ASCII.GetBytes("utility body"));
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    var totalSectors = (int)(ms.Length / 512);

    var before = ExtractAll(ms);

    // Drive the rebuild path directly with the same delegates the FAT
    // descriptor's fallback uses.
    DefragRebuilder.Rebuild(ms,
      new DefragOptions { Mode = DefragMode.ConsolidateAtStart },
      readEntries: stream => {
        var r = new FatReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var fw = new FatWriter();
        foreach (var (n, d) in files) fw.AddFile(n, d);
        return fw.Build(totalSectors: totalSectors);
      });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count), "every nested file survives the rebuild");
    foreach (var (path, data) in before) {
      Assert.That(after, Contains.Key(path), $"nested file {path} still present at its path");
      Assert.That(after[path], Is.EqualTo(data), $"nested file {path} content intact");
    }
  }

  [Test, Category("RoundTrip")]
  public void ANestedFileWhoseFirstClusterMoves_HasItsDirectoryEntryRepointed() {
    // Packing against the tail moves every owner, so a nested file's FIRST
    // cluster changes — which is what makes the parent directory's entry need
    // repointing. Every earlier fixture only ever moved a nested file's later
    // clusters, so the entry never had to change and the walk that finds it was
    // never exercised past the root.
    var payload = new byte[1_800];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)(i * 13 + 3);

    var w = new FatWriter();
    w.AddFile("readme.txt", Encoding.ASCII.GetBytes("root readme payload"));
    w.AddFile("docs/deep.txt", payload);
    w.AddFile("docs/api/leaf.txt", Encoding.ASCII.GetBytes("leaf body"));
    var image = w.BuildAutoSized();

    var g = FatGeometry.Read(image);
    var startBefore = FindStartCluster(image, g, "DEEP.TXT");
    Assert.That(startBefore, Is.GreaterThanOrEqualTo(2), "the nested file has to exist to begin with");

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtEnd,
      Profile = LayoutProfile.Performance,
    });

    var packed = ms.ToArray();
    var g2 = FatGeometry.Read(packed);
    var startAfter = FindStartCluster(packed, g2, "DEEP.TXT");
    Assert.That(startAfter, Is.Not.EqualTo(startBefore),
      "nothing moved, so this fixture proves nothing — pick a mode that relocates the file");

    var after = ExtractAll(ms);
    foreach (var (path, data) in before) {
      Assert.That(after, Contains.Key(path), $"nested file {path} still present at its path");
      Assert.That(after[path], Is.EqualTo(data), $"nested file {path} content intact");
    }
  }
}
