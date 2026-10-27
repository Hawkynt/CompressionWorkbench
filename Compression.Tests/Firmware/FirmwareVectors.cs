#pragma warning disable CS1591
using System.Reflection;

namespace Compression.Tests.Firmware;

/// <summary>The reference images under Firmware/ReferenceVectors, embedded as <c>FirmwareVectors.&lt;name&gt;</c>.</summary>
internal static class FirmwareVectors {
  public static byte[] Load(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"FirmwareVectors.{name}")
      ?? throw new FileNotFoundException($"embedded firmware vector {name} is missing");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
