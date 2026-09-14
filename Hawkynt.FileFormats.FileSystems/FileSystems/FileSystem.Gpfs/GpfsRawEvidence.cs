#pragma warning disable CS1591
using System.Numerics;

namespace FileSystem.Gpfs;

/// <summary>
/// Clean-room manifest for one quiesced raw GPFS capture. It deliberately stores
/// evidence, not inferred on-disk structure: a capture is useful only when the
/// raw NSDs and IBM oracle outputs can be tied together by hashes.
/// </summary>
internal sealed record GpfsEvidenceManifest(
  IReadOnlyDictionary<string, string> Metadata,
  IReadOnlyList<GpfsEvidenceNsd> Nsds,
  IReadOnlyList<GpfsEvidenceArtifact> Artifacts) {

  private static readonly string[] RequiredMetadata = [
    "corpus-id",
    "capture-id",
    "operation",
    "storage-scale-version",
    "format-version",
    "filesystem-uid",
    "capture-state",
  ];

  private static readonly string[] RequiredOracleKinds = [
    "mmfsckx",
    "tsdbfs",
    "mmfileid",
    "mmgetlocation",
    "mmlsdisk",
    "mmlsnsd",
  ];

  internal static GpfsEvidenceManifest Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);

    var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
    var nsds = new List<GpfsEvidenceNsd>();
    var artifacts = new List<GpfsEvidenceArtifact>();

    foreach (var raw in text.ReplaceLineEndings("\n").Split('\n')) {
      var line = raw.Trim();
      if (line.Length == 0 || line.StartsWith('#'))
        continue;

      var cells = line.Split('\t');
      switch (cells[0]) {
        case "meta" when cells.Length == 3:
          if (!metadata.TryAdd(cells[1], cells[2]))
            throw new InvalidDataException($"Duplicate GPFS evidence metadata key '{cells[1]}'.");
          break;

        case "nsd" when cells.Length == 7:
          if (!int.TryParse(cells[2], out var diskId)
              || !long.TryParse(cells[3], out var deviceSize)
              || !int.TryParse(cells[4], out var sectorBytes))
            throw new InvalidDataException("Malformed GPFS NSD evidence row.");
          nsds.Add(new GpfsEvidenceNsd(cells[1], diskId, deviceSize, sectorBytes, cells[5], cells[6]));
          break;

        case "artifact" when cells.Length == 4:
          artifacts.Add(new GpfsEvidenceArtifact(cells[1], cells[2], cells[3]));
          break;

        default:
          throw new InvalidDataException($"Unrecognized GPFS evidence manifest row: '{line}'.");
      }
    }

    return new GpfsEvidenceManifest(metadata, nsds, artifacts);
  }

  internal GpfsPromotionDecision ValidateCaptureCompleteness() {
    foreach (var key in RequiredMetadata)
      if (!Metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        return GpfsPromotionDecision.Fail($"Capture metadata '{key}' is missing.");

    if (!string.Equals(Metadata["capture-state"], "unmounted-clean", StringComparison.Ordinal))
      return GpfsPromotionDecision.Fail("Capture was not recorded as an unmounted clean filesystem.");

    if (Nsds.Count < 2)
      return GpfsPromotionDecision.Fail("A GPFS promotion corpus must contain every NSD of a multi-NSD filesystem.");

    if (Nsds.Select(static x => x.Name).Distinct(StringComparer.Ordinal).Count() != Nsds.Count)
      return GpfsPromotionDecision.Fail("Capture contains duplicate NSD names.");
    if (Nsds.Select(static x => x.DiskId).Distinct().Count() != Nsds.Count)
      return GpfsPromotionDecision.Fail("Capture contains duplicate GPFS disk IDs.");

    foreach (var nsd in Nsds) {
      if (nsd.DiskId < 0 || nsd.DeviceSize <= 0 || nsd.SectorBytes <= 0)
        return GpfsPromotionDecision.Fail($"NSD '{nsd.Name}' has invalid geometry.");
      if (!IsSha256(nsd.Sha256))
        return GpfsPromotionDecision.Fail($"NSD '{nsd.Name}' has no valid SHA-256 digest.");
      if (string.IsNullOrWhiteSpace(nsd.ImagePath))
        return GpfsPromotionDecision.Fail($"NSD '{nsd.Name}' has no raw-image path.");
    }

    foreach (var artifact in Artifacts)
      if (!IsSha256(artifact.Sha256) || string.IsNullOrWhiteSpace(artifact.Path))
        return GpfsPromotionDecision.Fail($"Oracle artifact '{artifact.Kind}' is not hash-addressed.");

    var artifactKinds = Artifacts.Select(static x => x.Kind).ToHashSet(StringComparer.Ordinal);
    foreach (var kind in RequiredOracleKinds)
      if (!artifactKinds.Contains(kind))
        return GpfsPromotionDecision.Fail($"Required IBM oracle output '{kind}' is missing.");

    return GpfsPromotionDecision.Pass("Capture contains a complete hash-addressed multi-NSD evidence set.");
  }

  private static bool IsSha256(string text)
    => text.Length == 64 && text.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

internal sealed record GpfsEvidenceNsd(
  string Name,
  int DiskId,
  long DeviceSize,
  int SectorBytes,
  string Sha256,
  string ImagePath);

internal sealed record GpfsEvidenceArtifact(string Kind, string Sha256, string Path);

/// <summary>
/// A corpus is the complete controlled transition series from one independently
/// formatted filesystem. One attractive capture is not enough to establish a
/// representation rule.
/// </summary>
internal sealed record GpfsEvidenceCorpus(IReadOnlyList<GpfsEvidenceManifest> Captures) {
  internal static readonly string[] RequiredCaptureIds = [
    "000-anchor",
    "010-empty",
    "020-tiny",
    "030-one-subblock",
    "040-one-block",
    "050-block-plus-one",
    "055-rebalanced",
    "060-indirect",
    "070-sparse",
    "080-directory",
    "090-links",
    "100-xattr-acl",
    "105-replicated",
    "110-delete",
  ];

  internal GpfsPromotionDecision ValidateCompleteness() {
    if (Captures.Count == 0)
      return GpfsPromotionDecision.Fail("GPFS evidence corpus is empty.");

    foreach (var capture in Captures) {
      var decision = capture.ValidateCaptureCompleteness();
      if (!decision.IsSatisfied)
        return decision;
    }

    var first = Captures[0];
    var corpusId = first.Metadata["corpus-id"];
    var filesystemUid = first.Metadata["filesystem-uid"];
    var formatVersion = first.Metadata["format-version"];
    var storageScaleVersion = first.Metadata["storage-scale-version"];
    var nsdIdentity = NsdIdentity(first);
    var seenCaptureIds = new HashSet<string>(StringComparer.Ordinal);

    foreach (var capture in Captures) {
      if (!string.Equals(capture.Metadata["corpus-id"], corpusId, StringComparison.Ordinal))
        return GpfsPromotionDecision.Fail("A corpus contains captures from different corpus IDs.");
      if (!string.Equals(capture.Metadata["filesystem-uid"], filesystemUid, StringComparison.Ordinal))
        return GpfsPromotionDecision.Fail("A corpus crosses filesystem UIDs.");
      if (!string.Equals(capture.Metadata["format-version"], formatVersion, StringComparison.Ordinal)
          || !string.Equals(capture.Metadata["storage-scale-version"], storageScaleVersion, StringComparison.Ordinal))
        return GpfsPromotionDecision.Fail("A corpus crosses Storage Scale or filesystem format versions.");
      if (!string.Equals(NsdIdentity(capture), nsdIdentity, StringComparison.Ordinal))
        return GpfsPromotionDecision.Fail("A corpus changes its NSD/disk-ID/geometry topology between captures.");
      if (!seenCaptureIds.Add(capture.Metadata["capture-id"]))
        return GpfsPromotionDecision.Fail($"Corpus '{corpusId}' contains duplicate capture IDs.");
    }

    foreach (var required in RequiredCaptureIds)
      if (!seenCaptureIds.Contains(required))
        return GpfsPromotionDecision.Fail($"Corpus '{corpusId}' is missing controlled capture '{required}'.");

    return GpfsPromotionDecision.Pass($"Corpus '{corpusId}' contains the complete controlled multi-NSD transition series.");
  }

  internal string CorpusId => Captures.Count == 0 ? string.Empty : Captures[0].Metadata.GetValueOrDefault("corpus-id", string.Empty);
  internal string FilesystemUid => Captures.Count == 0 ? string.Empty : Captures[0].Metadata.GetValueOrDefault("filesystem-uid", string.Empty);

  private static string NsdIdentity(GpfsEvidenceManifest capture)
    => string.Join("|", capture.Nsds
      .OrderBy(static x => x.DiskId)
      .Select(static x => $"{x.Name}:{x.DiskId}:{x.DeviceSize}:{x.SectorBytes}"));
}

/// <summary>
/// Results of comparing an independent raw parser with the IBM tools for one
/// complete corpus. The individual flags mirror the Stage-1 promotion evidence
/// so representation guesses cannot be hidden behind a single boolean.
/// </summary>
internal sealed record GpfsReadOnlyVerification(
  string CorpusId,
  bool DescriptorDiskMapping,
  bool InodeRecordLayout,
  bool InodeChecksumCoverage,
  bool DiskAddressPacking,
  bool DirectAddressing,
  bool IndirectAddressing,
  bool InodeAllocationMap,
  bool BlockAllocationMap,
  bool DirectoryLookup,
  bool SparseAndInlineData,
  bool MalformedMetadataFailsClosed) {

  internal bool IsComplete
    => DescriptorDiskMapping
       && InodeRecordLayout
       && InodeChecksumCoverage
       && DiskAddressPacking
       && DirectAddressing
       && IndirectAddressing
       && InodeAllocationMap
       && BlockAllocationMap
       && DirectoryLookup
       && SparseAndInlineData
       && MalformedMetadataFailsClosed;
}

internal sealed record GpfsMutationVerification(
  string CorpusId,
  bool RemountSucceeded,
  bool MmfsckxClean,
  bool NamespaceVerified,
  bool FileDataVerified,
  bool LinksAclXattrSparseAndReplicasVerified) {
  internal bool IsComplete
    => RemountSucceeded
       && MmfsckxClean
       && NamespaceVerified
       && FileDataVerified
       && LinksAclXattrSparseAndReplicasVerified;
}

internal readonly record struct GpfsPromotionDecision(bool IsSatisfied, string Reason) {
  internal static GpfsPromotionDecision Pass(string reason) => new(true, reason);
  internal static GpfsPromotionDecision Fail(string reason) => new(false, reason);
}

internal static class GpfsReadOnlyPromotionGate {
  internal static GpfsPromotionDecision Evaluate(
    IReadOnlyList<GpfsEvidenceCorpus> corpora,
    IReadOnlyList<GpfsReadOnlyVerification> verifications) {
    ArgumentNullException.ThrowIfNull(corpora);
    ArgumentNullException.ThrowIfNull(verifications);

    if (corpora.Count < 2)
      return GpfsPromotionDecision.Fail("At least two independent complete multi-NSD corpora are required.");

    var corpusIds = new HashSet<string>(StringComparer.Ordinal);
    var filesystemUids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var corpus in corpora) {
      var complete = corpus.ValidateCompleteness();
      if (!complete.IsSatisfied)
        return complete;
      if (!corpusIds.Add(corpus.CorpusId))
        return GpfsPromotionDecision.Fail("The promotion set contains a duplicate corpus ID.");
      if (!filesystemUids.Add(corpus.FilesystemUid))
        return GpfsPromotionDecision.Fail("Independent GPFS corpora must come from independently formatted filesystem UIDs.");

      var matches = verifications.Where(x => string.Equals(x.CorpusId, corpus.CorpusId, StringComparison.Ordinal)).ToArray();
      if (matches.Length != 1)
        return GpfsPromotionDecision.Fail($"Corpus '{corpus.CorpusId}' must have exactly one raw-parser verification result.");
      if (!matches[0].IsComplete)
        return GpfsPromotionDecision.Fail($"Corpus '{corpus.CorpusId}' has not satisfied every Stage-1 raw-reader check.");
    }

    return GpfsPromotionDecision.Pass("Two independent complete multi-NSD corpora agree with the raw parser on every Stage-1 requirement.");
  }
}

