namespace FileFormat.Ha;

internal static class HaConstants {
  internal static readonly byte[] Magic = [0x48, 0x41]; // "HA"
  internal const int MethodStore = 0;
  internal const int MethodHsc = 1;
  internal const int MethodAsc = 2;
  internal const int MethodDirectory = 14;
}
