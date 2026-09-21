#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Refs;

namespace Compression.Tests.Refs;

/// <summary>
/// Executing coverage for the offline ReFS whole-file block clone.
///
/// <see cref="RefsOfflineBlockCloner"/> and <see cref="RefsCowBlockRefcountEditor"/>
/// previously had no test that ran: their only caller was an external-oracle
/// fixture that skips unless two environment variables point at a real ReFS
/// image, which CI never sets. A wrong block clone corrupts across files
/// silently — two streams sharing clusters they should not, or reference counts
/// that disagree with reality — so the primitive is driven here against images
/// built by <see cref="RefsSyntheticVolume"/> and checked with
/// <see cref="RefsImageProbe"/>, which decodes the resulting bytes without going
/// back through the code that wrote them.
/// </summary>
[TestFixture]
public sealed class RefsBlockCloneTests {
  private const int ClusterSize = RefsSyntheticVolume.ClusterSize;

  private static byte[] Pattern(byte seed, int length) {
    var result = new byte[length];
    for (var i = 0; i < length; ++i) result[i] = (byte)(seed + i * 31);
    return result;
  }

  [Test, Category("HappyPath")]
  public void SyntheticVolume_IsReadableAsARefsNamespace() {
    var source = Pattern(0x11, 2 * ClusterSize);
    var destination = Pattern(0x77, 2 * ClusterSize);
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", source)
      .WithFile("bravo.bin", destination)
      .Build();

    using var stream = new MemoryStream(image, writable: true);
    var metadata = RefsMetadataReader.Open(stream);
    var files = new RefsNamespaceReader(metadata).ReadAll();

    Assert.Multiple(() => {
      Assert.That(files.Select(f => f.Path), Is.EquivalentTo(new[] { "alpha.bin", "bravo.bin" }));
      var alpha = files.Single(f => f.Path == "alpha.bin");
      Assert.That(alpha.IsResident, Is.False);
      Assert.That(alpha.Size, Is.EqualTo(source.Length));
      Assert.That(alpha.AllocatedSize, Is.EqualTo(source.Length));
      Assert.That(alpha.Extents, Has.Count.EqualTo(1));
      Assert.That(alpha.Extents[0].ClusterCount, Is.EqualTo(2));
    });

    var probe = new RefsImageProbe(image);
    var probed = probe.ReadFiles(probe.ActiveCheckpoint());
    Assert.Multiple(() => {
      Assert.That(probe.ReadFileContent(probed["alpha.bin"]), Is.EqualTo(source));
      Assert.That(probe.ReadFileContent(probed["bravo.bin"]), Is.EqualTo(destination));
    });
  }

  [Test, Category("HappyPath")]
  public void WholeFileClone_SharesTheSourceClustersAndMaterialisesCountTwo() {
    var sourceBytes = Pattern(0x11, 2 * ClusterSize);
    var destinationBytes = Pattern(0x77, 2 * ClusterSize);
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", sourceBytes)
      .WithFile("bravo.bin", destinationBytes)
      .Build();

    var before = new RefsImageProbe(image);
    var beforeCheckpoint = before.ActiveCheckpoint();
    var beforeFiles = before.ReadFiles(beforeCheckpoint);
    var sourceClusters = beforeFiles["alpha.bin"].Clusters;
    var formerDestinationClusters = beforeFiles["bravo.bin"].Clusters;
    Assume.That(before.CountRefcountRows(beforeCheckpoint), Is.Zero,
      "the fixture must start with a sparse Block Refcount tree");

    using (var stream = new MemoryStream(image, writable: true))
      RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "bravo.bin");

    var after = new RefsImageProbe(image);
    var afterCheckpoint = after.ActiveCheckpoint();
    var afterFiles = after.ReadFiles(afterCheckpoint);
    var refcounts = after.ReadRefcounts(afterCheckpoint);
    var allocated = after.ReadAllocatedClusters(afterCheckpoint);

