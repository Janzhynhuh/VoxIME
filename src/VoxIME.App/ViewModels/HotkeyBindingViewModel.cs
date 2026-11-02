using VoxIME.App.Infrastructure;

namespace VoxIME.App.ViewModels;

/// <summary>设置页里的一条热键输入（一个功能可以有多条）。</summary>
public sealed class HotkeyBindingViewModel : ObservableObject
{
    private string _gesture = "";

    public string Gesture
    {
        get => _gesture;
        set
        {
            if (SetProperty(ref _gesture, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>输入变化时通知父级重新校验。</summary>
    public event EventHandler? Changed;
}
