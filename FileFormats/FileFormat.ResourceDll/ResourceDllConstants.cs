#pragma warning disable CS1591
namespace FileFormat.ResourceDll;

/// <summary>
/// Constants shared between <see cref="ResourceDllReader"/> and <see cref="ResourceDllWriter"/>
/// for the Win32 resource directory tree (RT_RCDATA) layout inside a PE32+ DLL.
/// </summary>
internal static class ResourceDllConstants {
  /// <summary>Win32 RT_RCDATA resource type (raw binary).</summary>
  public const ushort RtRcData = 10;

  /// <summary>High bit on a resource directory entry's offset/id field marks
  /// "this is a string ID" (for names) or "this points to a sub-directory" (for child).</summary>
  public const uint HighBitFlag = 0x80000000u;

  /// <summary>Bytes per resource directory header.</summary>
  public const int DirHeaderSize = 16;

  /// <summary>Bytes per resource directory entry (id + child).</summary>
  public const int DirEntrySize = 8;

  /// <summary>Bytes per resource data entry (RVA + size + codepage + reserved).</summary>
  public const int DataEntrySize = 16;
}
