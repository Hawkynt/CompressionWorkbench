using Compression.Sfx.Ui;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Backends;

// Only the backend for the runtime this stub was published for is compiled in. A stub is prepended
// to every archive built with it, so carrying a second platform's backend would be dead weight in
// every one of them.
//
// Without a backend the window silently has nothing to draw on and the stub dies on launch — inside
// someone's SFX archive, where it is least debuggable. A RID whose condition matched neither one is
// a build failure here rather than a broken archive later.
#if !BACKEND_WINDOWS && !BACKEND_GTK
#error No NativeForms backend selected. Publish with -r win-* or -r linux-*; other runtimes get the CLI stub.
#endif

#if BACKEND_WINDOWS
BackendRegistry.Register(new Hawkynt.NativeForms.Backends.Windows.Win32Backend());
#endif
#if BACKEND_GTK
BackendRegistry.Register(new Hawkynt.NativeForms.Backends.Gtk.GtkBackend());
#endif

Application.Run(new SfxWindow());
return 0;
