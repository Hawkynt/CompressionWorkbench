using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

/// <summary>Layout helpers for the in-place defragmentation tests.</summary>
internal static class Reiser4TestLayouts {

  /// <summary>The blocks the tree's nodes occupy, in order.</summary>
  internal static ulong[] NodeBlocks(Stream image) {
    using var reader = new Reiser4Reader(image);
    Assert.That(reader.NativeTreeValid, Is.True, reader.NativeProblem);
    return [.. reader.NativeNodeBlocks.Order()];
  }

  /// <summary>
  /// Fragments a compact volume through the block mover itself: every other file's runs go to the
  /// free blocks at the end of the volume, last file highest, and the tree and bitmap are settled.
  /// </summary>
  internal static void ScatterInPlace(Stream image) {
    var descriptor = new Reiser4FormatDescriptor();
    image.Position = 0;
    var extents = descriptor.EnumerateExtents(image).ToList();
    var used = extents.Max(static e => e.Offset + e.Length);
    var mover = new Reiser4BlockMover();
    mover.Init(image);
    var top = image.Length;
    var files = extents.Where(static e => e.Kind == Compression.Registry.DefragBlockKind.Used).GroupBy(static e => e.FileName!).ToList();
    for (var i = 0; i < files.Count; i += 2)
      foreach (var run in files[i]) {
        var blocks = (run.Length + mover.BlockSize - 1) / mover.BlockSize * mover.BlockSize;
        var target = (top - blocks) / mover.BlockSize * mover.BlockSize;
        while (IsBitmap(target, blocks, mover.BlockSize)) target -= mover.BlockSize;
        Assert.That(target, Is.GreaterThan(used), "the volume has room to scatter into");
        mover.MoveExtent(image, run.Offset, target, run.Length);
        mover.UpdateAllocationAfterMove(image, run.FileName!, run.Offset, target, run.Length);
        top = target;
      }
    mover.SettleDirectory(image);
    image.Position = 0;
  }

  private static bool IsBitmap(long offset, long length, int blockSize) {
    for (var block = (ulong)(offset / blockSize); block < (ulong)((offset + length) / blockSize); ++block)
      if (block == 18 || block % Reiser4Writer.BlocksPerBitmap == 0) return true;
    return false;
  }
}
