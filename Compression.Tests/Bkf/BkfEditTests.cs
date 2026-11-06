using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Bkf;

namespace Compression.Tests.Bkf;

/// <summary>
/// Editing a BKF medium writes its Data Set anew: what stays keeps its DBLK bytes (attributes,
/// dates, names) and every data stream byte for byte; only the positional fields — Format
/// Logical Address, Control Block ID, directory and file IDs — and the checksums follow the
/// new layout. Media the editor cannot rewrite losslessly are refused untouched.
/// </summary>
[TestFixture]
public sealed class BkfEditTests {
  private static readonly DateTime When = new(2001, 9, 8, 7, 6, 5);

  /// <summary>A medium as NTBackup would write it: NACL security streams, attributes and dates.</summary>
  private static MemoryStream NtbackupLike() {
    var m = new MtfFixture();
    m.Set(1, s => s.Volb("C:")
      .Dirb("\0", security: true)
      .File("boot.ini", "[boot loader]"u8.ToArray(), security: true, attributes: 0x0F00, modified: When)
      .Dirb("docs\0", security: true, attributes: 1 << 8)
      .File("a.txt", "alpha"u8.ToArray(), security: true, modified: When)
      .File("b.txt", "beta"u8.ToArray(), security: true, modified: When)
      .Dirb("docs\0old\0", security: true)
      .File("gone.txt", "gone"u8.ToArray(), security: true));
    var ms = new MemoryStream();
    ms.Write(m.Build());
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, byte[]> Files(Stream bkf) {
    bkf.Position = 0;
    using var r = new BkfReader(bkf, leaveOpen: true);
    return r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, r.Extract);
  }

  /// <summary>Every DBLK of the set by path, its bytes with the positional fields and checksum blanked, and its streams.</summary>
  private static Dictionary<string, (byte[] Dblk, string Streams)> Kept(Stream bkf) {
    bkf.Position = 0;
    var media = MtfMedia.Read(bkf, keepData: true);
    var result = new Dictionary<string, (byte[], string)>();
    var folder = "";
    foreach (var b in media.Sets[0].Blocks) {
      var d = (byte[])b.Dblk.Clone();
      d.AsSpan(20, 8).Clear();   // Format Logical Address
      d.AsSpan(36, 4).Clear();   // Control Block ID
      d.AsSpan(50, 2).Clear();   // header checksum
      if (b.Type == "DIRB") d.AsSpan(76, 4).Clear();
      if (b.Type == "FILE") d.AsSpan(76, 8).Clear();
      var streams = string.Join(";", b.Streams.Select(s => Convert.ToHexString(s.Header) + ":" + Convert.ToHexString(s.Data!)));
      var key = b.Type switch {
        "DIRB" => "DIRB " + (folder = b.String(80).Replace('\0', '/').TrimEnd('/')),
        "FILE" => "FILE " + folder + "/" + b.String(84),
        _ => b.Type,
      };
      result[key] = (d, streams);
    }
    return result;
  }

