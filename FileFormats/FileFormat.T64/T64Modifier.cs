#pragma warning disable CS1591

namespace FileFormat.T64;

/// <summary>
/// Compatibility facade for the T64 in-place editor.
/// </summary>
public static class T64Modifier {
  /// <summary>
  /// Adds or replaces a file in an existing T64 tape image.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data, ushort startAddress = 0x0801)
    => T64InPlaceModifier.AddFile(image, name, data, startAddress);

  /// <summary>
  /// Removes a named file from the T64 image and compacts the vacated bytes.
  /// </summary>
  public static bool RemoveFile(Stream image, string name)
    => T64InPlaceModifier.RemoveFile(image, name);
}
