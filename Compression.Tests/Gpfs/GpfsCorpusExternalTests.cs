#pragma warning disable CS1591
using Category = NUnit.Framework.CategoryAttribute;
using FileSystem.Gpfs;

namespace Compression.Tests.Gpfs;

/// <summary>
/// Optional clean-room acceptance tests for real IBM Storage Scale evidence.
/// The proprietary Storage Scale installation and large raw NSD images are never
/// downloaded by the test suite; point the two environment variables at corpus
/// roots produced by tools/gpfs-lab/run-controlled-corpus.sh.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public sealed class GpfsCorpusExternalTests {
  [Test, CancelAfter(300_000), Category("HappyPath")]
  public void ConfiguredIndependentCorpora_HaveResolvableIBMReportedRawAddresses() {
    var roots = GetConfiguredCorpusRoots();
    var corpora = roots.Select(LoadCorpus).ToArray();

    Assert.Multiple(() => {
      Assert.That(corpora[0].ValidateCompleteness().IsSatisfied, Is.True);
      Assert.That(corpora[1].ValidateCompleteness().IsSatisfied, Is.True);
      Assert.That(corpora[0].CorpusId, Is.Not.EqualTo(corpora[1].CorpusId));
      Assert.That(corpora[0].FilesystemUid, Is.Not.EqualTo(corpora[1].FilesystemUid));
    });

    foreach (var (root, corpus) in roots.Zip(corpora))
      CorrelateEveryCapture(root, corpus);
  }

  [Test, CancelAfter(300_000), Category("HappyPath")]
  public void ConfiguredCorpora_RebalancePairConstrainsDiskAddressPacking() {
    var roots = GetConfiguredCorpusRoots();

    foreach (var root in roots) {
      var before = LoadCapture(root, "050-block-plus-one");
      var after = LoadCapture(root, "055-rebalanced");
      var beforeEvidence = LoadProbeEvidence(root, "050-block-plus-one", before, "block-plus-one.bin");
      var afterEvidence = LoadProbeEvidence(root, "055-rebalanced", after, "block-plus-one.bin");

      var commonSlots = beforeEvidence.Pointers.Pointers
        .Where(static x => x.Replicas.Count > 0)
        .Join(
          afterEvidence.Pointers.Pointers.Where(static x => x.Replicas.Count > 0),
          static x => x.SlotIndex,
          static x => x.SlotIndex,
          static (beforePointer, afterPointer) => (beforePointer, afterPointer))
        .Where(static pair => !pair.beforePointer.Replicas.SequenceEqual(pair.afterPointer.Replicas))
        .ToArray();

      Assert.That(commonSlots, Is.Not.Empty,
        $"Corpus '{root}' did not produce an IBM-reported physical pointer change across 050/055.");

      var moved = commonSlots[0];
      var beforeAddress = moved.beforePointer.Replicas[0];
      var afterAddress = moved.afterPointer.Replicas[0];
      var beforeRecord = ReadFirstReplica(root, "050-block-plus-one", before, beforeEvidence.Inode);
      var afterRecord = ReadFirstReplica(root, "055-rebalanced", after, afterEvidence.Inode);

      var candidates = GpfsRawCorrelation.IntersectAddressCandidates(
        beforeRecord.Bytes,
        beforeAddress,
        afterRecord.Bytes,
        afterAddress);

      TestContext.WriteLine(
        $"{Path.GetFileName(root)} slot {moved.beforePointer.SlotIndex}: " +
        $"{beforeAddress} -> {afterAddress}; aligned integer candidates={candidates.Count}");
      foreach (var candidate in candidates)
        TestContext.WriteLine($"  {candidate}");

      Assert.That(candidates, Is.Not.Empty,
        "No aligned integer disk-address encoding survived the paired capture. Extend the clean-room candidate search before claiming a packing rule.");
    }
  }

  private static string[] GetConfiguredCorpusRoots() {
    var first = Environment.GetEnvironmentVariable("CWB_GPFS_CORPUS_A");
    var second = Environment.GetEnvironmentVariable("CWB_GPFS_CORPUS_B");
    if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) {
      Assert.Ignore(
        "Set CWB_GPFS_CORPUS_A and CWB_GPFS_CORPUS_B to two independently formatted GPFS corpus roots produced by tools/gpfs-lab.");
      return [];
    }

    if (!Directory.Exists(first) || !Directory.Exists(second)) {
      Assert.Ignore("Configured GPFS corpus root does not exist.");
      return [];
    }

    return [Path.GetFullPath(first), Path.GetFullPath(second)];
  }

  private static GpfsEvidenceCorpus LoadCorpus(string root)
    => new(GpfsEvidenceCorpus.RequiredCaptureIds.Select(captureId => LoadCapture(root, captureId)).ToArray());

  private static GpfsEvidenceManifest LoadCapture(string root, string captureId) {
    var manifestPath = Path.Combine(root, captureId, "manifest.tsv");
    Assert.That(File.Exists(manifestPath), Is.True, $"Missing GPFS manifest: {manifestPath}");
    return GpfsEvidenceManifest.Parse(File.ReadAllText(manifestPath));
  }

  private static void CorrelateEveryCapture(string root, GpfsEvidenceCorpus corpus) {
    foreach (var manifest in corpus.Captures) {
      var captureId = manifest.Metadata["capture-id"];
      var captureRoot = Path.Combine(root, captureId);
      var decision = manifest.ValidateCaptureCompleteness();
      Assert.That(decision.IsSatisfied, Is.True, decision.Reason);

      var images = new Dictionary<int, Stream>();
      try {
        foreach (var nsd in manifest.Nsds)
          images.Add(nsd.DiskId, File.Open(Path.Combine(captureRoot, nsd.ImagePath), FileMode.Open, FileAccess.Read, FileShare.Read));

        var physicalAddresses = new HashSet<GpfsDiskAddress>();
        foreach (var artifact in manifest.Artifacts.Where(static x => x.Kind == "tsdbfs")) {
          var text = File.ReadAllText(Path.Combine(captureRoot, artifact.Path));
          var inode = GpfsOracleParser.ParseTsdbfsInode(text);
          var replicas = GpfsRawCorrelation.ReadInodeReplicas(manifest, inode, images);
          Assert.That(replicas, Has.Count.EqualTo(inode.PhysicalAddresses.Count));
          foreach (var address in inode.PhysicalAddresses)
            physicalAddresses.Add(address);

          if (!text.Contains("Disk pointers [", StringComparison.Ordinal))
            continue;
          var pointers = GpfsTsdbfsPointerParser.Parse(text);
          Assert.That(pointers.DeclaredSlotCount, Is.EqualTo(inode.AddressSlots));
          foreach (var pointer in pointers.Pointers)
            foreach (var address in pointer.Replicas)
              physicalAddresses.Add(address);

          foreach (var replica in replicas) {
            var checksumCandidates = GpfsRawCorrelation.FindUInt32Candidates(replica.Bytes, inode.Checksum);
            TestContext.WriteLine(
              $"{corpus.CorpusId}/{captureId} inode {inode.InodeNumber} replica {replica.Address}: " +
              $"checksum literal candidates={checksumCandidates.Count}");
          }
        }

        var mmfileidArtifact = manifest.Artifacts.Single(static x => x.Kind == "mmfileid");
        var mmfileid = GpfsMmfileidAggregateParser.Parse(File.ReadAllText(Path.Combine(captureRoot, mmfileidArtifact.Path)));
        var queries = mmfileid.ToDictionary(static x => x.QueryAddress);
        foreach (var address in physicalAddresses) {
          Assert.That(queries.TryGetValue(address, out var query), Is.True,
            $"{corpus.CorpusId}/{captureId}: no archived mmfileid query for tsdbfs address {address}.");
          Assert.That(query!.Owners, Is.Not.Empty,
            $"{corpus.CorpusId}/{captureId}: mmfileid did not identify an owner for tsdbfs address {address}.");
        }
      } finally {
        foreach (var image in images.Values)
          image.Dispose();
      }
    }
  }

  private static GpfsProbeEvidence LoadProbeEvidence(
    string root,
    string captureId,
    GpfsEvidenceManifest manifest,
    string probeName) {
    var captureRoot = Path.Combine(root, captureId);
    var artifact = manifest.Artifacts.Single(x => x.Kind == "tsdbfs" && x.Path.Contains(probeName, StringComparison.Ordinal));
    var text = File.ReadAllText(Path.Combine(captureRoot, artifact.Path));
    return new GpfsProbeEvidence(
      GpfsOracleParser.ParseTsdbfsInode(text),
      GpfsTsdbfsPointerParser.Parse(text));
  }

  private static GpfsInodeReplicaBytes ReadFirstReplica(
    string root,
    string captureId,
    GpfsEvidenceManifest manifest,
    GpfsInodeOracle inode) {
    var captureRoot = Path.Combine(root, captureId);
    var images = new Dictionary<int, Stream>();
    try {
      foreach (var nsd in manifest.Nsds)
        images.Add(nsd.DiskId, File.Open(Path.Combine(captureRoot, nsd.ImagePath), FileMode.Open, FileAccess.Read, FileShare.Read));
      return GpfsRawCorrelation.ReadInodeReplicas(manifest, inode, images).First();
    } finally {
      foreach (var image in images.Values)
        image.Dispose();
    }
  }

  private sealed record GpfsProbeEvidence(GpfsInodeOracle Inode, GpfsDiskPointerSet Pointers);
}
