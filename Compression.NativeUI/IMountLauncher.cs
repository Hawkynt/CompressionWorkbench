using Compression.Mounting;

namespace Compression.NativeUI;

/// <summary>
/// Composition seam between the cross-platform UI and an actual filesystem-session opener.
/// Dokan/FUSE composition owns opening the image and filesystem session; the UI owns the
/// user-selected plan and the returned mount-session lifecycle.
/// </summary>
internal interface IMountLauncher {
  ValueTask<IMountSession> MountAsync(
    string imagePath,
    string formatId,
    MountPlan plan,
    string target,
    CancellationToken cancellationToken = default
  );
}
