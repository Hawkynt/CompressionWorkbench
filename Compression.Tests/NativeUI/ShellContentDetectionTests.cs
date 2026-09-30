using System;
using System.IO;
using Compression.Core.DiskImage;
using Compression.Lib;
using Compression.NativeUI;
using Compression.NativeUI.ViewModels;
using Compression.Registry;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>
/// The shell judges an open image by its content, not its extension: a <c>.img</c> holding a
/// partition table is a partitioned disk, and what the shell offers to do with it follows from that.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ShellContentDetectionTests {
  private string _root = null!;
  private MainViewModel _model = null!;

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "cwb-shelldetect-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._root);
    UserSettings.PathOverride = Path.Combine(this._root, "settings.json");
    FormatRegistration.EnsureInitialized();
    this._model = new MainViewModel();
  }

  [TearDown]
  public void TearDown() {
    UserSettings.PathOverride = null;
    try { Directory.Delete(this._root, recursive: true); } catch { }
  }

  private static byte[] Fat() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("HELLO.TXT", "hello"u8.ToArray());
    return writer.Build();
  }

  /// <summary>A whole-disk image: an MBR with one FAT partition starting at 1 MiB.</summary>
  private static byte[] PartitionedDisk() {
    const long mib = 1024 * 1024;
    var fat = Fat();
    var length = (fat.Length + 511) / 512 * 512;
    var disk = new byte[mib + length + mib];
    using (var stream = new MemoryStream(disk, writable: true))
      new PartitionEditor(stream).AddPartition(mib, length, PartitionType.Fat16Lba, "data");
    fat.CopyTo(disk, (int)mib);
    return disk;
  }

  private string Write(string name, byte[] data) {
    var path = Path.Combine(this._root, name);
    File.WriteAllBytes(path, data);
    return path;
  }

  [Test]
  public void GivenAnImgHoldingAPartitionTable_WhenOpened_ThenItIsAPartitionedDiskAndNothingIsOfferedToBeAddedToIt() {
    this._model.Open(this.Write("disk.img", PartitionedDisk()));

    Assert.Multiple(() => {
      Assert.That(this._model.Format, Is.EqualTo("PartitionedDisk"), "the extension says FAT; the bytes say whole disk");
      Assert.That(this._model.AddFilesCommand.CanExecute(null), Is.False,
        "adding through the FAT writer would write a FAT structure over the partition table");
    });
  }

  [Test]
  public void GivenAnImgHoldingABareFatVolume_WhenOpened_ThenFilesCanBeAddedToIt() {
    this._model.Open(this.Write("floppy.img", Fat()));

    Assert.Multiple(() => {
      Assert.That(this._model.Format, Is.EqualTo("Fat"));
      Assert.That(this._model.AddFilesCommand.CanExecute(null), Is.True);
    });
  }

  [Test]
  public void GivenAPartitionedDisk_WhenFilesAreDroppedOnIt_ThenTheDropIsRefused() {
    this._model.Open(this.Write("disk.img", PartitionedDisk()));

    var (allowed, _) = this._model.EvaluateDropAgainstCurrentArchive([Path.Combine(this._root, "any.txt")]);

    Assert.That(allowed, Is.False);
  }
}
