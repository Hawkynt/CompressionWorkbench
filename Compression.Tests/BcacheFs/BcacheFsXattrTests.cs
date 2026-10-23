using System.Text;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// Extended attributes the writer lays down, read back here. The kernel's view of
/// the same keys is checked by <c>BcacheFsKernelMountTests</c>.
/// </summary>
[TestFixture]
public sealed class BcacheFsXattrTests {

  private static BcacheFsReader Read(BcacheFsWriter writer, MemoryStream image) {
    writer.WriteTo(image);
    image.Position = 0;
    return new BcacheFsReader(image);
  }

  [Test, Category("HappyPath")]
  public void GivenXattrsOnAFileALinkADirectoryAndTheRoot_WhenRead_ThenEachCarriesItsOwn() {
    var writer = new BcacheFsWriter();
    writer.AddFile("dir/a.txt", "a"u8.ToArray());
    writer.AddSymlink("l", "dir/a.txt");
    writer.AddExtendedAttribute("dir/a.txt", "user.comment", "hello"u8.ToArray());
    writer.AddExtendedAttribute("dir/a.txt", "trusted.t", [0, 1, 2]);
    writer.AddExtendedAttribute("dir/a.txt", "security.s", []);
    writer.AddExtendedAttribute("l", "user.on-link", "x"u8.ToArray());
    writer.AddExtendedAttribute("dir", "user.on-dir", "y"u8.ToArray());
    writer.AddExtendedAttribute("", "user.on-root", "z"u8.ToArray());
    using var image = new MemoryStream();
    using var reader = Read(writer, image);

    Assert.Multiple(() => {
      Assert.That(reader.Valid, Is.True, reader.Status);
      Assert.That(reader.ExtendedAttributes.Keys, Is.EquivalentTo(new[] { "dir/a.txt", "l", "dir", "" }));
      var file = reader.ExtendedAttributes["dir/a.txt"];
      Assert.That(file["user.comment"], Is.EqualTo("hello"u8.ToArray()));
      Assert.That(file["trusted.t"], Is.EqualTo(new byte[] { 0, 1, 2 }));
      Assert.That(file["security.s"], Is.Empty, "an empty value is still an attribute");
      Assert.That(reader.ExtendedAttributes["l"]["user.on-link"], Is.EqualTo("x"u8.ToArray()));
      Assert.That(reader.ExtendedAttributes["dir"]["user.on-dir"], Is.EqualTo("y"u8.ToArray()));
      Assert.That(reader.ExtendedAttributes[""]["user.on-root"], Is.EqualTo("z"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheSameNameSetTwice_WhenRead_ThenTheLastValueWins() {
    var writer = new BcacheFsWriter();
    writer.AddFile("a", []);
    writer.AddExtendedAttribute("a", "user.k", "first"u8.ToArray());
    writer.AddExtendedAttribute("a", "user.k", "second"u8.ToArray());
    using var image = new MemoryStream();
    using var reader = Read(writer, image);

    Assert.That(reader.ExtendedAttributes["a"]["user.k"], Is.EqualTo("second"u8.ToArray()));
  }

  [Test, Category("BoundaryValue")]
  public void GivenAValueThatExactlyFillsOneKey_WhenWritten_ThenItReadsBack() {
    // Header (4) + name (3) + value fills the 250 words a key's value may take.
    var value = new byte[BcacheFsXattrCodec.MaxValueBytes - 4 - 3];
    for (var i = 0; i < value.Length; ++i) value[i] = (byte)i;
    var writer = new BcacheFsWriter();
    writer.AddFile("a", []);
    writer.AddExtendedAttribute("a", "user.big", value);
    using var image = new MemoryStream();
    using var reader = Read(writer, image);

    Assert.That(reader.ExtendedAttributes["a"]["user.big"], Is.EqualTo(value));
  }

  [Test, Category("BoundaryValue")]
  public void GivenAValueOneByteTooLargeForAKey_WhenAdded_ThenItIsRefused()
    => Assert.Throws<ArgumentException>(() =>
      new BcacheFsWriter().AddExtendedAttribute("a", "user.big", new byte[BcacheFsXattrCodec.MaxValueBytes - 4 - 3 + 1]));

  [Test, Category("BoundaryValue")]
  public void GivenManyAttributesOnOneInode_WhenRead_ThenNoneIsLostToAHashCollision() {
    var writer = new BcacheFsWriter();
    writer.AddFile("a", []);
    for (var i = 0; i < 300; ++i) writer.AddExtendedAttribute("a", $"user.k{i}", Encoding.ASCII.GetBytes($"v{i}"));
    using var image = new MemoryStream();
    using var reader = Read(writer, image);

    Assert.That(reader.ExtendedAttributes["a"], Has.Count.EqualTo(300));
  }

  [TestCase("system.posix_acl_access")]
  [TestCase("os2.name")]
  [TestCase("comment")]
  [Category("ErrorHandling")]
  public void GivenANamespaceThisPackageDoesNotWrite_WhenAdded_ThenItIsRefused(string name)
    => Assert.Throws<NotSupportedException>(() => new BcacheFsWriter().AddExtendedAttribute("a", name, []));

  [TestCase("user.")]
  [TestCase("")]
  [Category("ErrorHandling")]
  public void GivenAnEmptyName_WhenAdded_ThenItIsRefused(string name)
    => Assert.Throws<ArgumentException>(() => new BcacheFsWriter().AddExtendedAttribute("a", name, []));

  [Test, Category("ErrorHandling")]
  public void GivenAnXattrOnAPathNeverAdded_WhenWritten_ThenItIsRefused() {
    var writer = new BcacheFsWriter();
    writer.AddExtendedAttribute("missing", "user.k", []);
    using var image = new MemoryStream();
    Assert.Throws<ArgumentException>(() => writer.WriteTo(image));
  }

  [Test, Category("HappyPath")]
  public void GivenAnAttribute_WhenHashed_ThenTheNamespaceByteLeadsTheName() {
    // The same stored name in two namespaces lands at two offsets.
    var user = BcacheFsXattrCodec.Hash(12345, 0, "k"u8);
    var trusted = BcacheFsXattrCodec.Hash(12345, 3, "k"u8);
    Assert.Multiple(() => {
      Assert.That(user, Is.Not.EqualTo(trusted));
      Assert.That(user, Is.EqualTo(BcacheFsFormat.SipHash24(12345, 0, [0, (byte)'k']) >> 1));
    });
  }
}
