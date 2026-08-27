#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Refs;

/// <summary>
/// Relocates live ReFS metadata. Ordinary MSB+ pages are repointed through the
/// metadata graph; CHKP pages are repointed through all fixed SUPB copies. Each
/// structure is allocated through the tier that actually owns it: Medium for
/// ordinary metadata, Container for Container-Table pages, and Small for the
/// Small Allocator / checkpoint bootstrap structures.
/// </summary>
internal sealed class RefsMetadataMover : IFilesystemMetadataMover {
  private readonly int _clusterSize;
  private readonly int _pageSize;
  private readonly HashSet<string> _relocatable;

  public RefsMetadataMover(Stream image) {
    var metadata = RefsMetadataReader.Open(image);
    var graph = new RefsMetadataGraph(image, metadata);
    var bootstrap = RefsBootstrapState.Open(image);
    this._clusterSize = metadata.ClusterSize;
    this._pageSize = metadata.PageSize;
    this._relocatable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    var allocators = new Dictionary<RefsAllocatorTier, RefsAllocatorWriter>();
    RefsAllocatorWriter Allocator(RefsAllocatorTier tier) {
      if (!allocators.TryGetValue(tier, out var writer))
        allocators[tier] = writer = new RefsAllocatorWriter(metadata, graph, tier);
      return writer;
    }

    foreach (var page in graph.Pages) {
      if (!page.IsContiguous) continue;
      var allocator = Allocator(TierFor(page));
      if (!page.PhysicalSlots.All(allocator.CoversPhysical)) continue;
      if (!allocator.AreAllocated(page.PhysicalSlots, expectedAllocated: true)) continue;
      this._relocatable.Add(RefsMetadataNames.Page(page.PhysicalHead));
    }

    var small = Allocator(RefsAllocatorTier.Small);
    var pageClusters = this._pageSize / this._clusterSize;
    foreach (var checkpoint in bootstrap.CheckpointLcns) {
      var slots = new ulong[pageClusters];
      for (var i = 0; i < slots.Length; ++i) slots[i] = checked(checkpoint + (ulong)i);
      if (slots.All(small.CoversPhysical) && small.AreAllocated(slots, expectedAllocated: true))
        this._relocatable.Add(RefsMetadataNames.Checkpoint(checkpoint));
    }
  }

  public IReadOnlySet<string> RelocatableMetadata => this._relocatable;

  public static bool TryGetTier(
      RefsMetadataGraph graph,
      string metadataName,
      out RefsAllocatorTier tier,
      out ulong physicalHead) {
    if (RefsMetadataNames.TryParseCheckpoint(metadataName, out physicalHead)) {
      tier = RefsAllocatorTier.Small;
      return true;
    }
    if (RefsMetadataNames.TryParsePage(metadataName, out physicalHead)
        && graph.TryGetPage(physicalHead, out var page)) {
      tier = TierFor(page);
      return true;
    }
    tier = default;
    physicalHead = 0;
    return false;
  }

  public void PrepareMetadataMove(
      Stream image,
      string metadataName,
      long oldOffset,
      long newOffset,
      long length) {
    this.ValidateMove(metadataName, oldOffset, newOffset, length, out var oldHead, out var newHead, out var checkpoint);
    if (oldHead == newHead) return;

    var metadata = RefsMetadataReader.Open(image);
    var graph = new RefsMetadataGraph(image, metadata);
    RefsAllocatorTier tier;
    IReadOnlyList<ulong> oldSlots;
    if (!checkpoint) {
      if (!graph.TryGetPage(oldHead, out var page) || !page.IsContiguous)
        throw new InvalidOperationException($"ReFS metadata page at 0x{oldHead:X} is no longer a relocatable live page.");
      tier = TierFor(page);
      oldSlots = page.PhysicalSlots;
    } else {
      var bootstrap = RefsBootstrapState.Open(image);
      if (!bootstrap.CheckpointLcns.Contains(oldHead))
        throw new InvalidOperationException($"ReFS checkpoint at 0x{oldHead:X} is no longer referenced by SUPB.");
      tier = RefsAllocatorTier.Small;
      oldSlots = ConsecutiveSlots(oldHead, this._pageSize / this._clusterSize);
    }

    var targets = ConsecutiveSlots(newHead, this._pageSize / this._clusterSize);
    var allocator = new RefsAllocatorWriter(metadata, graph, tier);
    foreach (var target in targets) {
      if (oldSlots.Contains(target)) continue;
      if (!allocator.CoversPhysical(target) || !allocator.AreAllocated([target], expectedAllocated: false))
        throw new InvalidOperationException(
          $"ReFS {tier} Allocator does not expose target LCN 0x{target:X} as free for '{metadataName}'.");
    }

    allocator.SetAllocated(targets, allocated: true);
    image.Flush();
  }

  public void UpdateMetadataAfterMove(
      Stream image,
      string metadataName,
      long oldOffset,
      long newOffset,
      long length,
      IReadOnlyList<(long Offset, long Length)>? liveRanges = null) {
    this.ValidateMove(metadataName, oldOffset, newOffset, length, out var oldHead, out var newHead, out var checkpoint);
    if (oldHead == newHead) return;

    RefsAllocatorTier tier;
    if (checkpoint) {
      tier = RefsAllocatorTier.Small;
      this.RelocateCheckpoint(image, oldHead, newHead);
    } else {
      var metadata = RefsMetadataReader.Open(image);
      var graph = new RefsMetadataGraph(image, metadata);
      if (!graph.TryGetPage(oldHead, out var page))
        throw new InvalidDataException($"ReFS metadata page 0x{oldHead:X} disappeared before relocation commit.");
      tier = TierFor(page);
      graph.RelocatePage(oldHead, newHead);
      image.Flush();
      this.NormalizeMovedPageSelfAddress(image, newHead);
    }

    this.ReleaseOldAllocation(image, oldHead, tier, liveRanges);
  }

