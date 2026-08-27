#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// ReFS bootstrap topology outside the normal metadata B+ graph. VBR and SUPB
/// locations are format-fixed; SUPB points at the two movable checkpoint pages.
/// </summary>
internal sealed class RefsBootstrapState {
  private readonly Stream _image;

  private RefsBootstrapState(
      Stream image,
      RefsMetadataReader metadata,
      IReadOnlyList<ulong> superblockLcns,
      ulong winningSuperblockLcn,
      ulong winningGeneration,
      IReadOnlyList<ulong> checkpointLcns) {
    this._image = image;
    this.Metadata = metadata;
    this.SuperblockLcns = superblockLcns;
    this.WinningSuperblockLcn = winningSuperblockLcn;
    this.WinningGeneration = winningGeneration;
    this.CheckpointLcns = checkpointLcns;
  }

  public RefsMetadataReader Metadata { get; }
  public IReadOnlyList<ulong> SuperblockLcns { get; }
  public ulong WinningSuperblockLcn { get; }
  public ulong WinningGeneration { get; }
  public IReadOnlyList<ulong> CheckpointLcns { get; }

  public static RefsBootstrapState Open(Stream image) {
    var metadata = RefsMetadataReader.Open(image);
    var totalClusters = metadata.Header.TotalClusters;
    var candidates = new[] { 0x1EUL, totalClusters - 2, totalClusters - 3 }.Distinct().ToArray();

    ulong winning = 0;
    ulong generation = 0;
    byte[]? winningBytes = null;
    var valid = new List<ulong>();
    foreach (var lcn in candidates) {
      var bytes = ReadCluster(image, lcn, metadata.ClusterSize);
      if (bytes == null || !bytes.AsSpan(0, 4).SequenceEqual("SUPB"u8)) continue;
      valid.Add(lcn);
      var current = ReadU64(bytes, 0x68);
      if (winningBytes == null || current > generation) {
        winning = lcn;
        generation = current;
        winningBytes = bytes;
      }
    }
    if (winningBytes == null)
      throw new InvalidDataException("No valid ReFS superblock copy is available.");

    var refsOffset = checked((int)ReadU32(winningBytes, 0x70));
    var refsCount = checked((int)ReadU32(winningBytes, 0x74));
    if (refsCount is < 1 or > 8 || refsOffset < 0 || refsOffset + refsCount * 8 > winningBytes.Length)
      throw new InvalidDataException("ReFS superblock checkpoint reference list is malformed.");

    var checkpoints = new List<ulong>(refsCount);
    for (var i = 0; i < refsCount; ++i) {
      var lcn = ReadU64(winningBytes, refsOffset + i * 8);
      if (lcn is not (0 or ulong.MaxValue)) checkpoints.Add(lcn);
    }

    return new RefsBootstrapState(image, metadata, candidates, winning, generation, checkpoints.Distinct().ToArray());
  }

  public byte[] ReadSuperblock(ulong lcn)
    => ReadCluster(this._image, lcn, this.Metadata.ClusterSize)
      ?? throw new InvalidDataException($"ReFS SUPB at 0x{lcn:X} lies outside the image.");

  public byte[] ReadCheckpoint(ulong lcn) {
    var bytes = new byte[this.Metadata.PageSize];
    var count = this.Metadata.PageSize / this.Metadata.ClusterSize;
    for (var i = 0; i < count; ++i) {
      var offset = checked((long)(lcn + (ulong)i) * this.Metadata.ClusterSize);
      if (offset < 0 || offset + this.Metadata.ClusterSize > this._image.Length)
        throw new InvalidDataException("ReFS checkpoint lies outside the image.");
      this._image.Position = offset;
      this._image.ReadExactly(bytes.AsSpan(i * this.Metadata.ClusterSize, this.Metadata.ClusterSize));
    }
    return bytes;
  }

  public void WriteCheckpoint(ulong lcn, ReadOnlySpan<byte> bytes) {
    if (bytes.Length != this.Metadata.PageSize)
      throw new ArgumentException("ReFS checkpoint write must contain one metadata page.", nameof(bytes));
    var count = this.Metadata.PageSize / this.Metadata.ClusterSize;
    for (var i = 0; i < count; ++i) {
      this._image.Position = checked((long)(lcn + (ulong)i) * this.Metadata.ClusterSize);
      this._image.Write(bytes.Slice(i * this.Metadata.ClusterSize, this.Metadata.ClusterSize));
    }
  }

  public void RepointCheckpoint(ulong oldLcn, ulong newLcn) {
    var newGeneration = checked(this.WinningGeneration + 1);
    var wrote = 0;
    foreach (var supbLcn in this.SuperblockLcns) {
      var supb = ReadCluster(this._image, supbLcn, this.Metadata.ClusterSize);
      if (supb == null || !supb.AsSpan(0, 4).SequenceEqual("SUPB"u8)) continue;
      var refsOffset = checked((int)ReadU32(supb, 0x70));
      var refsCount = checked((int)ReadU32(supb, 0x74));
      if (refsCount is < 1 or > 8 || refsOffset < 0 || refsOffset + refsCount * 8 > supb.Length)
        continue;

      var changed = false;
      for (var i = 0; i < refsCount; ++i) {
        var at = refsOffset + i * 8;
        if (ReadU64(supb, at) != oldLcn) continue;
        BinaryPrimitives.WriteUInt64LittleEndian(supb.AsSpan(at, 8), newLcn);
        changed = true;
      }
      if (!changed) continue;

      BinaryPrimitives.WriteUInt64LittleEndian(supb.AsSpan(0x68, 8), newGeneration);
      var selfOffset = checked((int)ReadU32(supb, 0x78));
      var selfLength = checked((int)ReadU32(supb, 0x7C));
      RefsChecksum.RefreshSelfChecksum(supb, this.Metadata.ClusterSize, selfOffset, selfLength);
      this._image.Position = checked((long)supbLcn * this.Metadata.ClusterSize);
      this._image.Write(supb);
      ++wrote;
    }
    if (wrote == 0)
      throw new InvalidDataException($"No live ReFS SUPB copy referenced checkpoint 0x{oldLcn:X}.");
    this._image.Flush();
  }

  private static byte[]? ReadCluster(Stream image, ulong lcn, int clusterSize) {
    var offset = checked((long)lcn * clusterSize);
    if (offset < 0 || offset + clusterSize > image.Length) return null;
    var result = new byte[clusterSize];
    image.Position = offset;
    image.ReadExactly(result);
    return result;
  }

  private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset)
    => offset >= 0 && offset + 4 <= bytes.Length
      ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4))
      : throw new InvalidDataException("ReFS bootstrap field lies outside its structure.");

  private static ulong ReadU64(ReadOnlySpan<byte> bytes, int offset)
    => offset >= 0 && offset + 8 <= bytes.Length
      ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8))
      : throw new InvalidDataException("ReFS bootstrap field lies outside its structure.");
}
