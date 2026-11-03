#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.ProDos;

namespace Compression.Tests.ProDos;

/// <summary>
/// Regression: the volume header's bit_map_pointer ($23) and total_blocks ($25)
/// must survive file adds. The header fills slot 0 of block 2 (bytes $04..$2A) and
/// the first file entry starts at $2B; an add that put the entry anywhere else, or
/// read the header fields from shifted offsets, corrupted the free-block bookkeeping
/// and the second add failed with "out of free blocks".
/// </summary>
[TestFixture]
public class ProDosVolumeHeaderRegressionTests {

  private const int BlockSize = ProDosReader.BlockSize;
  private const int VolumeDirStartBlock = ProDosReader.VolumeDirStartBlock;

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new ProDosWriter().Build());
    return ms;
  }

  [Test, Category("Regression")]
  public void TwoSequentialAddFiles_BothSucceedAndAreReadable() {
    var ms = BuildEmptyImage();

    // Given a fresh image, both Add operations succeed.
    ProDosModifier.AddFile(ms, "FILE1", "first-content"u8.ToArray());
    ProDosModifier.AddFile(ms, "FILE2", "second-content"u8.ToArray());

    // When listing, both files are present with their original content.
    ms.Position = 0;
    using var reader = new ProDosReader(ms);
    var e1 = reader.Entries.SingleOrDefault(e => e.Name == "FILE1");
    var e2 = reader.Entries.SingleOrDefault(e => e.Name == "FILE2");
    Assert.That(e1, Is.Not.Null, "FILE1 must be present after AddFile");
    Assert.That(e2, Is.Not.Null, "FILE2 must be present after AddFile");

    var data1 = reader.Extract(e1!);
    var data2 = reader.Extract(e2!);
    Assert.That(System.Text.Encoding.ASCII.GetString(data1), Is.EqualTo("first-content"));
    Assert.That(System.Text.Encoding.ASCII.GetString(data2), Is.EqualTo("second-content"));
  }

  [Test, Category("Regression")]
  public void FirstAddFile_PreservesVolumeHeaderTotalBlocksAndBitMapPointer() {
    var ms = BuildEmptyImage();

    // Capture the pre-add header fields.
    var imageBefore = ms.ToArray();
    var totalBlocksBefore = BinaryPrimitives.ReadUInt16LittleEndian(
      imageBefore.AsSpan(VolumeDirStartBlock * BlockSize + 4 + ProDosReader.HeaderTotalBlocksOrParentEntry, 2));
    var bitmapPointerBefore = BinaryPrimitives.ReadUInt16LittleEndian(
      imageBefore.AsSpan(VolumeDirStartBlock * BlockSize + 4 + ProDosReader.HeaderBitmapOrParentPointer, 2));
    Assert.That(totalBlocksBefore, Is.EqualTo(ProDosWriter.FloppyTotalBlocks),
      "fresh image has the canonical floppy block count");
    Assert.That(bitmapPointerBefore, Is.EqualTo(6),
      "fresh image points its bitmap at block 6");

    ProDosModifier.AddFile(ms, "FILE1", "content"u8.ToArray());

    // The first AddFile must not touch the volume header's total_blocks or
    // bit_map_pointer fields, which live at header offsets $23..$26 of block 2 —
    // bytes a misplaced first entry would have overwritten with the
    // file name and storage-type byte.
    var imageAfter = ms.ToArray();
    var totalBlocksAfter = BinaryPrimitives.ReadUInt16LittleEndian(
      imageAfter.AsSpan(VolumeDirStartBlock * BlockSize + 4 + ProDosReader.HeaderTotalBlocksOrParentEntry, 2));
    var bitmapPointerAfter = BinaryPrimitives.ReadUInt16LittleEndian(
      imageAfter.AsSpan(VolumeDirStartBlock * BlockSize + 4 + ProDosReader.HeaderBitmapOrParentPointer, 2));
    Assert.That(totalBlocksAfter, Is.EqualTo(totalBlocksBefore),
      "AddFile must not corrupt total_blocks");
    Assert.That(bitmapPointerAfter, Is.EqualTo(bitmapPointerBefore),
      "AddFile must not corrupt bit_map_pointer");
  }
}