    Assert.Multiple(() => {
      // Structural: both stream layouts now name the same physical clusters.
      Assert.That(afterFiles["alpha.bin"].Clusters, Is.EqualTo(sourceClusters));
      Assert.That(afterFiles["bravo.bin"].Clusters, Is.EqualTo(sourceClusters));
      Assert.That(afterFiles["bravo.bin"].Size, Is.EqualTo((ulong)sourceBytes.Length));
      Assert.That(afterFiles["bravo.bin"].AllocatedSize, Is.EqualTo((ulong)sourceBytes.Length));

      // Structural: every shared cluster carries reference count 2.
      foreach (var cluster in sourceClusters) {
        Assert.That(refcounts.ContainsKey(cluster), Is.True,
          $"PLCN 0x{cluster:X} has no Block Refcount row after the clone.");
        Assert.That(refcounts[cluster].Count, Is.EqualTo(2),
          $"PLCN 0x{cluster:X} is not shared by exactly two streams.");
      }

      // Structural: the row's own total field agrees with its words.
      foreach (var (start, totals) in after.ReadRefcountTotals(afterCheckpoint))
        Assert.That(totals.Stated, Is.EqualTo(totals.Actual),
          $"Block Refcount row 0x{start:X} states a total its words do not add up to.");

      // Round-trip: both names read back as the source content.
      Assert.That(after.ReadFileContent(afterFiles["alpha.bin"]), Is.EqualTo(sourceBytes));
      Assert.That(after.ReadFileContent(afterFiles["bravo.bin"]), Is.EqualTo(sourceBytes));

      // Structural: the destination's former allocation went back to the pool.
      foreach (var cluster in formerDestinationClusters)
        Assert.That(allocated, Does.Not.Contain(cluster),
          $"The destination's former PLCN 0x{cluster:X} is still marked allocated.");
      foreach (var cluster in sourceClusters)
        Assert.That(allocated, Does.Contain(cluster),
          $"Shared PLCN 0x{cluster:X} must stay allocated.");
    });
  }

  [Test, Category("HappyPath")]
  public void FirstClone_OnAZeroSlotOfAnExistingRow_BecomesTwoWithoutAddingARow() {
    var sourceBytes = Pattern(0x21, ClusterSize);
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", sourceBytes)
      .WithFile("bravo.bin", Pattern(0x5A, ClusterSize))
      .WithEmptyRefcountRow()
      .Build();

    var before = new RefsImageProbe(image);
    var beforeCheckpoint = before.ActiveCheckpoint();
    var sourceClusters = before.ReadFiles(beforeCheckpoint)["alpha.bin"].Clusters;
    Assert.Multiple(() => {
      Assert.That(before.CountRefcountRows(beforeCheckpoint), Is.EqualTo(1),
        "the fixture must start with exactly one all-zero Block Refcount row");
      Assert.That(before.ReadRefcounts(beforeCheckpoint), Is.Empty,
        "no slot of the seeded row may carry a non-zero word yet");
    });

    using (var stream = new MemoryStream(image, writable: true))
      RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "bravo.bin");

    var after = new RefsImageProbe(image);
    var afterCheckpoint = after.ActiveCheckpoint();
    var refcounts = after.ReadRefcounts(afterCheckpoint);

    Assert.Multiple(() => {
      Assert.That(after.CountRefcountRows(afterCheckpoint), Is.EqualTo(1),
        "an existing row must be updated, not duplicated");
      foreach (var cluster in sourceClusters)
        Assert.That(refcounts[cluster].Count, Is.EqualTo(2),
          $"A zero slot must become 2 on first sharing, not 1 — PLCN 0x{cluster:X}.");
      Assert.That(refcounts, Has.Count.EqualTo(sourceClusters.Count),
        "only the shared clusters may gain a non-zero reference word");
      foreach (var (start, totals) in after.ReadRefcountTotals(afterCheckpoint))
        Assert.That(totals.Stated, Is.EqualTo(totals.Actual),
          $"Block Refcount row 0x{start:X} states a stale total.");
    });
  }

  [Test, Category("HappyPath")]
  public void Clone_PublishesObjectTableAndRefcountRootsInOneAlternateCheckpoint() {
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", Pattern(0x31, 2 * ClusterSize))
      .WithFile("bravo.bin", Pattern(0x8F, 2 * ClusterSize))
      .Build();

    var before = new RefsImageProbe(image);
    var beforeCheckpoint = before.ActiveCheckpoint();
    var beforeFiles = before.ReadFiles(beforeCheckpoint);
    var formerDestinationClusters = beforeFiles["bravo.bin"].Clusters;
    var beforeAllocated = before.ReadAllocatedClusters(beforeCheckpoint);

    using (var stream = new MemoryStream(image, writable: true))
      RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "bravo.bin");

    var after = new RefsImageProbe(image);
    var afterCheckpoint = after.ActiveCheckpoint();
    var stale = after.ReadCheckpoint(beforeCheckpoint.Lcn);
    var afterAllocated = after.ReadAllocatedClusters(afterCheckpoint);

    Assert.Multiple(() => {
      // The publication is an alternate-slot swap, not an in-place edit.
      Assert.That(afterCheckpoint.Lcn, Is.Not.EqualTo(beforeCheckpoint.Lcn));
      Assert.That(afterCheckpoint.Clock, Is.GreaterThan(beforeCheckpoint.Clock));

      // Object Table #0 and Block Refcount #6 moved in the same publication;
      // a checkpoint carrying one without the other is the silent-corruption
      // case this primitive exists to avoid.
      Assert.That(afterCheckpoint.Roots[0], Is.Not.EqualTo(beforeCheckpoint.Roots[0]),
        "Object Table root #0 did not move.");
      Assert.That(afterCheckpoint.Roots[6], Is.Not.EqualTo(beforeCheckpoint.Roots[6]),
        "Block Refcount root #6 did not move.");
      Assert.That(afterCheckpoint.Roots[1], Is.Not.EqualTo(beforeCheckpoint.Roots[1]),
        "Medium Allocator root #1 must account for the newly reserved CoW pages.");
      Assert.That(afterCheckpoint.Roots[7], Is.EqualTo(beforeCheckpoint.Roots[7]),
        "The Container Table has no business changing during a block clone.");

      // The superseded slot is untouched and still describes the pre-clone volume.
      Assert.That(stale.Clock, Is.EqualTo(beforeCheckpoint.Clock));
      Assert.That(stale.Roots[0], Is.EqualTo(beforeCheckpoint.Roots[0]));
      Assert.That(stale.Roots[6], Is.EqualTo(beforeCheckpoint.Roots[6]));
      Assert.That(after.ReadFiles(stale)["bravo.bin"].Clusters, Is.EqualTo(formerDestinationClusters));
      Assert.That(after.CountRefcountRows(stale), Is.Zero);

      // Every page the replacement roots point at is newly reserved storage that
      // the published allocator now owns.
      foreach (var rootIndex in new[] { 0, 1, 6 })
        foreach (var lcn in afterCheckpoint.Roots[rootIndex]) {
          var physical = after.ToPhysical(lcn);
          Assert.That(beforeAllocated, Does.Not.Contain(physical),
            $"Root #{rootIndex} was rebuilt over live PLCN 0x{physical:X}.");
          Assert.That(afterAllocated, Does.Contain(physical),
            $"Root #{rootIndex} page PLCN 0x{physical:X} is not marked allocated by the published allocator.");
        }

      // Reclamation is visible only through the new checkpoint.
      foreach (var cluster in formerDestinationClusters) {
        Assert.That(beforeAllocated, Does.Contain(cluster));
        Assert.That(afterAllocated, Does.Not.Contain(cluster));
      }
    });
  }

  [Test, Category("ErrorHandling")]
  public void Clone_AbortedWhilePublishing_LeavesTheVolumeExactlyAsItWas() {
    var sourceBytes = Pattern(0x41, 2 * ClusterSize);
    var destinationBytes = Pattern(0x9D, 2 * ClusterSize);
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", sourceBytes)
      .WithFile("bravo.bin", destinationBytes)
      .Build();

    var before = new RefsImageProbe(image);
    var beforeCheckpoint = before.ActiveCheckpoint();
    var beforeFiles = before.ReadFiles(beforeCheckpoint);
    var beforeAllocated = before.ReadAllocatedClusters(beforeCheckpoint);
    var alternate = before.CheckpointLcns.Single(lcn => lcn != beforeCheckpoint.Lcn);

    using (var stream = new WriteBarrierStream(
             image,
             (long)alternate * ClusterSize,
             (long)(alternate + (ulong)before.ClustersPerPage) * ClusterSize)) {
      Assert.Throws<IOException>(() => RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "bravo.bin"));
      Assert.That(stream.Tripped, Is.True, "the abort must happen while the alternate checkpoint is written");
    }

    var after = new RefsImageProbe(image);
    var afterCheckpoint = after.ActiveCheckpoint();
    var afterFiles = after.ReadFiles(afterCheckpoint);

    Assert.Multiple(() => {
      Assert.That(afterCheckpoint.Lcn, Is.EqualTo(beforeCheckpoint.Lcn),
        "a failed publication must not change which checkpoint is active");
      Assert.That(afterCheckpoint.Clock, Is.EqualTo(beforeCheckpoint.Clock));
      Assert.That(afterCheckpoint.Roots, Is.EqualTo(beforeCheckpoint.Roots));
      Assert.That(after.CountRefcountRows(afterCheckpoint), Is.Zero,
        "no Block Refcount row may become live through an aborted clone");
      Assert.That(afterFiles["alpha.bin"].Clusters, Is.EqualTo(beforeFiles["alpha.bin"].Clusters));
      Assert.That(afterFiles["bravo.bin"].Clusters, Is.EqualTo(beforeFiles["bravo.bin"].Clusters));
      Assert.That(after.ReadFileContent(afterFiles["alpha.bin"]), Is.EqualTo(sourceBytes));
      Assert.That(after.ReadFileContent(afterFiles["bravo.bin"]), Is.EqualTo(destinationBytes));
      Assert.That(after.ReadAllocatedClusters(afterCheckpoint), Is.EquivalentTo(beforeAllocated),
        "the destination's allocation must not be reclaimed before the clone is durable");
    });
  }

  [Test, Category("HappyPath")]
  public void WritingToOneCloneAfterwards_LeavesTheOtherUntouched() {
    var sourceBytes = Pattern(0x51, 2 * ClusterSize);
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", sourceBytes)
      .WithFile("bravo.bin", Pattern(0xB3, 2 * ClusterSize))
      .Build();

    using (var stream = new MemoryStream(image, writable: true))
      RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "bravo.bin");

    var cloned = new RefsImageProbe(image);
    var shared = cloned.ReadFiles(cloned.ActiveCheckpoint())["alpha.bin"].Clusters;
    var replacement = Pattern(0xE7, 2 * ClusterSize);

    using (var stream = new MemoryStream(image, writable: true))
      RefsOfflineModifier.Add(stream, [ArchiveInputInfo.InMemory("bravo.bin", replacement)]);

    var after = new RefsImageProbe(image);
    var afterCheckpoint = after.ActiveCheckpoint();
    var afterFiles = after.ReadFiles(afterCheckpoint);
    var refcounts = after.ReadRefcounts(afterCheckpoint);
    var allocated = after.ReadAllocatedClusters(afterCheckpoint);

    Assert.Multiple(() => {
      Assert.That(afterFiles["alpha.bin"].Clusters, Is.EqualTo(shared),
        "the untouched clone must keep its extents");
      Assert.That(after.ReadFileContent(afterFiles["alpha.bin"]), Is.EqualTo(sourceBytes),
        "writing to one clone changed the other's bytes");
      Assert.That(afterFiles["bravo.bin"].Clusters, Is.Not.EqualTo(shared),
        "the written clone must land on fresh clusters, not over the shared ones");
      Assert.That(after.ReadFileContent(afterFiles["bravo.bin"]), Is.EqualTo(replacement));

      foreach (var cluster in shared) {
        Assert.That(allocated, Does.Contain(cluster),
          $"Shared PLCN 0x{cluster:X} was freed while a stream still references it.");
        var count = refcounts.TryGetValue(cluster, out var word) ? word.Count : (ushort)0;
        Assert.That(count, Is.LessThanOrEqualTo(1),
          $"PLCN 0x{cluster:X} still claims more than one owner after the second stream moved away.");
      }
    });
  }

  [Test, Category("ErrorHandling")]
  public void Clone_RefusesUnequalLogicalSizes() {
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", Pattern(0x61, ClusterSize))
      .WithFile("bravo.bin", Pattern(0xC1, 2 * ClusterSize))
      .Build();
    var untouched = (byte[])image.Clone();

    using var stream = new MemoryStream(image, writable: true);
    var error = Assert.Throws<NotSupportedException>(
      () => RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "bravo.bin"));
    Assert.Multiple(() => {
      Assert.That(error!.Message, Does.Contain("equal source/destination logical sizes"));
      Assert.That(image, Is.EqualTo(untouched), "a refused clone must not touch a single byte");
    });
  }

  [Test, Category("ErrorHandling")]
  public void Clone_RefusesNonClusterAlignedFiles() {
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", Pattern(0x71, ClusterSize - 17))
      .WithFile("bravo.bin", Pattern(0xD1, ClusterSize - 17))
      .Build();
    var untouched = (byte[])image.Clone();

    using var stream = new MemoryStream(image, writable: true);
    var error = Assert.Throws<NotSupportedException>(
      () => RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "bravo.bin"));
    Assert.Multiple(() => {
      Assert.That(error!.Message, Does.Contain("cluster-aligned logical size"));
      Assert.That(image, Is.EqualTo(untouched));
    });
  }

  [Test, Category("ErrorHandling")]
  public void Clone_RefusesResidentStreams() {
    var content = Pattern(0x81, 48);
    var image = new RefsSyntheticVolume()
      .WithResidentFile("alpha.bin", content)
      .WithFile("bravo.bin", Pattern(0xE1, ClusterSize))
      .Build();
    var untouched = (byte[])image.Clone();

    using var stream = new MemoryStream(image, writable: true);
    var error = Assert.Throws<NotSupportedException>(
      () => RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "bravo.bin"));
    Assert.Multiple(() => {
      Assert.That(error!.Message, Does.Contain("extent-backed"));
      Assert.That(image, Is.EqualTo(untouched));
    });
  }

  [Test, Category("ErrorHandling")]
  public void Clone_RefusesEmptyStreams() {
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", [])
      .WithFile("bravo.bin", [])
      .Build();
    var untouched = (byte[])image.Clone();

    using var stream = new MemoryStream(image, writable: true);
    Assert.Throws<NotSupportedException>(
      () => RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "bravo.bin"));
    Assert.That(image, Is.EqualTo(untouched));
  }

  [Test, Category("ErrorHandling")]
  public void Clone_RefusesAlreadySharedSourceClusters() {
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", Pattern(0x91, ClusterSize))
      .WithFile("bravo.bin", Pattern(0xA1, ClusterSize))
      .WithFile("carol.bin", Pattern(0xB1, ClusterSize))
      .Build();

    using (var stream = new MemoryStream(image, writable: true))
      RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "bravo.bin");

    var afterFirst = (byte[])image.Clone();
    using var second = new MemoryStream(image, writable: true);
    var error = Assert.Throws<NotSupportedException>(
      () => RefsOfflineBlockCloner.CloneWholeFile(second, "alpha.bin", "carol.bin"));
    Assert.Multiple(() => {
      Assert.That(error!.Message, Does.Contain("already shared"));
      Assert.That(image, Is.EqualTo(afterFirst),
        "a refused repeat clone must not half-apply over the first one");
    });
  }

  [Test, Category("ErrorHandling")]
  public void Clone_RefusesTheSameFileAsSourceAndDestination() {
    var image = new RefsSyntheticVolume().WithFile("alpha.bin", Pattern(0xA5, ClusterSize)).Build();
    using var stream = new MemoryStream(image, writable: true);
    Assert.Throws<ArgumentException>(
      () => RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "alpha.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Clone_RefusesAnUnknownDestination() {
    var image = new RefsSyntheticVolume().WithFile("alpha.bin", Pattern(0xA6, ClusterSize)).Build();
    using var stream = new MemoryStream(image, writable: true);
    Assert.Throws<FileNotFoundException>(
      () => RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "ghost.bin"));
  }

  [Test, Category("EdgeCase")]
  public void Clone_OfAFragmentedSourceCarriesEveryRunToTheDestination() {
    var sourceBytes = Pattern(0xB7, 4 * ClusterSize);
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", sourceBytes, fragments: 3)
      .WithFile("bravo.bin", Pattern(0xC7, 4 * ClusterSize))
      .Build();

    var before = new RefsImageProbe(image);
    var sourceView = before.ReadFiles(before.ActiveCheckpoint())["alpha.bin"];
    Assume.That(sourceView.Extents, Has.Count.EqualTo(3), "the fixture must fragment the source");

    using (var stream = new MemoryStream(image, writable: true))
      RefsOfflineBlockCloner.CloneWholeFile(stream, "alpha.bin", "bravo.bin");

    var after = new RefsImageProbe(image);
    var afterCheckpoint = after.ActiveCheckpoint();
    var afterFiles = after.ReadFiles(afterCheckpoint);
    var refcounts = after.ReadRefcounts(afterCheckpoint);

    Assert.Multiple(() => {
      Assert.That(afterFiles["bravo.bin"].Extents, Has.Count.EqualTo(3),
        "a fragmented clone must reproduce every run, not collapse or drop one");
      Assert.That(afterFiles["bravo.bin"].Clusters, Is.EqualTo(sourceView.Clusters));
      Assert.That(afterFiles["bravo.bin"].Extents.Select(e => (e.Vcn, e.Lcn, e.Run)),
        Is.EqualTo(sourceView.Extents.Select(e => (e.Vcn, e.Lcn, e.Run))));
      foreach (var cluster in sourceView.Clusters)
        Assert.That(refcounts[cluster].Count, Is.EqualTo(2));
      Assert.That(after.ReadFileContent(afterFiles["bravo.bin"]), Is.EqualTo(sourceBytes));
      Assert.That(after.ReadFileContent(afterFiles["alpha.bin"]), Is.EqualTo(sourceBytes));
    });
  }

  /// <summary>A memory image whose writes fail inside one byte range, to
  /// simulate losing the medium at the exact moment of publication.</summary>
  private sealed class WriteBarrierStream(byte[] buffer, long failFrom, long failTo)
    : MemoryStream(buffer, writable: true) {
    public bool Tripped { get; private set; }

    public override void Write(byte[] buffer, int offset, int count) {
      this.Guard(count);
      base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer) {
      this.Guard(buffer.Length);
      base.Write(buffer);
    }

    private void Guard(int count) {
      var start = this.Position;
      if (start + count <= failFrom || start >= failTo) return;
      this.Tripped = true;
      throw new IOException("Simulated media failure while publishing the alternate ReFS checkpoint.");
    }
  }
}
