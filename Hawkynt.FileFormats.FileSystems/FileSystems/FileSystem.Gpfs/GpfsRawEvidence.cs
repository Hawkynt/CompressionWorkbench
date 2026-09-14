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
/// Results of comparing an independent raw parser with the IBM tools for one
/// corpus. These are the Stage-1 promotion requirements, kept explicit so no
/// single successful inode lookup can accidentally promote the whole format.
/// </summary>
internal sealed record GpfsReadOnlyVerification(
  string CorpusId,
  bool DescriptorDiskMapping,
  bool InodeLocation,
  bool InodeChecksum,
  bool DirectAddressing,
  bool IndirectAddressing,
  bool InodeAllocationMap,
  bool DirectoryLookup,
  bool SparseAndInlineData,
  bool MalformedMetadataFailsClosed) {

  internal bool IsComplete
    => DescriptorDiskMapping
       && InodeLocation
       && InodeChecksum
       && DirectAddressing
       && IndirectAddressing
       && InodeAllocationMap
       && DirectoryLookup
       && SparseAndInlineData
       && MalformedMetadataFailsClosed;
}

internal readonly record struct GpfsPromotionDecision(bool IsSatisfied, string Reason) {
  internal static GpfsPromotionDecision Pass(string reason) => new(true, reason);
  internal static GpfsPromotionDecision Fail(string reason) => new(false, reason);
}

internal static class GpfsReadOnlyPromotionGate {
  internal static GpfsPromotionDecision Evaluate(
    IReadOnlyList<GpfsEvidenceManifest> captures,
    IReadOnlyList<GpfsReadOnlyVerification> verifications) {
    ArgumentNullException.ThrowIfNull(captures);
    ArgumentNullException.ThrowIfNull(verifications);

    if (captures.Count < 2)
      return GpfsPromotionDecision.Fail("At least two independent multi-NSD corpora are required.");

    var corpusIds = new HashSet<string>(StringComparer.Ordinal);
    foreach (var capture in captures) {
      var complete = capture.ValidateCaptureCompleteness();
      if (!complete.IsSatisfied)
        return complete;

      var corpusId = capture.Metadata["corpus-id"];
      if (!corpusIds.Add(corpusId))
        return GpfsPromotionDecision.Fail("The promotion set must contain independent corpus IDs.");

      var verification = verifications.SingleOrDefault(x => string.Equals(x.CorpusId, corpusId, StringComparison.Ordinal));
      if (verification is null)
        return GpfsPromotionDecision.Fail($"Corpus '{corpusId}' has no raw-parser verification result.");
      if (!verification.IsComplete)
        return GpfsPromotionDecision.Fail($"Corpus '{corpusId}' has not satisfied every Stage-1 raw-reader check.");
    }

    return GpfsPromotionDecision.Pass("Two independent multi-NSD corpora agree with the raw parser on every Stage-1 requirement.");
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
