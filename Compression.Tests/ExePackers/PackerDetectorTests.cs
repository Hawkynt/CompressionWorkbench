using System.Buffers.Binary;
using System.Text;
using Compression.Core.Crypto;
using Compression.Core.Deflate;
using Compression.Core.ExecutableUnpacking;
using Compression.Core.Streams;
using Compression.Lib;
using Compression.Tests.Support;
using FileFormat.ExePackers;
using FileFormat.Bzip2;
using FileFormat.Gzip;
using FileFormat.Xz;
using FileFormat.Zstd;

namespace Compression.Tests.ExePackers;

[TestFixture]
public class PackerDetectorTests {

  /// <summary>Builds a minimal MZ DOS executable header padded to 1 KB.</summary>
  private static byte[] MinimalMz() {
    var buf = new byte[1024];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    return buf;
  }

  /// <summary>Builds a minimal PE skeleton (MZ + PE header) padded to 1 KB.</summary>
  private static byte[] MinimalPe() {
    var buf = new byte[1024];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C), 0x80);
    buf[0x80] = (byte)'P'; buf[0x81] = (byte)'E';
    return buf;
  }

  // ── PKLITE ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void PkLite_DetectsCopyrightString() {
    var buf = MinimalMz();
    Encoding.ASCII.GetBytes("PKLITE Copr.").CopyTo(buf.AsSpan(0x100));

    using var ms = new MemoryStream(buf);
    var entries = new PkLiteFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "packed_payload.bin"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void PkLite_DetectsLowercaseCopyrightVariant() {
    var buf = MinimalMz();
    Encoding.ASCII.GetBytes("PKlite Copr.").CopyTo(buf.AsSpan(0x80));
    using var ms = new MemoryStream(buf);
    Assert.That(new PkLiteFormatDescriptor().List(ms, null), Is.Not.Empty);
  }

  [Test, Category("EdgeCase")]
  public void PkLite_PlainMz_Throws() {
    using var ms = new MemoryStream(MinimalMz());
    Assert.That(() => new PkLiteFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── LZEXE ─────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void LzExe_Detects091Signature() {
    var buf = MinimalMz();
    Encoding.ASCII.GetBytes("LZ91").CopyTo(buf.AsSpan(0x1C));
    using var ms = new MemoryStream(buf);
    var entries = new LzExeFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void LzExe_Detects090Signature() {
    var buf = MinimalMz();
    Encoding.ASCII.GetBytes("LZ09").CopyTo(buf.AsSpan(0x1C));
    using var ms = new MemoryStream(buf);
    Assert.That(new LzExeFormatDescriptor().List(ms, null), Is.Not.Empty);
  }

  [Test, Category("EdgeCase")]
  public void LzExe_NotMz_Throws() {
    var buf = new byte[64];
    using var ms = new MemoryStream(buf);
    Assert.That(() => new LzExeFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── GZEXE ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Gzexe_ExtractsEmbeddedGzipExecutable() {
    var original = Encoding.ASCII.GetBytes("#!/bin/sh\necho original executable payload\n");
    var wrapper = BuildGzexeWrapper(original);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var listStream = new MemoryStream(wrapper);
      var entries = new GzexeFormatDescriptor().List(listStream, null);
      Assert.That(entries.Any(e => e.Name == "compressed_payload.gz"), Is.True);
      Assert.That(entries.Any(e => e.Name == "reconstructed/original_executable.bin"), Is.True);

      using var extractStream = new MemoryStream(wrapper);
      new GzexeFormatDescriptor().Extract(extractStream, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "original_executable.bin")),
        Is.EqualTo(original).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Gzexe_PlainShellScript_Throws() {
    using var ms = new MemoryStream(Encoding.ASCII.GetBytes("#!/bin/sh\necho not packed\n"));
    Assert.That(() => new GzexeFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── BZEXE ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Bzexe_ExtractsEmbeddedBzip2Executable() {
    var original = Encoding.ASCII.GetBytes("#!/bin/sh\necho original bzip2 executable payload\n");
    var wrapper = BuildBzexeWrapper(original);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var listStream = new MemoryStream(wrapper);
      var entries = new BzexeFormatDescriptor().List(listStream, null);
      Assert.That(entries.Any(e => e.Name == "compressed_payload.bz2"), Is.True);
      Assert.That(entries.Any(e => e.Name == "reconstructed/original_executable.bin"), Is.True);

      using var extractStream = new MemoryStream(wrapper);
      new BzexeFormatDescriptor().Extract(extractStream, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "original_executable.bin")),
        Is.EqualTo(original).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Bzexe_PlainShellScript_Throws() {
    using var ms = new MemoryStream(Encoding.ASCII.GetBytes("#!/bin/sh\necho not packed\n"));
    Assert.That(() => new BzexeFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("HappyPath")]
  public void Papaw_ExtractsObfuscatedXzExecutable() {
    var original = "#!/bin/sh\necho papaw descriptor\n"u8.ToArray();
    var packed = BuildPapawWrapper(original);
    var descriptor = new PapawFormatDescriptor();

    using var listStream = new MemoryStream(packed);
    var entries = descriptor.List(listStream, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("reconstructed/original_executable.bin"));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var extractStream = new MemoryStream(packed);
      descriptor.Extract(extractStream, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "original_executable.bin")),
        Is.EqualTo(original).AsCollection);
      Assert.That(File.Exists(Path.Combine(tmp, "compressed_payload.restored.xz")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Papaw_PlainElf_Throws() {
    var plainElf = new byte[128];
    plainElf[0] = 0x7F; plainElf[1] = (byte)'E'; plainElf[2] = (byte)'L'; plainElf[3] = (byte)'F';
    using var ms = new MemoryStream(plainElf);
    Assert.That(() => new PapawFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExternalTool")]
  public void ExternalPapawTool_GeneratedFixture_ExtractsOriginalExecutable() {
    var papaw = ExecutablePackerToolCache.GetPapaw();
    Assume.That(papaw, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download Papaw release assets.");

    var python = ExecutablePackerToolCache.GetHostTool("python3", "python");
    Assume.That(python, Is.Not.Null, "python3/python is required to run papawify-xz.");

    var xz = ExecutablePackerToolCache.GetHostTool("xz");
    Assume.That(xz, Is.Not.Null, "xz is required by papawify-xz to generate compressed Papaw fixtures.");

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      var original = Encoding.ASCII.GetBytes("#!/bin/sh\necho third-party papaw fixture\n");
      var fixture = Path.Combine(tmp, "fixture.sh");
      var packed = Path.Combine(tmp, "fixture.papaw");
      File.WriteAllBytes(fixture, original);

      var path = Environment.GetEnvironmentVariable("PATH") ?? "";
      var env = new Dictionary<string, string> {
        ["PATH"] = Path.GetDirectoryName(xz!) + Path.PathSeparator + path,
      };
      var packOutput = ExecutablePackerToolCache.RunWithEnvironment(python!, env, papaw!.Papawify, papaw.Stub, fixture, packed);
      Assert.That(File.Exists(packed), Is.True, packOutput);

      using var ms = File.OpenRead(packed);
      new PapawFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "original_executable.bin")),
        Is.EqualTo(original).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("ExternalTool")]
  public void ExternalBzexeTool_GeneratedFixture_ExtractsOriginalExecutable() {
    var bzexe = ExecutablePackerToolCache.GetPackerTool("bzexe", "bzexe");
    Assume.That(bzexe, Is.Not.Null, "Put bzexe on PATH or set CWB_PACKER_BZEXE_URL with CWB_DOWNLOAD_EXE_PACKER_TOOLS=1.");

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      var original = Encoding.ASCII.GetBytes("#!/bin/sh\necho third-party bzexe fixture\n");
      var fixture = Path.Combine(tmp, "fixture.sh");
      File.WriteAllBytes(fixture, original);

      _ = ExecutablePackerToolCache.Run(bzexe!, fixture);
      Assume.That(File.Exists(fixture), Is.True, "bzexe did not produce an in-place wrapper.");

      using var ms = File.OpenRead(fixture);
      new BzexeFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "original_executable.bin")),
        Is.EqualTo(original).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("ExternalTool")]
  public void ExternalGzexeTool_GeneratedFixture_ExtractsOriginalExecutable() {
    var gzexe = ExecutablePackerToolCache.GetPackerTool("gzexe", "gzexe");
    Assume.That(gzexe, Is.Not.Null, "Put gzexe on PATH or set CWB_PACKER_GZEXE_URL with CWB_DOWNLOAD_EXE_PACKER_TOOLS=1.");

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      var original = Encoding.ASCII.GetBytes("#!/bin/sh\necho third-party gzexe fixture\n");
      var fixture = Path.Combine(tmp, "fixture.sh");
      File.WriteAllBytes(fixture, original);

      _ = ExecutablePackerToolCache.Run(gzexe!, fixture);
      Assume.That(File.Exists(fixture), Is.True, "gzexe did not produce an in-place wrapper.");

      using var ms = File.OpenRead(fixture);
      new GzexeFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "original_executable.bin")),
        Is.EqualTo(original).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  // ── Petite ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GoPacker_ExtractsAppendedZstdExecutable() {
    var original = "#!/bin/sh\necho gopacker descriptor\n"u8.ToArray();
    var packed = BuildGoPackerWrapper(original);
    var descriptor = new GoPackerFormatDescriptor();

    using var listStream = new MemoryStream(packed);
    var entries = descriptor.List(listStream, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("reconstructed/original_executable.bin"));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var extractStream = new MemoryStream(packed);
      descriptor.Extract(extractStream, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "original_executable.bin")),
        Is.EqualTo(original).AsCollection);
      Assert.That(File.Exists(Path.Combine(tmp, "compressed_payload.zst")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void GoPacker_PlainElf_Throws() {
    var plainElf = new byte[128];
    plainElf[0] = 0x7F; plainElf[1] = (byte)'E'; plainElf[2] = (byte)'L'; plainElf[3] = (byte)'F';
    using var ms = new MemoryStream(plainElf);
    Assert.That(() => new GoPackerFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExternalTool")]
  public void ExternalGoPackerTool_GeneratedFixture_ExtractsOriginalExecutable() {
    var go = ExecutablePackerToolCache.GetHostTool("go");
    Assume.That(go, Is.Not.Null, "A Go toolchain is required to run the official GoPacker source.");

    var source = ExecutablePackerToolCache.GetGoPackerSource();
    Assume.That(source, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download GoPacker source.");

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      var original = Encoding.ASCII.GetBytes("#!/bin/sh\necho third-party gopacker fixture\n");
      var fixture = Path.Combine(tmp, "fixture.sh");
      File.WriteAllBytes(fixture, original);

      var packOutput = ExecutablePackerToolCache.RunInDirectory(go!, source!, "run", ".", fixture);
      var packed = fixture + ".packed";
      Assert.That(File.Exists(packed), Is.True, packOutput);

      using var ms = File.OpenRead(packed);
      new GoPackerFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "original_executable.bin")),
        Is.EqualTo(original).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Origami_ExtractsXoredDeflateManagedAssembly() {
    var original = "MZ synthetic original managed assembly"u8.ToArray();
    var packed = BuildOrigamiWrapper(original);
    var descriptor = new OrigamiFormatDescriptor();

    using var listStream = new MemoryStream(packed);
    var entries = descriptor.List(listStream, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("encrypted_payload.bin"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("compressed_payload.deflate"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("reconstructed/original_assembly.bin"));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var extractStream = new MemoryStream(packed);
      descriptor.Extract(extractStream, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "original_assembly.bin")),
        Is.EqualTo(original).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Origami_PlainManagedPe_Throws() {
    using var ms = new MemoryStream(MinimalPe());
    Assert.That(() => new OrigamiFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExternalTool")]
  public void ExternalOrigamiTool_GeneratedFixture_ExtractsOriginalAssembly() {
    var dotnet = ExecutablePackerToolCache.GetHostTool("dotnet");
    Assume.That(dotnet, Is.Not.Null, "A dotnet SDK with net472 targeting support is required to build the official Origami source.");

    var source = ExecutablePackerToolCache.GetOrigamiSource();
    Assume.That(source, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download Origami source.");

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      var project = Path.Combine(source!, "src", "Origami.csproj");
      var buildOutput = ExecutablePackerToolCache.Run(dotnet!, "build", project, "-c", "Release");
      var packer = Path.Combine(source!, "src", "bin", "Release", "net472", OperatingSystem.IsWindows() ? "Origami.exe" : "Origami");
      Assume.That(File.Exists(packer), Is.True, "Official Origami source did not build in this environment.\n" + buildOutput);

      var original = Path.Combine(tmp, "fixture.dll");
      File.Copy(typeof(PackerDetectorTests).Assembly.Location, original);
      var packOutput = ExecutablePackerToolCache.Run(packer, original, "-pes");
      var packed = Path.Combine(tmp, "fixture_origami.dll");
      Assert.That(File.Exists(packed), Is.True, packOutput);

      using var ms = File.OpenRead(packed);
      new OrigamiFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "original_assembly.bin")),
        Is.EqualTo(File.ReadAllBytes(original)).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void SilentPacker_Elf64XorSectionInsertion_RestoresTextAndEntryPoint() {
    var originalText = "original text body"u8.ToArray();
    var packed = BuildSilentPackerElf64(originalText, out var originalEntry);
    var descriptor = new SilentPackerFormatDescriptor();

    using var listStream = new MemoryStream(packed);
    var entries = descriptor.List(listStream, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("reconstructed/reconstructed.elf"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("decrypted_text.bin"));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var extractStream = new MemoryStream(packed);
      descriptor.Extract(extractStream, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "decrypted_text.bin")),
        Is.EqualTo(originalText).AsCollection);
      var reconstructed = File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "reconstructed.elf"));
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(reconstructed.AsSpan(0x18)), Is.EqualTo(originalEntry));
      Assert.That(reconstructed.AsSpan(0x100, originalText.Length).ToArray(),
        Is.EqualTo(originalText).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void SilentPacker_PlainElf_Throws() {
    var plainElf = new byte[128];
    plainElf[0] = 0x7F; plainElf[1] = (byte)'E'; plainElf[2] = (byte)'L'; plainElf[3] = (byte)'F';
    plainElf[4] = 2; plainElf[5] = 1;
    using var ms = new MemoryStream(plainElf);
    Assert.That(() => new SilentPackerFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExternalTool")]
  public void ExternalSilentPackerTool_GeneratedFixture_RestoresElfTextAndEntryPoint() {
    if (OperatingSystem.IsWindows())
      Assert.Ignore("Silent_Packer release asset is a Linux executable.");

    var tool = ExecutablePackerToolCache.GetSilentPacker();
    Assume.That(tool, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download Silent_Packer release asset.");

    var fixtureSource = File.Exists("/bin/true") ? "/bin/true" : File.Exists("/usr/bin/true") ? "/usr/bin/true" : null;
    Assume.That(fixtureSource, Is.Not.Null, "A system ELF fixture such as /bin/true is required.");

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      var fixture = Path.Combine(tmp, "fixture");
      var packed = Path.Combine(tmp, "fixture.packed");
      File.Copy(fixtureSource!, fixture);

      var original = File.ReadAllBytes(fixture);
      var originalInfo = ReadElf64TextInfo(original);
      Assume.That(originalInfo, Is.Not.Null, "The system fixture is not an ELF64 file with a .text section.");

      var packOutput = ExecutablePackerToolCache.Run(tool!, "-f", fixture, "-c", "xor", "-m", "section_insertion", "-o", packed);
      Assert.That(File.Exists(packed), Is.True, packOutput);

      using var ms = File.OpenRead(packed);
      new SilentPackerFormatDescriptor().Extract(ms, tmp, null, null);
      var reconstructed = File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "reconstructed.elf"));
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(reconstructed.AsSpan(0x18)),
        Is.EqualTo(originalInfo!.Value.EntryPoint));
      Assert.That(reconstructed.AsSpan(originalInfo.Value.TextOffset, originalInfo.Value.TextSize).ToArray(),
        Is.EqualTo(original.AsSpan(originalInfo.Value.TextOffset, originalInfo.Value.TextSize).ToArray()).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Huan_DecryptsEmbeddedPePayload() {
    var original = MinimalPe();
    var packed = BuildHuanWrapper(original);
    var descriptor = new HuanFormatDescriptor();

    using var listStream = new MemoryStream(packed);
    var entries = descriptor.List(listStream, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("reconstructed/reconstructed.exe"));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var extractStream = new MemoryStream(packed);
      descriptor.Extract(extractStream, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "reconstructed", "reconstructed.exe")),
        Is.EqualTo(original).AsCollection);
      Assert.That(File.Exists(Path.Combine(tmp, "encrypted_payload.bin")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Huan_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPe());
    Assert.That(() => new HuanFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExternalTool")]
  public void ExternalXorPackerSource_IncludedFixture_ExtractsOriginalExecutable() {
    var source = ExecutablePackerToolCache.GetXorPackerSource();
    Assume.That(source, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download the Xor_Packer source archive.");

    var packed = Path.Combine(source!, "xorPacker", "bin", "Debug", "packed_exe.exe");
    var original = Path.Combine(source!, "xorPacker", "bin", "Debug", "putty.exe");
    Assume.That(File.Exists(packed), Is.True, "Upstream Xor_Packer packed fixture is not present.");
    Assume.That(File.Exists(original), Is.True, "Upstream Xor_Packer original fixture is not present.");

    var handler = new XorPackerExecutablePackerHandler();
    var packedBytes = File.ReadAllBytes(packed);
    var detection = handler.Detect(packedBytes);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(packedBytes, detection), new());
    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/reconstructed.exe").Data,
        Is.EqualTo(File.ReadAllBytes(original)).AsCollection);
    });
  }

  [Test, Category("ExternalTool")]
  public void ExternalPyPePackerTool_GeneratedFixture_ExtractsOriginalExecutable() {
    var python = ExecutablePackerToolCache.GetPython();
    Assume.That(python, Is.Not.Null, "Set CWB_PYTHON, put python on PATH, or use the bundled portable Python.");
    var deps = ExecutablePackerToolCache.GetPyPePackerDependencies(python!);
    Assume.That(deps, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to install PyPePacker into the test cache.");
    var source = ExecutablePackerToolCache.GetPyPePackerSource();
    Assume.That(source, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download the PyPePacker source archive.");

    var fixtureSource = Path.Combine(TestContext.CurrentContext.WorkDirectory, "testhost.exe");
    if (!File.Exists(fixtureSource))
      fixtureSource = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cwb.exe");
    Assume.That(File.Exists(fixtureSource), Is.True, "A PE apphost fixture is required in the test output directory.");

    var tmp = Path.Combine(TestContext.CurrentContext.WorkDirectory, "third-party-tools", "exe-packers", "pypepacker", "external-test");
    if (Directory.Exists(tmp))
      Directory.Delete(tmp, recursive: true);
    Directory.CreateDirectory(tmp);
    try {
      var original = Path.Combine(tmp, "fixture.exe");
      File.Copy(fixtureSource, original);

      var script = Path.Combine(deps!, "PyPePacker.py");
      var code = $"import runpy, sys; sys.path.insert(0, r'{deps}'); sys.argv=['PyPePacker.py','fixture.exe','fixture-cmd','TestKey123']; runpy.run_path(r'{script}', run_name='__main__')";
      var packOutput = ExecutablePackerToolCache.RunInDirectory(python!, tmp, "-c", code);
      var packed = Path.Combine(tmp, "fixture_packed.exe");
      Assert.That(File.Exists(packed), Is.True, packOutput);

      var handler = new PyPePackerExecutablePackerHandler();
      var packedBytes = File.ReadAllBytes(packed);
      var detection = handler.Detect(packedBytes);
      Assert.That(detection.IsMatch, Is.True);

      var result = handler.Unpack(handler.Parse(packedBytes, detection), new());
      Assert.Multiple(() => {
        Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
        Assert.That(result.Artifacts.Single(a => a.Name == "compressed_payload.py").Data.Length, Is.GreaterThan(0));
        Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/reconstructed.exe").Data,
          Is.EqualTo(File.ReadAllBytes(original)).AsCollection);
      });
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  // hXOR-Packer and SimpleDpack external-tool round-trips live in
  // StaticUnpackerTargetsTests, which drive the validated static unpackers
  // (hXOR byte-exact rebuild; SimpleDpack detect+locate) against the real
  // published packer binaries.

  [Test, Category("HappyPath")]
  public void Petite_DetectsPetiteString() {
    var buf = MinimalPe();
    Encoding.ASCII.GetBytes("Petite").CopyTo(buf.AsSpan(0x200));
    using var ms = new MemoryStream(buf);
    var entries = new PetiteFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
  }

  [Test, Category("EdgeCase")]
  public void Petite_PlainPe_Throws() {
    using var ms = new MemoryStream(MinimalPe());
    Assert.That(() => new PetiteFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── Shrinkler ─────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Shrinkler_DetectsHunkMagicAndStubString() {
    var buf = new byte[1024];
    // AmigaOS HUNK_HEADER magic at offset 0.
    buf[0] = 0x00; buf[1] = 0x00; buf[2] = 0x03; buf[3] = 0xF3;
    Encoding.ASCII.GetBytes("Shrinkler").CopyTo(buf.AsSpan(0x80));

    using var ms = new MemoryStream(buf);
    var entries = new ShrinklerFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "hunk_header.bin"), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void Shrinkler_HunkMagicWithoutStub_Throws() {
    var buf = new byte[1024];
    buf[0] = 0x00; buf[1] = 0x00; buf[2] = 0x03; buf[3] = 0xF3;
    using var ms = new MemoryStream(buf);
    Assert.That(() => new ShrinklerFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Shrinkler_StubWithoutHunkMagic_Throws() {
    var buf = new byte[1024];
    Encoding.ASCII.GetBytes("Shrinkler").CopyTo(buf.AsSpan(0x80));
    using var ms = new MemoryStream(buf);
    Assert.That(() => new ShrinklerFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  private static byte[] BuildGzexeWrapper(byte[] original) {
    using var compressed = new MemoryStream();
    using (var gzip = new GzipStream(compressed, CompressionStreamMode.Compress, leaveOpen: true))
      gzip.Write(original);

    var header = Encoding.ASCII.GetBytes("#!/bin/sh\nskip=7\n# gzexe synthetic fixture\ngzip -cd \"$0\"\n");
    var result = new byte[header.Length + compressed.Length];
    header.CopyTo(result.AsSpan());
    compressed.ToArray().CopyTo(result.AsSpan(header.Length));
    return result;
  }

  private static byte[] BuildBzexeWrapper(byte[] original) {
    using var compressed = new MemoryStream();
    using (var bzip2 = new Bzip2Stream(compressed, CompressionStreamMode.Compress, leaveOpen: true))
      bzip2.Write(original);

    var header = Encoding.ASCII.GetBytes("#!/bin/sh\nskip=7\n# bzexe synthetic fixture\nbzip2 -cd \"$0\"\n");
    var result = new byte[header.Length + compressed.Length];
    header.CopyTo(result.AsSpan());
    compressed.ToArray().CopyTo(result.AsSpan(header.Length));
    return result;
  }

  private static byte[] BuildPapawWrapper(byte[] original) {
    var stub = new byte[0x200];
    stub[0] = 0x7F; stub[1] = (byte)'E'; stub[2] = (byte)'L'; stub[3] = (byte)'F';
    stub[4] = 2; stub[5] = 1;

    using var compressed = new MemoryStream();
    using (var xz = new XzStream(compressed, CompressionStreamMode.Compress, dictionarySize: 512 * 1024, checkType: 0, leaveOpen: true))
      xz.Write(original);
    var fullXz = compressed.ToArray();
    var obfuscated = fullXz.ToArray();
    obfuscated[0] = 0; obfuscated[1] = 0; obfuscated[2] = 0; obfuscated[3] = 0x08; obfuscated[4] = 0;
    obfuscated[^2] = 0; obfuscated[^1] = 0;

    var result = new byte[stub.Length + obfuscated.Length + 8];
    stub.CopyTo(result.AsSpan());
    obfuscated.CopyTo(result.AsSpan(stub.Length));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 8), (uint)original.Length);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), (uint)obfuscated.Length);
    return result;
  }

  private static byte[] BuildGoPackerWrapper(byte[] original) {
    var stub = new byte[0x200];
    stub[0] = 0x7F; stub[1] = (byte)'E'; stub[2] = (byte)'L'; stub[3] = (byte)'F';
    stub[4] = 2; stub[5] = 1;

    using var compressed = new MemoryStream();
    using (var zstd = new ZstdStream(compressed, CompressionStreamMode.Compress, leaveOpen: true))
      zstd.Write(original);

    var compressedBytes = compressed.ToArray();
    var result = new byte[stub.Length + compressedBytes.Length + 16];
    stub.CopyTo(result.AsSpan());
    compressedBytes.CopyTo(result.AsSpan(stub.Length));
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(stub.Length + compressedBytes.Length), (ulong)compressedBytes.Length);
    "LALALALA"u8.CopyTo(result.AsSpan(result.Length - 8));
    return result;
  }

  private static byte[] BuildOrigamiWrapper(byte[] original) {
    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int sectionOffset = optionalOffset + 0xE0;
    const int sectionRaw = 0x400;
    const uint sectionRva = 0x2000;
    const uint cliRva = 0x2000;
    const uint metadataRva = 0x2100;
    const uint methodRva = 0x2500;
    const uint payloadRva = 0x2600;
    const uint imageBase = 0x00400000;
    const string key = "0123456789ABCDEF0123456789ABCDEF";

    var compressed = DeflateCompressor.Compress(original, DeflateCompressionLevel.Default);
    var encrypted = compressed.ToArray();
    var keyBytes = Encoding.UTF8.GetBytes(key);
    for (var i = 0; i < encrypted.Length; i++)
      encrypted[i] ^= keyBytes[i % keyBytes.Length];

    var image = new byte[0x4000];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), 0xE0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), methodRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), imageBase);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 56), 0x4000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 60), 0x400);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 92), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 96 + 14 * 8), cliRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 96 + 14 * 8 + 4), 0x48);

    ".text\0\0\0"u8.CopyTo(image.AsSpan(sectionOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 8), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 12), sectionRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 16), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 20), sectionRaw);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 36), 0x60000020);

    var cliOffset = sectionRaw + (int)(cliRva - sectionRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cliOffset), 0x48);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cliOffset + 4), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cliOffset + 6), 5);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cliOffset + 8), metadataRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cliOffset + 12), 0x300);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cliOffset + 20), 0x06000001);

    WriteOrigamiMetadata(image, sectionRaw + (int)(metadataRva - sectionRva), key, methodRva);

    var methodOffset = sectionRaw + (int)(methodRva - sectionRva);
    image[methodOffset] = (byte)((14 << 2) | 0x2);
    image[methodOffset + 1] = 0x21;
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(methodOffset + 2), payloadRva);
    image[methodOffset + 10] = 0x20;
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(methodOffset + 11), encrypted.Length);

    encrypted.CopyTo(image.AsSpan(sectionRaw + (int)(payloadRva - sectionRva)));
    return image;
  }

  private static void WriteOrigamiMetadata(byte[] image, int offset, string key, uint methodRva) {
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), 0x424A5342);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 4), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 6), 1);
    var version = "v4.0.30319\0"u8.ToArray();
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(offset + 12), version.Length);
    version.CopyTo(image.AsSpan(offset + 16));
    var streamHeaderOffset = (offset + 16 + version.Length + 3) & ~3;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(streamHeaderOffset + 2), 2);

    var cursor = streamHeaderOffset + 4;
    WriteStreamHeader(image, ref cursor, 0x100, 0x80, "#~");
    WriteStreamHeader(image, ref cursor, 0x200, 0x80, "#Strings");

    var tables = offset + 0x100;
    image[tables + 4] = 2;
    image[tables + 5] = 0;
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(tables + 8), (1UL << 0) | (1UL << 6));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tables + 24), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tables + 28), 1);
    var module = tables + 32;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(module), 0);
    var method = module + 10;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(method), methodRva);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(method + 4), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(method + 6), 0x16);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(method + 8), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(method + 10), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(method + 12), 1);

    var strings = offset + 0x200;
    Encoding.UTF8.GetBytes(key).CopyTo(image.AsSpan(strings + 1));
  }

  private static void WriteStreamHeader(byte[] image, ref int cursor, int offset, int size, string name) {
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(cursor), offset);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(cursor + 4), size);
    cursor += 8;
    var nameBytes = Encoding.ASCII.GetBytes(name);
    nameBytes.CopyTo(image.AsSpan(cursor));
    cursor += nameBytes.Length;
    image[cursor++] = 0;
    cursor = (cursor + 3) & ~3;
  }

  private static byte[] BuildSilentPackerElf64(byte[] originalText, out ulong originalEntry) {
    const ulong key = 0x1122334455667788;
    const ulong textAddress = 0x401000;
    const int textOffset = 0x100;
    const ulong loaderAddress = 0x402000;
    const int loaderOffset = 0x200;
    const int loaderSize = 0x80;
    const int sectionHeaderOffset = 0x600;
    originalEntry = textAddress;

    var image = new byte[0x800];
    image[0] = 0x7F; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F';
    image[4] = 2; image[5] = 1; image[6] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x10), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x12), 0x3E);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x14), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x18), loaderAddress);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x28), sectionHeaderOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x34), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3A), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3C), 4);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3E), 3);

    var encrypted = XorSilentPacker64(originalText, key);
    encrypted.CopyTo(image.AsSpan(textOffset));

    var jumpBase = loaderAddress + loaderSize - 32;
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(loaderOffset + loaderSize - 36), checked((int)((long)originalEntry - (long)jumpBase)));
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(loaderOffset + loaderSize - 32), key);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(loaderOffset + loaderSize - 24), textAddress);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(loaderOffset + loaderSize - 16), (ulong)originalText.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(loaderOffset + loaderSize - 8), loaderAddress);

    var strings = "\0.text\0.dec\0.shstrtab\0"u8.ToArray();
    strings.CopyTo(image.AsSpan(0x500));
    WriteElf64Section(image, sectionHeaderOffset + 64, 1, textAddress, textOffset, originalText.Length);
    WriteElf64Section(image, sectionHeaderOffset + 128, 7, loaderAddress, loaderOffset, loaderSize);
    WriteElf64Section(image, sectionHeaderOffset + 192, 12, 0, 0x500, strings.Length);
    return image;
  }

  private static byte[] XorSilentPacker64(ReadOnlySpan<byte> data, ulong key) {
    var result = data.ToArray();
    var rolling = key;
    for (var i = 0; i < result.Length; i++) {
      result[i] ^= (byte)rolling;
      rolling = (rolling >> 8) | (rolling << 56);
    }
    return result;
  }

  private static void WriteElf64Section(byte[] image, int offset, uint nameIndex, ulong address, int fileOffset, int size) {
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), nameIndex);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 4), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 8), 0x6);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 16), address);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 24), (ulong)fileOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 32), (ulong)size);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 48), 16);
  }

  private static (ulong EntryPoint, int TextOffset, int TextSize)? ReadElf64TextInfo(byte[] image) {
    if (image.Length < 0x40 || image[0] != 0x7F || image[1] != 'E' || image[2] != 'L' || image[3] != 'F' ||
        image[4] != 2 || image[5] != 1)
      return null;
    var entry = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x18));
    var shoff64 = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x28));
    var shentsize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3A));
    var shnum = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3C));
    var shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3E));
    if (shoff64 > int.MaxValue || shentsize < 64 || shnum == 0 || shstrndx >= shnum)
      return null;
    var shoff = (int)shoff64;
    if (shoff + shnum * shentsize > image.Length)
      return null;
    var stringHeader = shoff + shstrndx * shentsize;
    var stringsOffset = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(stringHeader + 24));
    var stringsSize = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(stringHeader + 32));
    if (stringsOffset < 0 || stringsSize < 0 || stringsOffset + stringsSize > image.Length)
      return null;

    for (var i = 0; i < shnum; i++) {
      var section = shoff + i * shentsize;
      var nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(section));
      if (ReadElfString(image, stringsOffset, stringsSize, nameIndex) != ".text")
        continue;
      var offset = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(section + 24));
      var size = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(section + 32));
      return (entry, offset, size);
    }

    return null;
  }

  private static string ReadElfString(byte[] image, int offset, int size, uint index) {
    if (index >= size)
      return "";
    var start = offset + (int)index;
    var end = start;
    while (end < offset + size && image[end] != 0)
      end++;
    return Encoding.ASCII.GetString(image, start, end - start);
  }

  private static byte[] BuildHuanWrapper(byte[] original) {
    var key = "0123456789ABCDEF"u8.ToArray();
    var iv = "FEDCBA9876543210"u8.ToArray();
    var encryptedLength = ((original.Length + 15) / 16) * 16;
    var padded = new byte[encryptedLength];
    original.CopyTo(padded.AsSpan());
    var encrypted = AesCryptor.EncryptCbcNoPaddingAny(padded, key, iv);
    var payloadLength = 40 + encrypted.Length;
    var rawSize = (payloadLength + 0x1FF) & ~0x1FF;

    var image = new byte[0x400 + rawSize];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x80);
    "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84), 0x8664);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x86), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x94), 0xF0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x98), 0x20B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0xB8), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0xBC), 0x200);

    var section = 0x80 + 24 + 0xF0;
    ".huan\0\0\0"u8.CopyTo(image.AsSpan(section));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 8), (uint)payloadLength);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 16), (uint)rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 20), 0x400);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 36), 0x40000040);

    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x400), original.Length);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x404), encrypted.Length);
    key.CopyTo(image.AsSpan(0x408));
    iv.CopyTo(image.AsSpan(0x418));
    encrypted.CopyTo(image.AsSpan(0x428));
    return image;
  }
}
