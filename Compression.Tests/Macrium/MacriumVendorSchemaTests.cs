using System.Reflection;
using System.Text;
using System.Text.Json;
using Compression.Registry;
using FileFormat.Macrium;

namespace Compression.Tests.Macrium;

/// <summary>
/// Checks the <c>$JSON</c> metadata our Reflect X writer emits against the vendor's own
/// <c>schema/required_schema.json</c> from <see href="https://github.com/macrium/mrimgx_file_layout"/>
/// (MIT, revision 93db24aaa9236a19976d4d3d6007eabd10dbc8d9, embedded next to its LICENSE.txt).
/// The schema is the vendor's statement of what a Reflect X reader may rely on, so it is the
/// independent oracle for the metadata half of the writer — a self round-trip cannot catch a
/// field our own reader simply never looks at.
/// </summary>
[TestFixture]
public sealed class MacriumVendorSchemaTests {

  private static JsonElement LoadSchema() {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MacriumVendor.required_schema.json")
      ?? throw new InvalidOperationException("The embedded Macrium vendor schema is missing.");
    using var doc = JsonDocument.Parse(stream);
    return doc.RootElement.Clone();
  }

  private static string CreateAndReadJson(FormatCreateOptions options, byte[] disk) {
    using var image = new MemoryStream();
    new MacriumFormatDescriptor().Create(image, [ArchiveInputInfo.InMemory("disk.raw", disk)], options);
    image.Position = 0;
    using var reader = new MacriumReader(image, options.Password);
    return Encoding.UTF8.GetString(reader.Entries.First(e => e.Name == "metadata.json").Data);
  }

  private static byte[] Disk(int size) {
    var bytes = new byte[size];
    new Random(size).NextBytes(bytes);
    return bytes;
  }

  public static IEnumerable<TestCaseData> WriterConfigurations() {
    yield return new TestCaseData(new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["Compression"] = "None" } })
      .SetName("GivenStoredBlocks_WhenCreating_ThenJsonSatisfiesTheVendorSchema");
    yield return new TestCaseData(new FormatCreateOptions())
      .SetName("GivenDefaultMediumZstd_WhenCreating_ThenJsonSatisfiesTheVendorSchema");
    yield return new TestCaseData(new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["Compression"] = "High" } })
      .SetName("GivenHighZstd_WhenCreating_ThenJsonSatisfiesTheVendorSchema");
    yield return new TestCaseData(new FormatCreateOptions { Password = "pw", MethodName = "aes-128-cbc", FormatSpecific = new Dictionary<string, string> { ["Pbkdf2Iterations"] = "1000" } })
      .SetName("GivenAes128_WhenCreating_ThenJsonSatisfiesTheVendorSchema");
    yield return new TestCaseData(new FormatCreateOptions { Password = "pw", FormatSpecific = new Dictionary<string, string> { ["Pbkdf2Iterations"] = "1000", ["ReservedSectorsLength"] = "1024" } })
      .SetName("GivenAes256WithTrack0_WhenCreating_ThenJsonSatisfiesTheVendorSchema");
  }

  [TestCaseSource(nameof(WriterConfigurations))]
  public void WriterJson_SatisfiesVendorRequiredSchema(FormatCreateOptions options) {
    var json = CreateAndReadJson(options, Disk(8192));
    using var doc = JsonDocument.Parse(json);
    var violations = new List<string>();
    Validate(LoadSchema(), doc.RootElement, "$", violations);
    Assert.That(violations, Is.Empty, "Writer $JSON breaks the vendor's required_schema.json:\n" + string.Join("\n", violations) + "\n\n" + json);
  }

  [TestCase(1, "medium", TestName = "GivenLowestZstdEffort_WhenWriting_ThenTheTierIsTheVendorsMediumNotAnInventedLow")]
  [TestCase(6, "medium", TestName = "GivenEffortBelowHighThreshold_WhenWriting_ThenTheTierIsMedium")]
  [TestCase(7, "high", TestName = "GivenEffortAtHighThreshold_WhenWriting_ThenTheTierIsHigh")]
  [TestCase(9, "high", TestName = "GivenHighestZstdEffort_WhenWriting_ThenTheTierIsHigh")]
  public void Writer_CompressionLevel_MapsOntoVendorTiers(int level, string expectedTier) {
    var bytes = new MacriumWriter { CompressDataBlocks = true, CompressionLevel = level }.Build(Disk(4096));
    using var reader = new MacriumReader(new MemoryStream(bytes));
    using var doc = JsonDocument.Parse(reader.Entries.First(e => e.Name == "metadata.json").Data);
    Assert.That(doc.RootElement.GetProperty("_compression").GetProperty("compression_level").GetString(), Is.EqualTo(expectedTier));
  }

  [TestCase(0, TestName = "GivenZstdEffortZero_WhenWriting_ThenItIsRejected")]
  [TestCase(10, TestName = "GivenZstdEffortTen_WhenWriting_ThenItIsRejected")]
  public void Writer_CompressionLevel_OutsideOneToNine_IsRejected(int level)
    => Assert.That(() => new MacriumWriter { CompressDataBlocks = true, CompressionLevel = level }.Build(Disk(512)),
      Throws.InvalidOperationException);

  /// <summary>Minimal JSON-Schema walker for the keywords the vendor schema uses: type, required, enum, properties, items.</summary>
  private static void Validate(JsonElement schema, JsonElement value, string path, List<string> violations) {
    if (schema.TryGetProperty("type", out var type) && !MatchesType(type.GetString()!, value)) {
      violations.Add($"{path}: expected {type.GetString()}, found {value.ValueKind}");
      return;
    }
    if (schema.TryGetProperty("enum", out var allowed)
        && !allowed.EnumerateArray().Any(a => JsonElement.DeepEquals(a, value)))
      violations.Add($"{path}: '{value}' is not one of [{string.Join(", ", allowed.EnumerateArray())}]");
    if (value.ValueKind == JsonValueKind.Object) {
      if (schema.TryGetProperty("required", out var required))
        foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
          if (!value.TryGetProperty(name, out _))
            violations.Add($"{path}: missing required '{name}'");
      if (schema.TryGetProperty("properties", out var properties))
        foreach (var property in properties.EnumerateObject())
          if (value.TryGetProperty(property.Name, out var child))
            Validate(property.Value, child, $"{path}.{property.Name}", violations);
    }
    if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items)) {
      var index = 0;
      foreach (var item in value.EnumerateArray())
        Validate(items, item, $"{path}[{index++}]", violations);
    }
  }

  private static bool MatchesType(string type, JsonElement value) => type switch {
    "object" => value.ValueKind == JsonValueKind.Object,
    "array" => value.ValueKind == JsonValueKind.Array,
    "string" => value.ValueKind == JsonValueKind.String,
    "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
    "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
    "number" => value.ValueKind == JsonValueKind.Number,
    _ => true,
  };
}
