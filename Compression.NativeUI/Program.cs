using Compression.Lib;
using Compression.Mounting;
using Compression.Mounting.Dokan;
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
}

var mountBackends = new MountBackendRegistry(backends);
var launcher = new RegistryMountLauncher(new FilesystemMountLauncher(mountBackends));
Application.Run(new MainForm(backends, launcher));