  private void NormalizeMovedPageSelfAddress(Stream image, ulong newHead) {
    var metadata = RefsMetadataReader.Open(image);
    var graph = new RefsMetadataGraph(image, metadata);
    if (!graph.TryGetPage(newHead, out var info))
      throw new InvalidDataException($"Relocated ReFS page 0x{newHead:X} is not reachable after repointing.");

    var page = graph.ReadPage(newHead);
    var slots = graph.GetPageSlots(newHead);
    for (var i = 0; i < 4; ++i) {
      ulong selfLcn = 0;
      if (i < slots.Count) {
        if (info.VirtualAddresses) {
          if (!metadata.TryPhysicalToVirtualLcn(slots[i], out selfLcn))
            throw new InvalidDataException(
              $"Relocated ReFS page slot PLCN 0x{slots[i]:X} has no VLCN for its self descriptor.");
        } else {
          selfLcn = slots[i];
        }
      }
      BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x20 + i * 8, 8), selfLcn);
    }

    graph.WritePage(newHead, page);
    graph.RefreshChecksumPaths([newHead]);
    image.Flush();
  }

  private void RelocateCheckpoint(Stream image, ulong oldHead, ulong newHead) {
    var bootstrap = RefsBootstrapState.Open(image);
    var checkpoint = bootstrap.ReadCheckpoint(newHead);
    if (checkpoint.Length < 0x94 || !checkpoint.AsSpan(0, 4).SequenceEqual("CHKP"u8))
      throw new InvalidDataException("Relocated ReFS checkpoint bytes do not contain CHKP.");

    BinaryPrimitives.WriteUInt64LittleEndian(checkpoint.AsSpan(0x20, 8), newHead);
    for (var i = 1; i < 4; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(checkpoint.AsSpan(0x20 + i * 8, 8), 0UL);

    var selfOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(checkpoint.AsSpan(0x58, 4)));
    var selfLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(checkpoint.AsSpan(0x5C, 4)));
    RefsChecksum.RefreshSelfChecksum(checkpoint, this._clusterSize, selfOffset, selfLength);
    bootstrap.WriteCheckpoint(newHead, checkpoint);
    image.Flush();
    bootstrap.RepointCheckpoint(oldHead, newHead);
  }

  private void ReleaseOldAllocation(
      Stream image,
      ulong oldHead,
      RefsAllocatorTier tier,
      IReadOnlyList<(long Offset, long Length)>? liveRanges) {
    var fresh = RefsMetadataReader.Open(image);
    var freshGraph = new RefsMetadataGraph(image, fresh);
    var count = this._pageSize / this._clusterSize;
    var release = new List<ulong>(count);
    for (var i = 0; i < count; ++i) {
      var physical = checked(oldHead + (ulong)i);
      var byteOffset = checked((long)physical * this._clusterSize);
      if (OverlapsAny(byteOffset, this._clusterSize, liveRanges)) continue;
      release.Add(physical);
    }
    if (release.Count > 0)
      new RefsAllocatorWriter(fresh, freshGraph, tier).SetAllocated(release, allocated: false);
    image.Flush();
  }

  private void ValidateMove(
      string metadataName,
      long oldOffset,
      long newOffset,
      long length,
      out ulong oldHead,
      out ulong newHead,
      out bool checkpoint) {
    checkpoint = RefsMetadataNames.TryParseCheckpoint(metadataName, out _);
    var page = RefsMetadataNames.TryParsePage(metadataName, out _);
    if (!this._relocatable.Contains(metadataName) || (!page && !checkpoint))
      throw new NotSupportedException($"'{metadataName}' is not relocatable ReFS metadata.");
    if (length != this._pageSize)
      throw new InvalidOperationException($"ReFS metadata pages must move as exactly {this._pageSize:N0} bytes.");
    if (oldOffset < 0 || newOffset < 0 || oldOffset % this._clusterSize != 0 || newOffset % this._clusterSize != 0)
      throw new InvalidOperationException("ReFS metadata relocation must be cluster aligned.");
    oldHead = checked((ulong)(oldOffset / this._clusterSize));
    newHead = checked((ulong)(newOffset / this._clusterSize));
  }

  internal static RefsAllocatorTier TierFor(RefsMetadataPageInfo page)
    => page.TableId switch {
      0x0B or 0x0C => RefsAllocatorTier.Container,
      0x22 => RefsAllocatorTier.Small,
      _ => RefsAllocatorTier.Medium,
    };

  private static ulong[] ConsecutiveSlots(ulong head, int count) {
    var result = new ulong[count];
    for (var i = 0; i < count; ++i) result[i] = checked(head + (ulong)i);
    return result;
  }

  private static bool OverlapsAny(
      long offset,
      long length,
      IReadOnlyList<(long Offset, long Length)>? ranges) {
    if (ranges == null) return false;
    var end = checked(offset + length);
    foreach (var (otherOffset, otherLength) in ranges) {
      if (otherLength <= 0) continue;
      var otherEnd = checked(otherOffset + otherLength);
      if (offset < otherEnd && otherOffset < end) return true;
    }
    return false;
  }
}
