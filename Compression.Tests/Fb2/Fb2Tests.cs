#pragma warning disable CS1591
using System.Text;
using FileFormat.Fb2;

namespace Compression.Tests.Fb2;

[TestFixture]
public class Fb2Tests {

  private static byte[] MakeMinimalFb2() => Encoding.UTF8.GetBytes("""
    <?xml version="1.0" encoding="UTF-8"?>
    <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0">
      <description>
        <title-info>
          <book-title>Test Book</book-title>
          <author><first-name>John</first-name><last-name>Doe</last-name></author>
          <lang>en</lang>
        </title-info>
      </description>
      <body>
        <section><title><p>Chapter 1</p></title><p>Content</p></section>
        <section><title><p>Chapter 2</p></title><p>More content</p></section>
      </body>
      <binary id="cover.jpg" content-type="image/jpeg">/9j/4AAQ</binary>
    </FictionBook>
    """);

  [Test]
  public void DescriptorListHasChaptersAndMetadata() {
    var data = MakeMinimalFb2();
    using var ms = new MemoryStream(data);
    var entries = new Fb2FormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.fb2"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("chapter_")), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Count(e => e.Name.StartsWith("chapter_")), Is.EqualTo(2));
  }

  [Test]
  public void MetadataIniContainsTitleAndAuthor() {
    var data = MakeMinimalFb2();
    var tmpDir = Path.Combine(Path.GetTempPath(), "fb2_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new Fb2FormatDescriptor().Extract(ms, tmpDir, null, null);
      var ini = File.ReadAllText(Path.Combine(tmpDir, "metadata.ini"));
      Assert.That(ini, Does.Contain("title=Test Book"));
      Assert.That(ini, Does.Contain("John Doe"));
    } finally {
      if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
    }
  }
}
