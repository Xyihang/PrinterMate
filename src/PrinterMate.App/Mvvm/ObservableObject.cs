using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PrinterMate.App.Mvvm;

/// <summary>最小可用的 MVVM 通知基类(不引入任何第三方包)。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>页面被切换进入/离开时的钩子(用于定时器启停)。</summary>
public interface IActivatable
{
    void OnActivated();

    void OnDeactivated();
}
