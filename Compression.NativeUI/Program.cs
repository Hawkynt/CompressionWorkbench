// Program.cs is top-level statements, so it sits in the GLOBAL namespace while MainForm,
// RegistryMountLauncher and IMountLauncher all declare `namespace Compression.NativeUI`. Without
// this using it cannot see its own project's types, which is why the project did not compile.
using Compression.Lib;
using Compression.NativeUI;
using Compression.Mounting;
using Compression.Mounting.Dokan;
using Compression.Mounting.Fuse;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Backends;
using Hawkynt.NativeForms.Backends.Gtk;
using Hawkynt.NativeForms.Backends.Windows;

BackendRegistry.Register(new Win32Backend());
BackendRegistry.Register(new GtkBackend());

FormatRegistration.EnsureInitialized();

var backends = new List<IFilesystemMountBackend>();
if (OperatingSystem.IsWindows()) {
  var dokan = new DokanFilesystemMountBackend();
  if (dokan.RuntimeStatus.IsAvailable)
    backends.Add(dokan);
} else if (OperatingSystem.IsLinux()) {
  var fuse = new FuseFilesystemMountBackend();
  if (fuse.RuntimeStatus.IsAvailable)
    backends.Add(fuse);
}

var mountBackends = new MountBackendRegistry(backends);
var launcher = new RegistryMountLauncher(new FilesystemMountLauncher(mountBackends));
Application.Run(new MainForm(backends, launcher));
