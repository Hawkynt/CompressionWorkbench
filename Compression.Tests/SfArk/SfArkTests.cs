using System.Buffers.Binary;
using System.Text;
using Compression.Core.Streams.SfArk;
using Compression.Registry;

namespace Compression.Tests.SfArk;

/// <summary>Builds small SoundFont-shaped RIFF files around chosen sample data.</summary>
internal static class Sf2Builder {
  private static byte[] Chunk(string id, ReadOnlySpan<byte> body) {
    var c = new byte[8 + body.Length + (body.Length & 1)];
    Encoding.ASCII.GetBytes(id).CopyTo(c, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(4), (uint)body.Length);
    body.CopyTo(c.AsSpan(8));
    return c;
  }

  private static byte[] List(string type, params byte[][] chunks)
    => Chunk("LIST", [.. Encoding.ASCII.GetBytes(type), .. chunks.SelectMany(c => c)]);

  public static byte[] Build(short[] samples) {
    var smpl = new byte[2 * samples.Length];
    for (var i = 0; i < samples.Length; ++i) BinaryPrimitives.WriteInt16LittleEndian(smpl.AsSpan(2 * i), samples[i]);
    var info = List("INFO", Chunk("ifil", [2, 0, 1, 0]), Chunk("INAM", "Test bank\0"u8));
    var sdta = List("sdta", Chunk("smpl", smpl));
    var pdta = List("pdta", Chunk("phdr", new byte[38]), Chunk("inst", new byte[22]));
    return Chunk("RIFF", [.. "sfbk"u8, .. info, .. sdta, .. pdta]);
  }

  public static short[] Tone(int n, int seed) {
    var rnd = new Random(seed);
    var s = new short[n];
    for (var i = 0; i < n; ++i)
      s[i] = (short)(9000 * Math.Sin(i * 0.031) + 3000 * Math.Sin(i * 0.17) + rnd.Next(-200, 200));
    return s;
  }
}

[TestFixture]
public class SfArkTests {

  public static IEnumerable<TestCaseData> SampleSets() {
    yield return new TestCaseData(Array.Empty<short>()).SetName("NoSamples");
    yield return new TestCaseData(new short[] { 1234 }).SetName("OneSample");
    yield return new TestCaseData(Sf2Builder.Tone(1023, 1)).SetName("JustUnderOneBlock");
    yield return new TestCaseData(Sf2Builder.Tone(1024, 2)).SetName("ExactlyOneBlock");
    yield return new TestCaseData(Sf2Builder.Tone(5000, 3)).SetName("SeveralBlocks");
    yield return new TestCaseData(new short[3000]).SetName("Silence");
    yield return new TestCaseData(Enumerable.Range(0, 700).Select(i => (short)(i % 3 == 0 ? -1 : 0)).ToArray()).SetName("ZeroAndMinusOne");
    yield return new TestCaseData(Enumerable.Range(0, 900).Select(i => (short)(i % 2 == 0 ? short.MaxValue : short.MinValue)).ToArray()).SetName("FullScaleSquare");
    var rnd = new Random(9);
    yield return new TestCaseData(Enumerable.Range(0, 2100).Select(_ => (short)rnd.Next(short.MinValue, short.MaxValue + 1)).ToArray()).SetName("WhiteNoise");
  }

  [TestCaseSource(nameof(SampleSets)), Category("RoundTrip")]
  public void SoundFont_RoundTrips(short[] samples) {
    var sf2 = Sf2Builder.Build(samples);
    var packed = SfArkCodec.Encode(sf2, "bank.sf2");
    var decoded = SfArkCodec.Decode(packed, out var info);
    Assert.Multiple(() => {
      Assert.That(decoded, Is.EqualTo(sf2));
      Assert.That(info.FileName, Is.EqualTo("bank.sf2"));
      Assert.That(info.Method, Is.EqualTo(5));
    });
  }

  public static IEnumerable<TestCaseData> OtherData() {
    yield return new TestCaseData(Array.Empty<byte>()).SetName("Empty");
    yield return new TestCaseData("not a soundfont"u8.ToArray()).SetName("Text");
    var big = new byte[200_000];
    new Random(5).NextBytes(big);
    yield return new TestCaseData(big).SetName("LargerThanOneZlibBlock");
    yield return new TestCaseData("RIFF\x04\0\0\0sfbk"u8.ToArray()).SetName("SoundFontWithoutSamples");
  }

