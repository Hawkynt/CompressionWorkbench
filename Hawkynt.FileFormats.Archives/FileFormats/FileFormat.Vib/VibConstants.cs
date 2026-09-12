namespace FileFormat.Vib;

/// <summary>Well-known member names inside a vSphere Installation Bundle.</summary>
internal static class VibConstants {
  /// <summary>Bundle metadata member.</summary>
  public const string DescriptorName = "descriptor.xml";

  /// <summary>Detached PKCS#7 signature member.</summary>
  public const string SignatureName = "sig.pkcs7";
}
