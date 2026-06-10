#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.ProDos;

namespace Compression.Tests.ProDos;

/// <summary>
/// Regression: the Volume Directory Header is 43 bytes (per spec), three bytes
/// larger than a regular 39-byte file entry. The first file slot of the volume
/// directory therefore starts at byte 4 + 43 = 47 of block 2, not byte 4 + 39 =
/// 43. The previous (buggy) layout placed slot 1 at byte 43, which overlapped
/// the volume header's bit_map_pointer (at byte 4 + 0x26 = 42) and total_blocks
/// (at byte 4 + 0x28 = 44) fields. The first AddFile silently corrupted those
/// fields; the second AddFile read garbage for total_blocks and threw
/// "ProDOS: out of free blocks" on AllocateBlock.
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
      imageBefore.AsSpan(VolumeDirStartBlock * BlockSize + 4 + 0x28, 2));
    var bitmapPointerBefore = BinaryPrimitives.ReadUInt16LittleEndian(
      imageBefore.AsSpan(VolumeDirStartBlock * BlockSize + 4 + 0x26, 2));
    Assert.That(totalBlocksBefore, Is.EqualTo(ProDosWriter.FloppyTotalBlocks),
      "fresh image has the canonical floppy block count");
    Assert.That(bitmapPointerBefore, Is.EqualTo(6),
      "fresh image points its bitmap at block 6");

    ProDosModifier.AddFile(ms, "FILE1", "content"u8.ToArray());

    // The first AddFile must not touch the volume header's total_blocks or
    // bit_map_pointer fields, which live at bytes 4+0x26..4+0x29 of block 2 —
    // bytes the buggy slot-1-at-byte-43 layout would have overwritten with the
    // file name and storage-type byte.
    var imageAfter = ms.ToArray();
    var totalBlocksAfter = BinaryPrimitives.ReadUInt16LittleEndian(
      imageAfter.AsSpan(VolumeDirStartBlock * BlockSize + 4 + 0x28, 2));
    var bitmapPointerAfter = BinaryPrimitives.ReadUInt16LittleEndian(
      imageAfter.AsSpan(VolumeDirStartBlock * BlockSize + 4 + 0x26, 2));
    Assert.That(totalBlocksAfter, Is.EqualTo(totalBlocksBefore),
      "AddFile must not corrupt total_blocks");
    Assert.That(bitmapPointerAfter, Is.EqualTo(bitmapPointerBefore),
      "AddFile must not corrupt bit_map_pointer");
  }
}
