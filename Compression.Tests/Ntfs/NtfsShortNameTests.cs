namespace Compression.Tests.Ntfs;

/// <summary>
/// 8.3 short-name (SFN) generation switch. NTFS $FILE_NAME attributes carry a
/// namespace byte: 0 = POSIX, 1 = Win32, 2 = DOS, 3 = Win32&amp;DOS (a single
/// name valid as both a long name and an 8.3 short name). By default the writer
/// records names as Win32&amp;DOS (namespace 3), the way a freshly formatted
/// Windows volume does. Disabling short-name generation — the equivalent of
/// <c>fsutil behavior set disable8dot3</c> — records names as Win32-only
/// (namespace 1), suppressing the DOS short name. Files round-trip either way.
/// </summary>
[TestFixture]
public class NtfsShortNameTests {

  private const byte NamespaceWin32 = 1;
  private const byte NamespaceDos = 2;
  private const byte NamespaceWin32AndDos = 3;

  [Test, Category("RoundTrip")]
  public void ShortNamesEnabledByDefault_EmitsWin32AndDosNamespace() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("report.txt", "hello"u8.ToArray());
    var disk = w.Build();

    using (var ms = new MemoryStream(disk)) {
      var r = new FileSystem.Ntfs.NtfsReader(ms);
      var entry = r.Entries.Single(e => e.Name == "report.txt");
      Assert.That(r.Extract(entry), Is.EqualTo("hello"u8.ToArray()), "file content round-trips with short names on");
    }

    var record = MftInspector.FindRecordByFileName(disk, "report.txt");
    var namespaces = MftInspector.FileNameNamespaces(record);
    Assert.That(namespaces, Does.Contain(NamespaceWin32AndDos),
      "default writer emits a combined Win32&DOS name (carries the 8.3 short name)");
    Assert.That(namespaces, Does.Not.Contain(NamespaceDos),
      "no separate DOS-only $FILE_NAME entry is emitted");
  }

  [Test, Category("RoundTrip")]
  public void ShortNamesSuppressed_EmitsWin32OnlyNamespace_NoDosName() {
    var w = new FileSystem.Ntfs.NtfsWriter(generateShortNames: false);
    w.AddFile("report.txt", "hello"u8.ToArray());
    var disk = w.Build();

    using (var ms = new MemoryStream(disk)) {
      var r = new FileSystem.Ntfs.NtfsReader(ms);
      var entry = r.Entries.Single(e => e.Name == "report.txt");
      Assert.That(r.Extract(entry), Is.EqualTo("hello"u8.ToArray()), "file content round-trips with short names off");
    }

    var record = MftInspector.FindRecordByFileName(disk, "report.txt");
    var namespaces = MftInspector.FileNameNamespaces(record);
    Assert.That(namespaces, Does.Contain(NamespaceWin32),
      "short-name suppression records the long name in the Win32-only namespace");
    Assert.Multiple(() => {
      Assert.That(namespaces, Does.Not.Contain(NamespaceDos), "no DOS short name");
      Assert.That(namespaces, Does.Not.Contain(NamespaceWin32AndDos), "not the combined Win32&DOS namespace");
    });
  }

  [Test, Category("RoundTrip")]
  public void ShortNameSwitch_DoesNotBreakSubdirectories() {
    var w = new FileSystem.Ntfs.NtfsWriter(generateShortNames: false);
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    var disk = w.Build();

    using (var ms = new MemoryStream(disk)) {
      var r = new FileSystem.Ntfs.NtfsReader(ms);
      var file = r.Entries.Single(e => e.Name.Replace('\\', '/') == "docs/guide.txt");
      Assert.That(r.Extract(file), Is.EqualTo("in docs"u8.ToArray()), "nested file round-trips with short names off");
    }

    var namespaces = MftInspector.AllUserFileNameNamespaces(disk);
    Assert.That(namespaces, Does.Not.Contain(NamespaceDos), "no DOS short names anywhere in the image");
    Assert.That(namespaces, Does.Not.Contain(NamespaceWin32AndDos),
      "no combined Win32&DOS names anywhere when short names are suppressed");
  }
}
