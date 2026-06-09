using System.IO.Compression;
using Compression.Registry;
using FileFormat.Numpy;

namespace Compression.Tests.Numpy;

[TestFixture]
public class NumpyWriterTests {

  // ─── NPY direct writer ──────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NpyWriter_RoundTripsThroughReader() {
    var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

    using var ms = new MemoryStream();
    NpyWriter.Write(ms, payload);

    var arr = NpyReader.Read(ms.ToArray());
    Assert.That(arr.MajorVersion, Is.EqualTo(1));
    Assert.That(arr.Dtype, Is.EqualTo("|u1"));
    Assert.That(arr.Shape, Is.EqualTo("(8,)"));
    Assert.That(arr.FortranOrder, Is.False);
    Assert.That(arr.ArrayBytes, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void NpyWriter_HeaderIsMultipleOf64() {
    using var ms = new MemoryStream();
    NpyWriter.Write(ms, new byte[] { 0xAA, 0xBB });
    var arr = NpyReader.Read(ms.ToArray());
    Assert.That((arr.HeaderBytes.Length) % 64, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void NpyWriter_RespectsDtypeAndShapeOverrides() {
    var payload = new byte[24];
    using var ms = new MemoryStream();
    NpyWriter.Write(ms, payload, dtype: "<f4", shape: "(2, 3)");
    var arr = NpyReader.Read(ms.ToArray());
    Assert.That(arr.Dtype, Is.EqualTo("<f4"));
    Assert.That(arr.Shape, Is.EqualTo("(2, 3)"));
  }

  [Test, Category("HappyPath")]
  public void NpyWriter_FortranOrderEncoded() {
    var payload = new byte[8];
    using var ms = new MemoryStream();
    NpyWriter.Write(ms, payload, fortranOrder: true);
    var arr = NpyReader.Read(ms.ToArray());
    Assert.That(arr.FortranOrder, Is.True);
  }

  [Test, Category("EdgeCase")]
  public void NpyWriter_EmptyPayload_WritesValidNpy() {
    using var ms = new MemoryStream();
    NpyWriter.Write(ms, []);
    var arr = NpyReader.Read(ms.ToArray());
    Assert.That(arr.ArrayBytes, Is.Empty);
    Assert.That(arr.Shape, Is.EqualTo("(0,)"));
  }

  // ─── NPY descriptor (IArchiveCreatable) ────────────────────────────────

  [Test, Category("HappyPath")]
  public void NpyDescriptor_Capabilities_IncludeWormCreate() {
    var d = new NpyFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NpyDescriptor_CreateFromRawBytes_ProducesReadableNpy() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("array.bin", new byte[] { 9, 8, 7, 6, 5 }),
    };

    using var output = new MemoryStream();
    new NpyFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    var arr = NpyReader.Read(output.ToArray());
    Assert.That(arr.ArrayBytes, Is.EqualTo(new byte[] { 9, 8, 7, 6, 5 }));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NpyDescriptor_CreateFromExistingNpy_PassesThroughVerbatim() {
    // Build a real NPY first.
    using var npyMs = new MemoryStream();
    NpyWriter.Write(npyMs, new byte[] { 0x01, 0x02, 0x03 });
    var npyBytes = npyMs.ToArray();

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("existing.npy", npyBytes),
    };

    using var output = new MemoryStream();
    new NpyFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(npyBytes));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NpyDescriptor_HeaderBinPlusArrayBin_ReassemblesCleanly() {
    // Extract from a known NPY first, then feed the parts back in.
    using var src = new MemoryStream();
    NpyWriter.Write(src, new byte[] { 0xAA, 0xBB, 0xCC });
    var arr = NpyReader.Read(src.ToArray());

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("header.bin", arr.HeaderBytes),
      ArchiveInputInfo.InMemory("array.bin", arr.ArrayBytes),
    };

    using var output = new MemoryStream();
    new NpyFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(src.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void NpyDescriptor_DtypeOverride_AppliedToHeader() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("data", new byte[16]),
    };
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["npy_dtype"] = "<f4",
        ["npy_shape"] = "(4,)",
      },
    };

    using var output = new MemoryStream();
    new NpyFormatDescriptor().Create(output, inputs, options);

    var arr = NpyReader.Read(output.ToArray());
    Assert.That(arr.Dtype, Is.EqualTo("<f4"));
    Assert.That(arr.Shape, Is.EqualTo("(4,)"));
  }

  // ─── NPZ descriptor ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void NpzDescriptor_Capabilities_IncludeWormCreate() {
    var d = new NpzFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NpzDescriptor_CreateFromRawBytes_WrapsEachAsNpy() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("arr_0", new byte[] { 1, 2, 3, 4 }),
      ArchiveInputInfo.InMemory("arr_1", new byte[] { 9, 8 }),
    };

    using var output = new MemoryStream();
    new NpzFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    using var zip = new ZipArchive(output, ZipArchiveMode.Read);
    var names = zip.Entries.Select(e => e.FullName).ToList();
    Assert.That(names, Does.Contain("arr_0.npy"));
    Assert.That(names, Does.Contain("arr_1.npy"));

    using var s = zip.GetEntry("arr_0.npy")!.Open();
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    var parsed = NpyReader.Read(ms.ToArray());
    Assert.That(parsed.ArrayBytes, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NpzDescriptor_PreservesExistingNpyEntries() {
    using var npyMs = new MemoryStream();
    NpyWriter.Write(npyMs, new byte[] { 0xAA, 0xBB }, dtype: "|u1", shape: "(2,)");
    var npyBytes = npyMs.ToArray();

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("a.npy", npyBytes),
    };

    using var output = new MemoryStream();
    new NpzFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    using var zip = new ZipArchive(output, ZipArchiveMode.Read);
    using var s = zip.GetEntry("a.npy")!.Open();
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    Assert.That(ms.ToArray(), Is.EqualTo(npyBytes));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NpzDescriptor_ListSurfacesNpyEntries_AfterRoundTrip() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("alpha", new byte[] { 1, 2, 3 }),
      ArchiveInputInfo.InMemory("beta",  new byte[] { 4, 5 }),
    };

    using var output = new MemoryStream();
    new NpzFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var entries = new NpzFormatDescriptor().List(output, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("alpha.npy"));
    Assert.That(names, Does.Contain("beta.npy"));
  }

  [Test, Category("EdgeCase")]
  public void NpzDescriptor_EmptyInputs_ProducesEmptyZip() {
    using var output = new MemoryStream();
    new NpzFormatDescriptor().Create(output, new List<ArchiveInputInfo>(), new FormatCreateOptions());

    output.Position = 0;
    using var zip = new ZipArchive(output, ZipArchiveMode.Read);
    Assert.That(zip.Entries, Is.Empty);
  }
}
