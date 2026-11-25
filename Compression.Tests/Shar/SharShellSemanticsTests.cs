using System.Text;
using FileFormat.Shar;

namespace Compression.Tests.Shar;

/// <summary>
/// The reader interprets a shar the way <c>/bin/sh</c> runs it: each here-document line is handed
/// on with its newline, byte for byte, to the command that owns it.
/// </summary>
[TestFixture]
public class SharShellSemanticsTests {

  private static IReadOnlyList<SharEntry> Read(string script)
    => new SharReader(new MemoryStream(Encoding.Latin1.GetBytes(script))).Entries;

  [Category("HappyPath")]
  [Test]
  public void Given_CarriageReturnsInABody_When_Read_Then_TheyAreKept() {
    var entries = Read("sed 's/^X//' > 'a.txt' << 'SHAR_EOF'\nXdos\r\nXline\r\nSHAR_EOF\n");
    Assert.That(entries.Single().Data, Is.EqualTo("dos\r\nline\r\n"u8.ToArray()));
  }

  [Category("HappyPath")]
  [Test]
  public void Given_RedirectionAfterTheHereDocOperator_When_Read_Then_TheFileIsFound() {
    var entries = Read("  sed 's/^X//' << 'SHAR_EOF' > 'a.txt' &&\nXhello\nSHAR_EOF\n");
    Assert.That(entries.Single().FileName, Is.EqualTo("a.txt"));
    Assert.That(entries.Single().Data, Is.EqualTo("hello\n"u8.ToArray()));
  }

  [Category("EdgeCase")]
  [Test]
  public void Given_HereDocWithoutOutput_When_Read_Then_NoEntry() {
    var entries = Read("md5sum -c >/dev/null 2>&1 << \\SHAR_EOF\n0123  a.txt\nSHAR_EOF\ncat << 'EOF'\nmessage\nEOF\n");
    Assert.That(entries, Is.Empty);
  }

  [Category("EdgeCase")]
  [Test]
  public void Given_RedirectionOnlyCommand_When_Read_Then_AnEmptyFile() {
    var entries = Read("${echo} \"x - extracting empty.txt (empty)\"\n > 'empty.txt' &&\n  chmod 0644 'empty.txt'\n");
    Assert.That(entries.Single().FileName, Is.EqualTo("empty.txt"));
    Assert.That(entries.Single().Data, Is.Empty);
  }

  [Category("HappyPath")]
  [Test]
  public void Given_SedPipedToUudecode_When_Read_Then_TheBeginLineNamesTheFile() {
    // GNU shar's layout for a file without a final newline.
    var entries = Read("  sed 's/^X//' << 'SHAR_EOF' | uudecode &&\nbegin 600 nonl.txt\n1;F\\@;F5W;&EN92!A=\"!E;F30\n`\nend\nSHAR_EOF\n");
    Assert.That(entries.Single().FileName, Is.EqualTo("nonl.txt"));
    Assert.That(Encoding.ASCII.GetString(entries.Single().Data), Is.EqualTo("no newline at end"));
  }

  [Category("HappyPath")]
  [Test]
  public void Given_Base64Uudecode_When_Read_Then_Decoded() {
    var entries = Read("uudecode << 'EOF'\nbegin-base64 644 b.bin\nAAEC/w==\n====\nEOF\n");
    Assert.That(entries.Single().Data, Is.EqualTo(new byte[] { 0, 1, 2, 0xFF }));
  }

  [Category("EdgeCase")]
  [Test]
  public void Given_UnquotedDelimiter_When_Read_Then_BackslashEscapesAreProcessed() {
    var entries = Read("cat > a.txt << EOF\ncost \\$5 \\\\ more\njoined \\\nline\nEOF\n");
    Assert.That(Encoding.ASCII.GetString(entries.Single().Data), Is.EqualTo("cost $5 \\ more\njoined line\n"));
  }

  [Category("EdgeCase")]
  [Test]
  public void Given_QuotedDelimiter_When_Read_Then_BodyIsLiteral() {
    var entries = Read("cat > a.txt << \\EOF\ncost \\$5\nEOF\n");
    Assert.That(Encoding.ASCII.GetString(entries.Single().Data), Is.EqualTo("cost \\$5\n"));
  }

  [Category("EdgeCase")]
  [Test]
  public void Given_TabStrippingHereDoc_When_Read_Then_LeadingTabsGo() {
    var entries = Read("cat > a.txt <<- 'EOF'\n\t\tindented\n\tEOF\n");
    Assert.That(Encoding.ASCII.GetString(entries.Single().Data), Is.EqualTo("indented\n"));
  }

  [Category("EdgeCase")]
  [Test]
  public void Given_AppendRedirection_When_Read_Then_PartsAreJoined() {
    var entries = Read("cat > a.txt << 'EOF'\none\nEOF\ncat >> a.txt << 'EOF'\ntwo\nEOF\n");
    Assert.That(Encoding.ASCII.GetString(entries.Single().Data), Is.EqualTo("one\ntwo\n"));
  }

  [Category("EdgeCase")]
  [Test]
  public void Given_QuotedNameWithApostrophe_When_Read_Then_NameIsUnquoted() {
    var entries = Read("sed 's/^X//' << 'SHAR_EOF' > 'sub/it'\\''s spaced.txt' &&\nXin sub\nSHAR_EOF\n");
    Assert.That(entries.Single().FileName, Is.EqualTo("sub/it's spaced.txt"));
  }

  [Category("EdgeCase")]
  [Test]
  public void Given_OtherSedPrefix_When_Read_Then_ThatPrefixIsStripped() {
    var entries = Read("sed 's/^@//' > a.txt << 'EOF'\n@one\n@@two\nEOF\n");
    Assert.That(Encoding.ASCII.GetString(entries.Single().Data), Is.EqualTo("one\n@two\n"));
  }

  [Category("Exception")]
  [Test]
  public void Given_FileUnpackedByCompress_When_Read_Then_NotSupported() {
    var script = "sed 's/^X//' << 'SHAR_EOF' | uudecode &&\nbegin 600 _sh1/cmi\n#'YV0\n`\nend\nSHAR_EOF\ncompress -dc ${lock_dir}/cmi > 'a.txt' && \\\n  chmod 0644 'a.txt'\n";
    Assert.Throws<NotSupportedException>(() => Read(script));
  }

  // ── our writer emits what sh reads back ──

  [Category("Boundary")]
  [TestCase("")]
  [TestCase("\n")]
  [TestCase("a")]
  [TestCase("a\n")]
  [TestCase("a\r\n")]
  [TestCase("\n\n")]
  public void Given_TextAroundTheFinalNewline_When_RoundTripped_Then_BytesAreKept(string text) {
    var writer = new SharWriter();
    writer.AddFile("t.txt", Encoding.ASCII.GetBytes(text));
    var entries = new SharReader(new MemoryStream(writer.ToByteArray())).Entries;
    Assert.That(entries.Single().Data, Is.EqualTo(Encoding.ASCII.GetBytes(text)));
  }

  [Category("Regression")]
  [Test]
  public void Given_NameWithShellMetacharacters_When_Written_Then_TheAnnouncementQuotesIt() {
    var writer = new SharWriter();
    writer.AddFile("a;touch b", "x\n"u8.ToArray());
    var script = Encoding.UTF8.GetString(writer.ToByteArray());
    Assert.That(script, Does.Contain("echo x - 'a;touch b'"));
    Assert.That(new SharReader(new MemoryStream(writer.ToByteArray())).Entries.Single().FileName, Is.EqualTo("a;touch b"));
  }
}
