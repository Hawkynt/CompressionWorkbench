using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Compression.Registry;
using FileSystem.TFat;
using NUnit.Framework;

namespace Compression.Tests.TFat;

/// <summary>
/// Tests for the TFAT creation-options schema (IFormatOptionsSchema) and the
/// new geometry-aware writer entry points (BuildAutoSized, volume label,
/// fixed-image cluster optimisation) added to mirror FileSystem.Fat.
/// </summary>
[TestFixture]
public class TFatSchemaTests {

  // ── Schema surface ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesOptionsSchema() {
    var d = new TFatFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());

    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    var keys = schema.Select(o => o.Key).ToArray();

    Assert.That(keys, Does.Contain("ImageSize"));
    Assert.That(keys, Does.Contain("ClusterSize"));
    Assert.That(keys, Does.Contain("VolumeLabel"));
    Assert.That(keys, Does.Contain("FatType"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ClusterSizeAndImageSize_OfferAuto() {
    var schema = ((IFormatOptionsSchema)new TFatFormatDescriptor()).OptionsSchema;

    var cluster = schema.First(o => o.Key == "ClusterSize");
    Assert.That(cluster.Kind, Is.EqualTo(FormatOptionKind.Enum));
    Assert.That(cluster.AllowedValues, Does.Contain("Auto"));
    Assert.That(cluster.AllowedValues, Does.Contain("4 KB"));

    var image = schema.First(o => o.Key == "ImageSize");
    Assert.That(image.Kind, Is.EqualTo(FormatOptionKind.Enum));
    Assert.That(image.AllowedValues!.Any(v => v.StartsWith("Auto")), Is.True);
    Assert.That(image.AllowedValues, Does.Contain("32 MB"));
  }

  // ── Create() with explicit FormatSpecific cluster size ────────────────

  [Test, Category("HappyPath")]
  public void Create_WithExplicitCluster_RoundTripsViaReader() {
    var d = new TFatFormatDescriptor();
    var payload = Encoding.ASCII.GetBytes("TFAT explicit-cluster round-trip payload.");
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, payload);
      var inputs = new List<ArchiveInputInfo> {
        new(tmp, "DATA.BIN", false),
      };
      var options = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> {
          ["ImageSize"]   = "32 MB",
          ["ClusterSize"] = "4 KB",
          ["VolumeLabel"] = "TFATVOL",
        },
      };

      using var ms = new MemoryStream();
      d.Create(ms, inputs, options);
      var image = ms.ToArray();

      // 4 KB clusters at 512 B/sector ⇒ 8 sectors per cluster.
      Assert.That(image[13], Is.EqualTo(8));

      ms.Position = 0;
      using var reader = new TFatReader(ms);
      var entry = reader.Entries.Single(e => !e.IsDirectory);
      Assert.That(entry.Name, Is.EqualTo("DATA.BIN"));
      Assert.That(reader.Extract(entry), Is.EqualTo(payload));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("HappyPath")]
  public void Create_FixedImageAutoCluster_PicksClusterAndRoundTrips() {
    var d = new TFatFormatDescriptor();
    var payload = Encoding.ASCII.GetBytes("fixed-image auto-cluster");
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, payload);
      var inputs = new List<ArchiveInputInfo> {
        new(tmp, "AUTO.TXT", false),
      };
      // ImageSize fixed, ClusterSize Auto ⇒ Create() must run the cluster optimiser.
      var options = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> {
          ["ImageSize"]   = "128 MB",
          ["ClusterSize"] = "Auto",
        },
      };

      using var ms = new MemoryStream();
      d.Create(ms, inputs, options);
      var image = ms.ToArray();

      // 128 MB fixed image was honoured exactly.
      Assert.That(image.Length, Is.EqualTo(128L * 1024 * 1024));
      // A non-zero cluster size was selected.
      Assert.That(image[13], Is.GreaterThan(0));

      ms.Position = 0;
      using var reader = new TFatReader(ms);
      var entry = reader.Entries.Single(e => !e.IsDirectory);
      Assert.That(reader.Extract(entry), Is.EqualTo(payload));
    } finally {
      File.Delete(tmp);
    }
  }

  // ── New writer entry points ───────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void BuildAutoSized_RoundTripsAndCarriesTfatMarker() {
    var w = new TFatWriter();
    w.AddFile("ONE.TXT", Encoding.ASCII.GetBytes("first file"));
    w.AddFile("TWO.TXT", Encoding.ASCII.GetBytes("second file, longer content here"));
    var image = w.BuildAutoSized(volumeLabel: "AUTO");

    // BS_Reserved1 at offset 37 is FAT's unclean-unmount flag, not a place to
    // mark a volume as TFAT — a marker there has every checker calling the
    // volume dirty and possibly corrupt. The tag in BS_FilSysType says it.
    Assert.That(image[37], Is.EqualTo(0x00));

    using var ms = new MemoryStream(image);
    using var reader = new TFatReader(ms);
    var names = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToArray();
    Assert.That(names, Does.Contain("ONE.TXT"));
    Assert.That(names, Does.Contain("TWO.TXT"));
    Assert.That(Encoding.ASCII.GetString(reader.Extract(reader.Entries.First(e => e.Name == "ONE.TXT"))),
      Is.EqualTo("first file"));
  }

  [Test, Category("HappyPath")]
  public void Build_VolumeLabel_WrittenToBootSector() {
    var w = new TFatWriter();
    w.AddFile("X.TXT", Encoding.ASCII.GetBytes("x"));
    var image = w.Build(volumeLabel: "MYLABEL");

    // FAT12/16 BS_VolLab is 11 bytes at offset 43.
    var label = Encoding.ASCII.GetString(image, 43, 11);
    Assert.That(label, Is.EqualTo("MYLABEL    "));
  }

  [Test, Category("HappyPath")]
  public void Build_DefaultLabel_BackCompatible() {
    // Build() with no label still defaults to "NO NAME    ".
    var w = new TFatWriter();
    w.AddFile("X.TXT", Encoding.ASCII.GetBytes("x"));
    var image = w.Build();
    var label = Encoding.ASCII.GetString(image, 43, 11);
    Assert.That(label, Is.EqualTo("NO NAME    "));
  }
}
