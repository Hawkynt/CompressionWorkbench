using Compression.Lib;
using Compression.Mounting;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Backends;
using Hawkynt.NativeForms.Backends.Gtk;
using Hawkynt.NativeForms.Backends.Windows;

BackendRegistry.Register(new Win32Backend());
BackendRegistry.Register(new GtkBackend());

FormatRegistration.EnsureInitialized();
Application.Run(new MainForm(Array.Empty<IFilesystemMountBackend>()));
