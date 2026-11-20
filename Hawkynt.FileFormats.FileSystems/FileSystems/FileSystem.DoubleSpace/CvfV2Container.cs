namespace FileSystem.DoubleSpace;

/// <summary>
/// The two version-2 compressed volume files of MS-DOS 6.x, told apart by the MDBPB's OEM name and CVF
/// version (word at 0x33); the layout is otherwise the same.
/// </summary>
public enum CvfV2Container {
  /// <summary>DriveSpace (MS-DOS 6.22): <c>MSDBL6.0</c>, version 2.</summary>
  DriveSpace,

  /// <summary>DoubleSpace (MS-DOS 6.0 / 6.2): <c>MSDSP6.0</c>, version 0.</summary>
  DoubleSpace,
}