  [TestCaseSource(nameof(OtherData)), Category("RoundTrip")]
  public void AnyFile_RoundTrips(byte[] data)
    => Assert.That(SfArkCodec.Decode(SfArkCodec.Encode(data)), Is.EqualTo(data));

  [Test, Category("HappyPath")]
  public void Encode_ShrinksTonalAudio() {
    var sf2 = Sf2Builder.Build(Sf2Builder.Tone(40_000, 4));
    Assert.That(SfArkCodec.Encode(sf2).Length, Is.LessThan(sf2.Length * 3 / 4));
  }

  [Test, Category("Spec")]
  public void Header_HasSignatureMethodAndOffsets() {
    var sf2 = Sf2Builder.Build(Sf2Builder.Tone(100, 6));
    var packed = SfArkCodec.Encode(sf2, "x.sf2");
    var smpl = sf2.AsSpan().IndexOf("smpl"u8) + 8;
    Assert.Multiple(() => {
      Assert.That(Encoding.ASCII.GetString(packed, 26, 5), Is.EqualTo("sfArk"));
      Assert.That(packed[31], Is.EqualTo(5));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(4)), Is.EqualTo(sf2.Length));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(8)), Is.EqualTo(packed.Length));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(34)), Is.EqualTo(smpl));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(38)), Is.EqualTo(smpl + 200));
    });
  }

  [Test, Category("HappyPath")]
  public void Header_IsFoundBehindAStub() {
    var sf2 = Sf2Builder.Build(Sf2Builder.Tone(300, 7));
    var stub = Enumerable.Repeat((byte)0x90, 5000).Concat("sfArk"u8.ToArray()).ToArray();
    Assert.That(SfArkCodec.Decode([.. stub, .. SfArkCodec.Encode(sf2)]), Is.EqualTo(sf2));
  }

  [Test, Category("Exception")]
  public void Decode_NoSignature_Throws()
    => Assert.Throws<InvalidDataException>(() => SfArkCodec.Decode(new byte[100]));

  [Test, Category("Exception")]
  public void Decode_DamagedHeader_Throws() {
    var packed = SfArkCodec.Encode(Sf2Builder.Build(Sf2Builder.Tone(100, 8)));
    packed[4] ^= 1;
    Assert.Throws<InvalidDataException>(() => SfArkCodec.Decode(packed));
  }

  [Test, Category("Exception")]
  public void Decode_DamagedBody_FailsTheFileCheck() {
    var packed = SfArkCodec.Encode(Sf2Builder.Build(Sf2Builder.Tone(3000, 9)));
    packed[^40] ^= 0x10;
    Assert.Throws<InvalidDataException>(() => SfArkCodec.Decode(packed));
  }

  [Test, Category("Exception")]
  public void Decode_Truncated_Throws() {
    var packed = SfArkCodec.Encode(Sf2Builder.Build(Sf2Builder.Tone(3000, 10)));
    Assert.Throws<InvalidDataException>(() => SfArkCodec.Decode(packed.AsSpan(0, packed.Length / 2)));
  }

  [Test, Category("Exception")]
  public void Decode_VersionOne_IsRefused() {
    var v1 = new byte[64];
    "sfArk"u8.CopyTo(v1.AsSpan(26));
    v1[31] = 2;
    Assert.Throws<NotSupportedException>(() => SfArkCodec.Decode(v1));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IsRegisteredAndDetected() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var descriptor = FormatRegistry.GetById("SfArk");
    Assert.That(descriptor, Is.Not.Null);
    Assert.That(BuildingBlockRegistry.GetById("BB_SfArk"), Is.Not.Null);
  }
}

