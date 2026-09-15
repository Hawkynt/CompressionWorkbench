#pragma warning disable CS1591
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace FileSystem.OneFs;

/// <summary>Per-member fingerprint for one empirically inspected OneFS-sized block.</summary>
public sealed record OneFsMemberBlockFingerprint(
  int DeviceIndex,
  long BlockIndex,
  string Sha256
);

/// <summary>
/// Byte-stability summary for the same candidate block offset across multiple
/// raw device images.
/// </summary>
/// <remarks>
/// This result is intentionally semantic-free. Matching or highly stable bytes
/// do not prove that an offset contains a OneFS superblock, tree node, bitmap,
/// or any other structure.
/// </remarks>
public sealed record OneFsSameOffsetCorrelation(
  long BlockIndex,
  IReadOnlyList<OneFsMemberBlockFingerprint> Members,
  int StableByteCount,
  int VariantByteCount
) {
  public bool AllBytesStable => this.VariantByteCount == 0;
  public bool AllFingerprintsEqual
    => this.Members.Count > 0
       && this.Members.Select(static member => member.Sha256).Distinct(StringComparer.Ordinal).Take(2).Count() == 1;
}

/// <summary>One exact-copy group among explicitly supplied candidate block offsets.</summary>
public sealed record OneFsExactBlockCopyGroup(
  int DeviceIndex,
  string Sha256,
  IReadOnlyList<long> BlockIndices
);

/// <summary>
/// Clean-room forensic correlation helpers for candidate OneFS raw members.
/// </summary>
/// <remarks>
/// The correlator reads only explicitly requested complete 8 KiB blocks through
/// <see cref="OneFsDeviceSet.ReadBlock"/>. It does not scan media implicitly and
/// deliberately assigns no filesystem meaning to matching bytes. Its purpose is
/// to support evidence gathering against genuine member images before raw
/// structure offsets are promoted into parser constants.
/// </remarks>
public sealed class OneFsMediaCorrelator {
  private readonly OneFsDeviceSet _devices;

  public OneFsMediaCorrelator(OneFsDeviceSet devices)
    => this._devices = devices ?? throw new ArgumentNullException(nameof(devices));

  /// <summary>
  /// Compares the same complete block index across every member that contains it.
  /// </summary>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockIndex"/> is negative or absent from every supplied member.</exception>
  public OneFsSameOffsetCorrelation CompareSameOffset(long blockIndex) {
    if (blockIndex < 0)
      throw new ArgumentOutOfRangeException(nameof(blockIndex));

    var eligible = this._devices.Devices
      .Where(device => blockIndex < device.CompleteBlockCount)
      .Select(static device => device.Index)
      .ToArray();
    if (eligible.Length == 0)
      throw new ArgumentOutOfRangeException(nameof(blockIndex), "No supplied member contains that complete block index.");

    var fingerprints = new OneFsMemberBlockFingerprint[eligible.Length];
    var reference = new byte[OneFsReader.PhysicalBlockSize];
    var current = new byte[OneFsReader.PhysicalBlockSize];
    var stable = new bool[OneFsReader.PhysicalBlockSize];
    Array.Fill(stable, true);

    this._devices.ReadBlock(eligible[0], blockIndex, reference);
    fingerprints[0] = new OneFsMemberBlockFingerprint(eligible[0], blockIndex, Fingerprint(reference));

    for (var memberIndex = 1; memberIndex < eligible.Length; ++memberIndex) {
      var deviceIndex = eligible[memberIndex];
      this._devices.ReadBlock(deviceIndex, blockIndex, current);
      fingerprints[memberIndex] = new OneFsMemberBlockFingerprint(deviceIndex, blockIndex, Fingerprint(current));

      for (var byteIndex = 0; byteIndex < stable.Length; ++byteIndex)
        stable[byteIndex] &= current[byteIndex] == reference[byteIndex];
    }

    var stableByteCount = stable.Count(static value => value);
    return new OneFsSameOffsetCorrelation(
      blockIndex,
      Array.AsReadOnly(fingerprints),
      stableByteCount,
      OneFsReader.PhysicalBlockSize - stableByteCount);
  }

  /// <summary>
  /// Finds byte-identical copies among explicit candidate block indices on one member.
  /// </summary>
  /// <remarks>
  /// Only groups containing at least two candidate offsets are returned. The
  /// method intentionally requires the caller to provide candidate offsets so it
  /// cannot accidentally turn a multi-terabyte image into an unbounded scan.
  /// SHA-256 is used for bucketing and every reported group is then byte-compared,
  /// so a hash collision cannot produce a false exact-copy claim.
  /// </remarks>
  public IReadOnlyList<OneFsExactBlockCopyGroup> FindExactCopies(int deviceIndex, IEnumerable<long> candidateBlockIndices) {
    ArgumentNullException.ThrowIfNull(candidateBlockIndices);
    if ((uint)deviceIndex >= (uint)this._devices.Devices.Count)
      throw new ArgumentOutOfRangeException(nameof(deviceIndex));

    var candidates = candidateBlockIndices.Distinct().ToArray();
    var blocks = new Dictionary<long, byte[]>(candidates.Length);
    var byFingerprint = new Dictionary<string, List<long>>(StringComparer.Ordinal);

    foreach (var blockIndex in candidates) {
      var block = new byte[OneFsReader.PhysicalBlockSize];
      this._devices.ReadBlock(deviceIndex, blockIndex, block);
      blocks.Add(blockIndex, block);

      var fingerprint = Fingerprint(block);
      if (!byFingerprint.TryGetValue(fingerprint, out var indices))
        byFingerprint.Add(fingerprint, indices = []);
      indices.Add(blockIndex);
    }

    var result = new List<OneFsExactBlockCopyGroup>();
    foreach (var (fingerprint, indices) in byFingerprint.Where(static pair => pair.Value.Count > 1)) {
      var pending = new List<long>(indices);
      while (pending.Count > 1) {
        var referenceIndex = pending[0];
        pending.RemoveAt(0);
        var exact = new List<long> { referenceIndex };
        for (var index = pending.Count - 1; index >= 0; --index) {
          var candidateIndex = pending[index];
          if (!blocks[referenceIndex].AsSpan().SequenceEqual(blocks[candidateIndex]))
            continue;
          exact.Add(candidateIndex);
          pending.RemoveAt(index);
        }

        if (exact.Count > 1) {
          exact.Sort();
          result.Add(new OneFsExactBlockCopyGroup(
            deviceIndex,
            fingerprint,
            Array.AsReadOnly(exact.ToArray())));
        }
      }
    }

    result.Sort(static (left, right) => left.BlockIndices[0].CompareTo(right.BlockIndices[0]));
    return new ReadOnlyCollection<OneFsExactBlockCopyGroup>(result);
  }

  private static string Fingerprint(ReadOnlySpan<byte> block)
    => Convert.ToHexString(SHA256.HashData(block));
}
