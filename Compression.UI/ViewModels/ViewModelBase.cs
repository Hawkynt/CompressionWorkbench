using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Compression.UI.ViewModels;

internal abstract class ViewModelBase : INotifyPropertyChanged {
  public event PropertyChangedEventHandler? PropertyChanged;

  protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null) {
    if (EqualityComparer<T>.Default.Equals(field, value)) return false;
    field = value;
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    return true;
  }

  protected void OnPropertyChanged([CallerMemberName] string? name = null)
    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