/// <summary>
/// The sfarkxtc reference decoder (sfArkLib): published sfArk files (Standard and Max methods)
/// must decode as sfarkxtc decodes them, and sfarkxtc must decode the files we write.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class SfArkReferenceTests {
  private const string Dir = "/tmp/cwb-sfark";
  private static readonly (string Name, string Url, int Method)[] Samples = [
    ("Chaos_V20.sfArk", "http://www.synthfont.com/SoundFonts/Chaos_V20.sfArk", 6),
    ("WST25FStein.sfArk", "https://www.lycanroc.net/media/downloads/soundfonts/WST25FStein.sfArk", 7),
  ];

  private static readonly Lazy<string?> Setup = new(() => {
    if (!FsInteropToolbox.WslAvailable) return "WSL/Linux shell unavailable.";
    if (!FsInteropToolbox.WslHasTool("sfarkxtc")) return "'sfarkxtc' is required (apt install sfarkxtc).";
    if (!FsInteropToolbox.WslHasTool("curl")) return "'curl' is required.";
    var fetch = string.Join("; ", Samples.Select(s => $"test -s {s.Name} || curl -sSfL --max-time 300 -o {s.Name} '{s.Url}'"));
    var r = FsInteropToolbox.RunWsl($"set -e; mkdir -p {Dir}; cd {Dir}; {fetch}");
    return r.ExitCode == 0 ? null : $"downloading the sfArk samples failed (offline?):\n{r.StdOut}\n{r.StdErr}";
  });

  // sfarkxtc resolves the output name against the input file's directory.
  private static string NewDir() {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_sfark_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    return dir;
  }

  [TestCaseSource(nameof(SampleNames))]
  public void PublishedFile_DecodesLikeSfarkxtc(string name) {
    if (Setup.Value is { } error) Assert.Ignore(error);
    var dir = NewDir();
    try {
      var packed = Path.Combine(dir, name);
      var reference = Path.Combine(dir, "reference.sf2");
      var r = FsInteropToolbox.RunWsl($"cp {Dir}/{name} {FsInteropToolbox.WinToWsl(packed)} && sfarkxtc {FsInteropToolbox.WinToWsl(packed)} reference.sf2");
      Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);
      var decoded = SfArkCodec.Decode(File.ReadAllBytes(packed), out var info);
      Assert.That(info.Method, Is.EqualTo(Samples.First(s => s.Name == name).Method));
      Assert.That(decoded, Is.EqualTo(File.ReadAllBytes(reference)));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  private static IEnumerable<string> SampleNames() => Samples.Select(s => s.Name);

  [Test]
  public void OurFile_IsDecodedBySfarkxtc() {
    if (Setup.Value is { } error) Assert.Ignore(error);
    var dir = NewDir();
    try {
      var sf2 = Sf2Builder.Build([.. Sf2Builder.Tone(9000, 11), .. new short[700], .. Enumerable.Range(0, 333).Select(i => (short)(i * 197))]);
      var packed = Path.Combine(dir, "ours.sfArk");
      var output = Path.Combine(dir, "ours.sf2");
      File.WriteAllBytes(packed, SfArkCodec.Encode(sf2, "ours.sf2"));
      var r = FsInteropToolbox.RunWsl($"sfarkxtc {FsInteropToolbox.WinToWsl(packed)} ours.sf2");
      Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);
      Assert.That(File.ReadAllBytes(output), Is.EqualTo(sf2));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void PublishedFile_SurvivesOurRepacking() {
    if (Setup.Value is { } error) Assert.Ignore(error);
    var dir = NewDir();
    try {
      var name = Samples[1].Name;
      var packed = Path.Combine(dir, name);
      var r = FsInteropToolbox.RunWsl($"cp {Dir}/{name} {FsInteropToolbox.WinToWsl(packed)}");
      Assert.That(r.ExitCode, Is.EqualTo(0), r.StdErr);
      var sf2 = SfArkCodec.Decode(File.ReadAllBytes(packed));
      var ours = Path.Combine(dir, "ours.sfArk");
      var output = Path.Combine(dir, "ours.sf2");
      File.WriteAllBytes(ours, SfArkCodec.Encode(sf2, "ours.sf2"));
      r = FsInteropToolbox.RunWsl($"sfarkxtc {FsInteropToolbox.WinToWsl(ours)} ours.sf2");
      Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);
      Assert.That(File.ReadAllBytes(output), Is.EqualTo(sf2));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
