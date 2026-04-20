namespace Compression.Tests.PackDisk;

[TestFixture]
public class PackDiskTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PackDisk_Properties() {
    var desc = new FileFormat.PackDisk.PackDiskFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("PackDisk"));
    Assert.That(desc.Extensions, Does.Contain(".pdsk"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("PDSK"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_XMash_Properties() {
    var desc = new FileFormat.PackDisk.XMashFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("xMash"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("XMSH"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_XDisk_Properties() {
    var desc = new FileFormat.PackDisk.XDiskFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("xDisk"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("XDSK"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Dcs_Properties() {
    var desc = new FileFormat.PackDisk.DcsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Dcs"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[4]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.PackDisk.PackDiskReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[100];
    data[0] = (byte)'X';
    data[1] = (byte)'X';
    data[2] = (byte)'X';
    data[3] = (byte)'X';
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.PackDisk.PackDiskReader(ms));
  }

  // ── WORM creation ────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void AllFour_ReportWormCapability() {
    Assert.That(new FileFormat.PackDisk.PackDiskFormatDescriptor().Capabilities
      .HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(new FileFormat.PackDisk.DcsFormatDescriptor().Capabilities
      .HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(new FileFormat.PackDisk.XDiskFormatDescriptor().Capabilities
      .HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(new FileFormat.PackDisk.XMashFormatDescriptor().Capabilities
      .HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_Stored_RoundTrip_PackDisk() {
    var t0 = new byte[FileFormat.PackDisk.PackDiskWriter.TrackSize];
    new Random(7).NextBytes(t0);
    var t1 = new byte[FileFormat.PackDisk.PackDiskWriter.TrackSize];
    new Random(11).NextBytes(t1);

    var w = new FileFormat.PackDisk.PackDiskWriter("PDSK");
    w.AddTrack(t0);
    w.AddTrack(t1);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileFormat.PackDisk.PackDiskReader(ms);
    Assert.That(r.Format, Is.EqualTo("PackDisk"));
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(t0));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(t1));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_RecognisesMagic_ForAllFourFormats() {
    // Sanity: each magic produces a file the reader classifies under the right name.
    foreach (var (magic, expectedFormat) in new[] {
      ("PDSK", "PackDisk"),
      ("XMSH", "xMash"),
      ("XDSK", "xDisk"),
      ("DCS\0", "DCS"),
    }) {
      var w = new FileFormat.PackDisk.PackDiskWriter(magic);
      w.AddTrack(new byte[FileFormat.PackDisk.PackDiskWriter.TrackSize]);
      using var ms = new MemoryStream();
      w.WriteTo(ms);
      ms.Position = 0;
      var r = new FileFormat.PackDisk.PackDiskReader(ms);
      Assert.That(r.Format, Is.EqualTo(expectedFormat), $"magic '{magic}'");
      Assert.That(r.Entries, Has.Count.EqualTo(1));
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_PackDisk_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, new byte[FileFormat.PackDisk.PackDiskWriter.TrackSize]);
      var d = new FileFormat.PackDisk.PackDiskFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "track_000.raw", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Method, Is.EqualTo("Stored"));
    } finally {
      File.Delete(tmp);
    }
  }
}
