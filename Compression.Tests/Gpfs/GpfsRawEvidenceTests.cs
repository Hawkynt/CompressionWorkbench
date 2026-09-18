using FileSystem.Gpfs;

namespace Compression.Tests.Gpfs;

[TestFixture]
public class GpfsRawEvidenceTests {
  private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
  private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

  [Test]
  public void Manifest_RequiresAllSixIBMOracleFamilies() {
    var manifest = GpfsEvidenceManifest.Parse(BuildManifest("a", "uid-a", "000-anchor", includeMmfileid: false));

    var decision = manifest.ValidateCaptureCompleteness();

    Assert.Multiple(() => {
      Assert.That(decision.IsSatisfied, Is.False);
      Assert.That(decision.Reason, Does.Contain("mmfileid"));
    });
  }

  [Test]
  public void Manifest_AcceptsHashAddressedUnmountedMultiNsdCapture() {
    var manifest = GpfsEvidenceManifest.Parse(BuildManifest("a", "uid-a", "000-anchor"));

    var decision = manifest.ValidateCaptureCompleteness();

    Assert.That(decision.IsSatisfied, Is.True, decision.Reason);
    Assert.Multiple(() => {
      Assert.That(manifest.Nsds, Has.Count.EqualTo(2));
      Assert.That(manifest.Nsds.Select(static x => x.DiskId), Is.EqualTo(new[] { 1, 2 }));
      Assert.That(manifest.Metadata["capture-state"], Is.EqualTo("unmounted-clean"));
    });
  }

  [Test]
  public void Corpus_RejectsMissingSemanticTransition() {
    var captures = BuildCorpus("a", "uid-a").Captures
      .Where(static x => x.Metadata["capture-id"] != "030-one-subblock")
      .ToArray();

    var decision = new GpfsEvidenceCorpus(captures).ValidateCompleteness();

    Assert.That(decision.IsSatisfied, Is.False);
    Assert.That(decision.Reason, Does.Contain("030-one-subblock"));
  }

  [Test]
  public void PromotionGate_RejectsOneCorpusEvenWhenEveryCheckPasses() {
    var corpora = new[] { BuildCorpus("a", "uid-a") };
    var verifications = new[] { CompleteVerification("a") };

    var decision = GpfsReadOnlyPromotionGate.Evaluate(corpora, verifications);

    Assert.That(decision.IsSatisfied, Is.False);
    Assert.That(decision.Reason, Does.Contain("two independent").IgnoreCase);
  }

  [Test]
  public void PromotionGate_RejectsTwoCorpusIdsFromSameFilesystemUid() {
    var corpora = new[] { BuildCorpus("a", "same-uid"), BuildCorpus("b", "same-uid") };
    var verifications = new[] { CompleteVerification("a"), CompleteVerification("b") };

    var decision = GpfsReadOnlyPromotionGate.Evaluate(corpora, verifications);

    Assert.That(decision.IsSatisfied, Is.False);
    Assert.That(decision.Reason, Does.Contain("independently formatted"));
  }

  [Test]
  public void PromotionGate_RejectsIncompleteRawParserAgreement() {
    var corpora = new[] { BuildCorpus("a", "uid-a"), BuildCorpus("b", "uid-b") };
    var verifications = new[] {
      CompleteVerification("a"),
      CompleteVerification("b") with { BlockAllocationMap = false },
    };

    var decision = GpfsReadOnlyPromotionGate.Evaluate(corpora, verifications);

    Assert.That(decision.IsSatisfied, Is.False);
    Assert.That(decision.Reason, Does.Contain("Stage-1"));
  }

  [Test]
  public void PromotionGate_AcceptsTwoIndependentCompleteCorpora() {
    var corpora = new[] { BuildCorpus("a", "uid-a"), BuildCorpus("b", "uid-b") };
    var verifications = new[] { CompleteVerification("a"), CompleteVerification("b") };

    var decision = GpfsReadOnlyPromotionGate.Evaluate(corpora, verifications);

    Assert.That(decision.IsSatisfied, Is.True, decision.Reason);
  }