internal static class GpfsMutationPromotionGate {
  internal static GpfsPromotionDecision Evaluate(GpfsMutationVerification verification) {
    ArgumentNullException.ThrowIfNull(verification);
    return verification.IsComplete
      ? GpfsPromotionDecision.Pass("Mutated evidence remounted, passed mmfsckx, and preserved all verified semantics.")
      : GpfsPromotionDecision.Fail("R/W and maintenance remain disabled until remount, mmfsckx, namespace, data, and metadata verification all pass.");
  }
}

internal readonly record struct GpfsChangedRange(int Offset, int Length);
internal readonly record struct GpfsBitTransition(int ByteOffset, byte XorMask) {
  internal int LsbFirstBitIndex => BitOperations.TrailingZeroCount((uint)XorMask);
  internal int MsbFirstBitIndex => 7 - LsbFirstBitIndex;
}

/// <summary>Byte/bit differencing primitives used by paired clean captures.</summary>
internal static class GpfsRawDiff {
  internal static IReadOnlyList<GpfsChangedRange> FindChangedRanges(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after) {
    if (before.Length != after.Length)
      throw new ArgumentException("Paired GPFS evidence buffers must have equal length.");

    var result = new List<GpfsChangedRange>();
    for (var i = 0; i < before.Length;) {
      if (before[i] == after[i]) {
        ++i;
        continue;
      }

      var start = i++;
      while (i < before.Length && before[i] != after[i])
        ++i;
      result.Add(new GpfsChangedRange(start, i - start));
    }

    return result;
  }

  internal static bool TryFindSingleBitTransition(
    ReadOnlySpan<byte> before,
    ReadOnlySpan<byte> after,
    out GpfsBitTransition transition) {
    if (before.Length != after.Length)
      throw new ArgumentException("Paired GPFS evidence buffers must have equal length.");

    transition = default;
    var found = false;
    for (var i = 0; i < before.Length; ++i) {
      var xor = (byte)(before[i] ^ after[i]);
      if (xor == 0)
        continue;
      if (found || !BitOperations.IsPow2((uint)xor))
        return false;
      transition = new GpfsBitTransition(i, xor);
      found = true;
    }

    return found;
  }
}
