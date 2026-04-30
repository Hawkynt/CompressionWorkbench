namespace Compression.Tests.Mix;

[TestFixture]
public class MixTests {

  [Test, Category("HappyPath")]
  public void WestwoodCrc_KnownVector() {
    // Regression-locked against the OpenRA-equivalent "classic" Westwood hash algorithm.
    // (rotate-left + add per little-endian dword over uppercased, null-padded ASCII).
    Assert.That(FileFormat.Mix.WestwoodCrc.Hash("AIRCRAFT.SHP"), Is.EqualTo(0x061DFAD7u));
    Assert.That(FileFormat.Mix.WestwoodCrc.Hash("CONQUER.MIX"), Is.EqualTo(0xA2361104u));
    Assert.That(FileFormat.Mix.WestwoodCrc.Hash("A"), Is.EqualTo(0x00000041u));
    Assert.That(FileFormat.Mix.WestwoodCrc.Hash("AB"), Is.EqualTo(0x00004241u));
  }

  [Test, Category("HappyPath")]
  public void WestwoodCrc_CaseInsensitive() {
    Assert.That(FileFormat.Mix.WestwoodCrc.Hash("aircraft.shp"),
      Is.EqualTo(FileFormat.Mix.WestwoodCrc.Hash("AIRCRAFT.SHP")));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Westwood test payload"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Mix.MixWriter(ms, leaveOpen: true))
      w.AddEntry("TEST.SHP", data);
    ms.Position = 0;

    var r = new FileFormat.Mix.MixReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Id, Is.EqualTo(FileFormat.Mix.WestwoodCrc.Hash("TEST.SHP")));
    Assert.That(r.Entries[0].Name, Is.EqualTo(r.Entries[0].Id.ToString("X8") + ".bin"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Entries[0].Offset, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
    Assert.That(r.BodySize, Is.EqualTo(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[64];
    var data2 = new byte[128];
    var data3 = new byte[32];
    Array.Fill(data1, (byte)0x11);
    Array.Fill(data2, (byte)0x22);
    Array.Fill(data3, (byte)0x33);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Mix.MixWriter(ms, leaveOpen: true)) {
      w.AddEntry("ALPHA.SHP", data1);
      w.AddEntry("BRAVO.SHP", data2);
      w.AddEntry("CHARLIE.SHP", data3);
    }
    ms.Position = 0;

    var r = new FileFormat.Mix.MixReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));

    // All three round-trip by ID lookup
    var byId = r.Entries.ToDictionary(e => e.Id);
    Assert.That(r.Extract(byId[FileFormat.Mix.WestwoodCrc.Hash("ALPHA.SHP")]), Is.EqualTo(data1));
    Assert.That(r.Extract(byId[FileFormat.Mix.WestwoodCrc.Hash("BRAVO.SHP")]), Is.EqualTo(data2));
    Assert.That(r.Extract(byId[FileFormat.Mix.WestwoodCrc.Hash("CHARLIE.SHP")]), Is.EqualTo(data3));

    // Directory is sorted ascending by ID
    for (var i = 1; i < r.Entries.Count; ++i)
      Assert.That(r.Entries[i].Id, Is.GreaterThan(r.Entries[i - 1].Id),
        "Directory must be sorted ascending by Westwood ID for binary-search lookup.");

    // Body size = sum of payloads
    Assert.That(r.BodySize, Is.EqualTo(data1.Length + data2.Length + data3.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_DirectorySorted() {
    // Add in deliberately unsorted order — verify the writer sorts by ID.
    var names = new[] { "ZULU.DAT", "ALPHA.DAT", "MIKE.DAT", "BRAVO.DAT" };
    var datas = names.Select(n => System.Text.Encoding.ASCII.GetBytes("payload-" + n)).ToArray();

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Mix.MixWriter(ms, leaveOpen: true)) {
      for (var i = 0; i < names.Length; ++i)
        w.AddEntry(names[i], datas[i]);
    }
    ms.Position = 0;

    var r = new FileFormat.Mix.MixReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(names.Length));

    for (var i = 1; i < r.Entries.Count; ++i)
      Assert.That(r.Entries[i].Id, Is.GreaterThan(r.Entries[i - 1].Id),
        "MIX directory must be sorted ascending by ID.");

    // All payloads still round-trip by ID
    var byId = r.Entries.ToDictionary(e => e.Id);
    for (var i = 0; i < names.Length; ++i) {
      var id = FileFormat.Mix.WestwoodCrc.Hash(names[i]);
      Assert.That(r.Extract(byId[id]), Is.EqualTo(datas[i]),
        $"Payload mismatch for '{names[i]}' (id=0x{id:X8})");
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_EmptyArchive() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Mix.MixWriter(ms, leaveOpen: true)) {
      // No entries
    }
    ms.Position = 0;

    var r = new FileFormat.Mix.MixReader(ms);
    Assert.That(r.Entries, Is.Empty);
    Assert.That(r.BodySize, Is.EqualTo(0));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_EmptyEntry() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Mix.MixWriter(ms, leaveOpen: true))
      w.AddEntry("EMPTY.DAT", []);
    ms.Position = 0;

    var r = new FileFormat.Mix.MixReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Mix.MixFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Mix"));
    Assert.That(d.DisplayName, Is.EqualTo("Westwood MIX"));
    Assert.That(d.Extensions, Contains.Item(".mix"));
    Assert.That(d.MagicSignatures, Is.Empty);
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".mix"));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries), Is.True);
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("td"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListAndExtract() {
    var data = "MIX descriptor test"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Mix.MixWriter(ms, leaveOpen: true))
      w.AddEntry("DESCTEST.SHP", data);
    ms.Position = 0;

    var d = new FileFormat.Mix.MixFormatDescriptor();
    var entries = d.List(ms, password: null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Method, Is.EqualTo("Stored"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(data.Length));
    Assert.That(entries[0].IsEncrypted, Is.False);
  }

  [Test, Category("ErrorHandling")]
  public void TooSmall_Throws() {
    using var ms = new MemoryStream([0x01, 0x02, 0x03]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Mix.MixReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void ImplausibleHeader_Throws() {
    // file_count = 1000 (would need 12000 bytes of directory + body), stream is only 6 bytes.
    var buf = new byte[6];
    buf[0] = 0xE8; buf[1] = 0x03; // 1000 LE
    buf[2] = 0x00; buf[3] = 0x00; buf[4] = 0x00; buf[5] = 0x00; // bodySize = 0
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Mix.MixReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void DuplicateName_Throws() {
    using var ms = new MemoryStream();
    using var w = new FileFormat.Mix.MixWriter(ms, leaveOpen: true);
    w.AddEntry("SAME.SHP", [1, 2, 3]);
    Assert.Throws<InvalidOperationException>(() => w.AddEntry("SAME.SHP", [4, 5, 6]));
  }
}