  [Test]
  public void MutationGate_RequiresRemountAndCleanMmfsckxTogether() {
    var incomplete = new GpfsMutationVerification("a", true, false, true, true, true);
    var complete = incomplete with { MmfsckxClean = true };

    Assert.Multiple(() => {
      Assert.That(GpfsMutationPromotionGate.Evaluate(incomplete).IsSatisfied, Is.False);
      Assert.That(GpfsMutationPromotionGate.Evaluate(complete).IsSatisfied, Is.True);
    });
  }

  [Test]
  public void RawDiff_CoalescesOnlyContiguousChangedBytes() {
    byte[] before = [0, 0, 0, 0, 0, 0, 0, 0];
    byte[] after = [0, 1, 2, 0, 0, 3, 0, 0];

    var ranges = GpfsRawDiff.FindChangedRanges(before, after);

    Assert.That(ranges, Is.EqualTo(new[] {
      new GpfsChangedRange(1, 2),
      new GpfsChangedRange(5, 1),
    }));
  }

  [TestCase(0x01, 0, 7)]
  [TestCase(0x04, 2, 5)]
  [TestCase(0x80, 7, 0)]
  public void RawDiff_ReportsBothPossibleBitmapBitConventions(byte mask, int lsbIndex, int msbIndex) {
    byte[] before = [0, 0, 0];
    byte[] after = [0, mask, 0];

    var found = GpfsRawDiff.TryFindSingleBitTransition(before, after, out var transition);

    Assert.That(found, Is.True);
    Assert.Multiple(() => {
      Assert.That(transition.ByteOffset, Is.EqualTo(1));
      Assert.That(transition.XorMask, Is.EqualTo(mask));
      Assert.That(transition.LsbFirstBitIndex, Is.EqualTo(lsbIndex));
      Assert.That(transition.MsbFirstBitIndex, Is.EqualTo(msbIndex));
    });
  }

  [Test]
  public void RawDiff_DoesNotCallMultiBitChangeASingleAllocationTransition() {
    byte[] before = [0, 0];
    byte[] after = [0, 3];

    Assert.That(GpfsRawDiff.TryFindSingleBitTransition(before, after, out _), Is.False);
  }

  private static GpfsReadOnlyVerification CompleteVerification(string corpusId)
    => new(corpusId, true, true, true, true, true, true, true, true, true, true, true);

  private static GpfsEvidenceCorpus BuildCorpus(string corpusId, string filesystemUid)
    => new(GpfsEvidenceCorpus.RequiredCaptureIds
      .Select(captureId => GpfsEvidenceManifest.Parse(BuildManifest(corpusId, filesystemUid, captureId)))
      .ToArray());

  private static string BuildManifest(string corpusId, string filesystemUid, string captureId, bool includeMmfileid = true) {
    var lines = new List<string> {
      $"meta\tcorpus-id\t{corpusId}",
      $"meta\tcapture-id\t{captureId}",
      $"meta\toperation\t{captureId}",
      "meta\tstorage-scale-version\t6.0.1.0",
      "meta\tformat-version\t39.00",
      $"meta\tfilesystem-uid\t{filesystemUid}",
      "meta\tcapture-state\tunmounted-clean",
      $"nsd\tnsd1\t1\t10485760\t512\t{ShaA}\traw/nsd1.img",
      $"nsd\tnsd2\t2\t10485760\t512\t{ShaB}\traw/nsd2.img",
      $"artifact\tmmfsckx\t{ShaA}\toracle/mmfsckx.txt",
      $"artifact\ttsdbfs\t{ShaA}\toracle/tsdbfs-inode-64.txt",
      $"artifact\tmmgetlocation\t{ShaA}\toracle/mmgetlocation-file.txt",
      $"artifact\tmmlsdisk\t{ShaA}\toracle/mmlsdisk.txt",
      $"artifact\tmmlsnsd\t{ShaA}\toracle/mmlsnsd.txt",
    };
    if (includeMmfileid)
      lines.Add($"artifact\tmmfileid\t{ShaA}\toracle/mmfileid.txt");
    return string.Join("\n", lines);
  }
}
