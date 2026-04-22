using Compression.Registry;

namespace Compression.Tests.ZipVariants;

[TestFixture]
public class ZipVariantTests {

  // ──────────────────────────────────────────────────────────────
  //  Descriptor_Properties tests
  // ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Jar_Descriptor_Properties() {
    var d = new FileFormat.Jar.JarFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Jar"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".jar"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void War_Descriptor_Properties() {
    var d = new FileFormat.War.WarFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("War"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".war"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Ear_Descriptor_Properties() {
    var d = new FileFormat.Ear.EarFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ear"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".ear"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Apk_Descriptor_Properties() {
    var d = new FileFormat.Apk.ApkFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Apk"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".apk"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Ipa_Descriptor_Properties() {
    var d = new FileFormat.Ipa.IpaFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ipa"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".ipa"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Xpi_Descriptor_Properties() {
    var d = new FileFormat.Xpi.XpiFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Xpi"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".xpi"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Epub_Descriptor_Properties() {
    var d = new FileFormat.Epub.EpubFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Epub"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".epub"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Odt_Descriptor_Properties() {
    var d = new FileFormat.Odt.OdtFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Odt"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".odt"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Ods_Descriptor_Properties() {
    var d = new FileFormat.Ods.OdsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ods"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".ods"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Odp_Descriptor_Properties() {
    var d = new FileFormat.Odp.OdpFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Odp"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".odp"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Docx_Descriptor_Properties() {
    var d = new FileFormat.Docx.DocxFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Docx"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".docx"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Xlsx_Descriptor_Properties() {
    var d = new FileFormat.Xlsx.XlsxFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Xlsx"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".xlsx"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Pptx_Descriptor_Properties() {
    var d = new FileFormat.Pptx.PptxFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Pptx"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".pptx"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Cbz_Descriptor_Properties() {
    var d = new FileFormat.Cbz.CbzFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Cbz"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".cbz"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Maff_Descriptor_Properties() {
    var d = new FileFormat.Maff.MaffFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Maff"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".maff"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Kmz_Descriptor_Properties() {
    var d = new FileFormat.Kmz.KmzFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Kmz"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".kmz"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Appx_Descriptor_Properties() {
    var d = new FileFormat.Appx.AppxFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Appx"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".appx"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void NuPkg_Descriptor_Properties() {
    var d = new FileFormat.NuPkg.NuPkgFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("NuPkg"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".nupkg"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  [Test, Category("HappyPath")]
  public void Crx_Descriptor_Properties() {
    var d = new FileFormat.Crx.CrxFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Crx"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".crx"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes,
      Is.EqualTo(new byte[] { (byte)'C', (byte)'r', (byte)'2', (byte)'4' }));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.95));
  }

  [Test, Category("HappyPath")]
  public void Cbr_Descriptor_Properties() {
    var d = new FileFormat.Cbr.CbrFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Cbr"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".cbr"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.InstanceOf<IFormatDescriptor>());
    Assert.That(d, Is.InstanceOf<IArchiveFormatOperations>());
  }

  // ──────────────────────────────────────────────────────────────
  //  RoundTrip_ViaInterface tests (ZIP-based variants)
  // ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Jar_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_jar_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "JAR test content");

      var desc = new FileFormat.Jar.JarFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("JAR test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void War_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_war_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "WAR test content");

      var desc = new FileFormat.War.WarFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("WAR test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Ear_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_ear_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "EAR test content");

      var desc = new FileFormat.Ear.EarFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("EAR test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Apk_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_apk_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "APK test content");

      var desc = new FileFormat.Apk.ApkFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("APK test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Ipa_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_ipa_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "IPA test content");

      var desc = new FileFormat.Ipa.IpaFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("IPA test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Xpi_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_xpi_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "XPI test content");

      var desc = new FileFormat.Xpi.XpiFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("XPI test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Epub_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_epub_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "EPUB test content");

      var desc = new FileFormat.Epub.EpubFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("EPUB test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Odt_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_odt_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "ODT test content");

      var desc = new FileFormat.Odt.OdtFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("ODT test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Ods_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_ods_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "ODS test content");

      var desc = new FileFormat.Ods.OdsFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("ODS test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Odp_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_odp_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "ODP test content");

      var desc = new FileFormat.Odp.OdpFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("ODP test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Docx_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_docx_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "DOCX test content");

      var desc = new FileFormat.Docx.DocxFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("DOCX test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Xlsx_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_xlsx_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "XLSX test content");

      var desc = new FileFormat.Xlsx.XlsxFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("XLSX test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Pptx_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_pptx_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "PPTX test content");

      var desc = new FileFormat.Pptx.PptxFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("PPTX test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Cbz_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_cbz_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "CBZ test content");

      var desc = new FileFormat.Cbz.CbzFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("CBZ test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Maff_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_maff_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "MAFF test content");

      var desc = new FileFormat.Maff.MaffFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("MAFF test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Kmz_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_kmz_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "KMZ test content");

      var desc = new FileFormat.Kmz.KmzFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("KMZ test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Appx_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_appx_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "APPX test content");

      var desc = new FileFormat.Appx.AppxFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("APPX test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void NuPkg_RoundTrip_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_nupkg_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var testFile = Path.Combine(tmpDir, "test.txt");
      File.WriteAllText(testFile, "NuPkg test content");

      var desc = new FileFormat.NuPkg.NuPkgFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [new ArchiveInputInfo(testFile, "test.txt", false)],
        new FormatCreateOptions());
      ms.Position = 0;

      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(ms, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "test.txt")), Is.EqualTo("NuPkg test content"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  // ──────────────────────────────────────────────────────────────
  //  CRX: List/Extract only (no Create — CRX3 header is read-only)
  // ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Crx_ListAndExtract_ViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_crx_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);

      // Build a CRX3 file in memory: magic "Cr24" + version(3) + header_length(0) + ZIP payload
      using var crxMs = new MemoryStream();
      crxMs.Write([(byte)'C', (byte)'r', (byte)'2', (byte)'4']); // magic
      crxMs.Write(BitConverter.GetBytes((uint)3));                // version
      crxMs.Write(BitConverter.GetBytes((uint)0));                // header length (no protobuf header)

      // Append a valid ZIP with one entry
      using (var w = new FileFormat.Zip.ZipWriter(crxMs, leaveOpen: true))
        w.AddEntry("manifest.json", "{\"name\":\"test\"}"u8.ToArray());

      crxMs.Position = 0;

      var desc = new FileFormat.Crx.CrxFormatDescriptor();
      var ops = desc;

      // List
      var entries = ops.List(crxMs, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("manifest.json"));

      // Extract
      crxMs.Position = 0;
      var extractDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(extractDir);
      ops.Extract(crxMs, extractDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "manifest.json")),
        Is.EqualTo("{\"name\":\"test\"}"));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }
}