  [Test, Category("HappyPath")]
  public void GivenAnNtbackupLikeMedium_WhenAFileIsAddedToAFolder_ThenEveryKeptBlockKeepsItsBytesAndStreams() {
    using var bkf = NtbackupLike();
    var before = Kept(bkf);

    new BkfFormatDescriptor().Add(bkf, [ArchiveInputInfo.InMemory("docs/new.txt", "new"u8.ToArray())]);

    var after = Kept(bkf);
    Assert.Multiple(() => {
      foreach (var (key, (dblk, streams)) in before) {
        Assert.That(after.ContainsKey(key), Is.True, key);
        Assert.That(after[key].Dblk, Is.EqualTo(dblk), $"{key}: DBLK bytes");
        Assert.That(after[key].Streams, Is.EqualTo(streams), $"{key}: data streams");
      }
      Assert.That(Files(bkf)["docs/new.txt"], Is.EqualTo("new"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAFileAddedToAFolder_WhenRead_ThenItFollowsThatFoldersFilesAndIdsRunOn() {
    using var bkf = NtbackupLike();
    BkfEditor.AddFile(bkf, "docs/new.txt", "new"u8.ToArray());

    bkf.Position = 0;
    var media = MtfMedia.Read(bkf, keepData: false);
    var types = media.Sets[0].Blocks.Select(b => b.Type == "FILE" ? "FILE:" + b.String(84) : b.Type).ToList();
    var files = media.Sets[0].Blocks.Where(b => b.Type == "FILE").ToList();
    Assert.Multiple(() => {
      Assert.That(types, Is.EqualTo(new[] { "VOLB", "DIRB", "FILE:boot.ini", "DIRB", "FILE:a.txt", "FILE:b.txt", "FILE:new.txt", "DIRB", "FILE:gone.txt" }));
      Assert.That(files.Select(f => f.U32(80)), Is.EqualTo(new uint[] { 1, 2, 3, 4, 5 }), "file IDs");
      Assert.That(files.Select(f => f.U32(76)), Is.EqualTo(new uint[] { 1, 2, 2, 2, 3 }), "directory IDs");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAFolder_WhenRemoved_ThenItsBlocksAndFilesGoAndTheRestStays() {
    using var bkf = NtbackupLike();
    new BkfFormatDescriptor().Remove(bkf, ["docs/old"]);
    Assert.That(Files(bkf).Keys, Is.EquivalentTo(new[] { "boot.ini", "docs/a.txt", "docs/b.txt" }));
  }

  [Test, Category("HappyPath")]
  public void GivenAFile_WhenReplaced_ThenThereIsOneCopyWithTheNewContent() {
    using var bkf = NtbackupLike();
    new BkfFormatDescriptor().Add(bkf, [ArchiveInputInfo.InMemory("docs/a.txt", "ALPHA2"u8.ToArray())]);
    var files = Files(bkf);
    Assert.That(files.Keys.Count(k => k == "docs/a.txt"), Is.EqualTo(1));
    Assert.That(files["docs/a.txt"], Is.EqualTo("ALPHA2"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void GivenAMissingName_WhenRemoved_ThenItIsReportedAndTheMediumUnchanged() {
    using var bkf = NtbackupLike();
    var before = bkf.ToArray();
    Assert.Throws<FileNotFoundException>(() => new BkfFormatDescriptor().Remove(bkf, ["docs/none.txt"]));
    Assert.That(bkf.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("ErrorHandling")]
  public void GivenAPathThroughAFolderName_WhenAddedAsAFile_ThenItIsRefused() {
    using var bkf = NtbackupLike();
    Assert.Throws<IOException>(() => BkfEditor.AddFile(bkf, "docs/old", [1]));
  }

  [Test, Category("ErrorHandling")]
  public void GivenTwoDataSets_WhenEdited_ThenItIsRefusedUntouched() {
    var m = new MtfFixture();
    m.Set(1, s => s.Volb("C:").Dirb("\0").File("a.txt", [1]));
    m.Set(2, s => s.Volb("C:").Dirb("\0").File("a.txt", [2]));
    using var bkf = new MemoryStream();
    bkf.Write(m.Build());
    var before = bkf.ToArray();
    Assert.Throws<NotSupportedException>(() => BkfEditor.AddFile(bkf, "b.txt", [3]));
    Assert.That(bkf.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("ErrorHandling")]
  public void GivenAMediaBasedCatalog_WhenEdited_ThenItIsRefusedUntouched() {
    var m = new MtfFixture(mediaBasedCatalog: 1);
    m.Set(1, s => s.Volb("C:").Dirb("\0").File("a.txt", [1]));
    using var bkf = new MemoryStream();
    bkf.Write(m.Build());
    var before = bkf.ToArray();
    Assert.Throws<NotSupportedException>(() => BkfEditor.Remove(bkf, "a.txt"));
    Assert.That(bkf.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("Boundary")]
  public void GivenANameTooLongForTheBlock_WhenAdded_ThenItGoesIntoAnFnamStreamAndReadsBack() {
    using var bkf = NtbackupLike();
    var name = new string('n', 600) + ".txt";
    BkfEditor.AddFile(bkf, "docs/" + name, "long"u8.ToArray());
    Assert.That(Files(bkf)["docs/" + name], Is.EqualTo("long"u8.ToArray()));
    bkf.Position = 0;
    var file = MtfMedia.Read(bkf, keepData: false).Sets[0].Blocks.Last(b => b.Type == "FILE" && b.Stream("FNAM") != null);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(file.Dblk.AsSpan(52)) & (1u << 17), Is.Not.Zero, "FILE_NAME_IN_STREAM");
  }
}
