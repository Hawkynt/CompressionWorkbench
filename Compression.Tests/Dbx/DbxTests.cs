using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Dbx;

namespace Compression.Tests.Dbx;

/// <summary>
/// Outlook Express DBX. The fixtures are the four real Outlook Express 6 files of the DbxOutlookExpress
/// test suite (MIT); the oracle is undbx 0.21, which saves Inbox.dbx's one message byte for byte as we
/// extract it. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class DbxTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DbxVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded DBX vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_OutlookExpressInbox_When_Read_Then_TheMessageUndbxSavesByteForByte() {
    var messages = new DbxReader(Vector("Inbox.dbx")).ReadMessages();
    Assert.That(messages, Has.Count.EqualTo(1));
    Assert.That(messages[0].Subject, Is.EqualTo("Welcome to Outlook Express 6"));
    Assert.That(messages[0].Data, Has.Length.EqualTo(10139));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(messages[0].Data)),
      Is.EqualTo("5690ac3f898d12554c351767385901b1281720a1b485b08057b47ced59891ec9"));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_OutlookExpressFolders_When_Read_Then_TheFolderTree() {
    var folders = new DbxReader(Vector("Folders.dbx")).ReadFolders();
    Assert.That(folders.Select(f => (f.Id, f.ParentId, f.Name, f.FileName)), Is.EqualTo(new (uint, uint, string, string)[] {
      (9, 0, "Hotmail", ""), (1, 0, "Local Folders", ""), (7, 1, "Deleted Items", ""), (8, 1, "Drafts", ""),
      (4, 1, "Inbox", "Inbox.dbx"), (5, 1, "Outbox", "Outbox.dbx"), (6, 1, "Sent Items", ""),
    }));
  }

  [TestCase("Outbox.dbx", DbxKind.Messages)]
  [TestCase("Offline.dbx", DbxKind.Offline)]
  [Category("ThirdPartyVector")]
  public void Given_MailboxWithoutMessages_When_Listed_Then_Empty(string vector, DbxKind kind) {
    Assert.That(new DbxReader(Vector(vector)).Kind, Is.EqualTo(kind));
    Assert.That(new DbxFormatDescriptor().List(new MemoryStream(Vector(vector)), null), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Given_Inbox_When_ExtractedViaDescriptor_Then_MessageEml() {
    var d = new DbxFormatDescriptor();
    Assert.That(d.List(new MemoryStream(Vector("Inbox.dbx")), null).Select(e => e.Name), Is.EqualTo(new[] { "message_0000.eml" }));
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_dbx_{Guid.NewGuid():N}");
    try {
      d.Extract(new MemoryStream(Vector("Folders.dbx")), dir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(dir, "folders.ini")), Does.Contain("name=Inbox\nparent=1\nfile=Inbox.dbx"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Given_DbxPath_When_Detected_Then_Dbx() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_dbx_{Guid.NewGuid():N}.dbx");
    File.WriteAllBytes(path, Vector("Inbox.dbx"));
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("Dbx"));
    } finally { File.Delete(path); }
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongSignature_When_Read_Then_InvalidData() {
    var bytes = Vector("Inbox.dbx");
    bytes[1] = 0;
    Assert.Throws<InvalidDataException>(() => _ = new DbxReader(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedHeader_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new DbxReader(Vector("Inbox.dbx")[..0x1000]));

  [Test, Category("EdgeCase")]
  public void Given_TreeRootPointingAway_When_Read_Then_InvalidData() {
    var bytes = Vector("Inbox.dbx");
    BitConverter.GetBytes(0x24C0u).CopyTo(bytes, 0x39 * 4); // a root that does not name itself
    Assert.Throws<InvalidDataException>(() => new DbxReader(bytes).ReadMessages());
  }

  [Test, Category("EdgeCase")]
  public void Given_MessageChainLoop_When_Read_Then_InvalidData() {
    var bytes = Vector("Inbox.dbx");
    var reader = new DbxReader(bytes);
    // Find the first data block: its header names itself and its 'next' field follows at +12.
    var message = reader.ReadMessages()[0].Data;
    var firstText = bytes.AsSpan().IndexOf(message.AsSpan(0, 32));
    var block = firstText - 16;
    BitConverter.GetBytes((uint)block).CopyTo(bytes, block + 12); // next -> itself
    Assert.Throws<InvalidDataException>(() => new DbxReader(bytes).ReadMessages());
  }
}
