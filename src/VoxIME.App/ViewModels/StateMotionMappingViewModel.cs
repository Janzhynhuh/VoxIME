using System.Collections.ObjectModel;
using System.ComponentModel;
using VoxIME.App.Infrastructure;
using VoxIME.Core.Configuration;

namespace VoxIME.App.ViewModels;

/// <summary>设置页里的单个动作组复选项。</summary>
public sealed class MotionGroupOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public MotionGroupOptionViewModel(string group, bool isSelected)
    {
        Group = group;
        _isSelected = isSelected;
    }

    public string Group { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// 一个状态的动作映射：可勾选多个动作组同时叠加播放，或勾“不播放”；
/// 一个都不勾表示自动使用与状态同名的动作组（沿用旧版行为）。
/// </summary>
public sealed class StateMotionMappingViewModel : ObservableObject
{
    private readonly List<string> _selected = new();
    private bool _disabled;
    private bool _syncing;

    public StateMotionMappingViewModel(string stateLabel, string autoGroupName)
    {
        StateLabel = stateLabel;
        AutoGroupName = autoGroupName;
    }

    public string StateLabel { get; }

    /// <summary>“自动”模式会查找的动作组名（如 Listening）。</summary>
    public string AutoGroupName { get; }

    public ObservableCollection<MotionGroupOptionViewModel> Options { get; } = new();

    /// <summary>勾选“不播放”：切到该状态时不触发动作。</summary>
    public bool Disabled
    {
        get => _disabled;
        set
        {
            if (!SetProperty(ref _disabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectionEnabled));
            OnPropertyChanged(nameof(Summary));
        }
    }

    public bool SelectionEnabled => !_disabled;

    /// <summary>当前勾选的动作组（按勾选顺序，最多 <see cref="Live2DMotionMap.MaxGroups"/> 个）。</summary>
    public IReadOnlyList<string> SelectedGroups => _selected.ToList();

    public string Summary
    {
        get
        {
            if (_disabled)
            {
                return "不播放：切到该状态时不触发动作。";
            }

            if (_selected.Count == 0)
            {
                return $"自动：使用与状态同名的动作组「{AutoGroupName}」，找不到时继续待机。";
            }

            return _selected.Count == 1
                ? $"将播放：{_selected[0]}"
                : $"将同时叠加 {_selected.Count} 个动作：{string.Join("、", _selected)}";
        }
    }

    /// <summary>按当前模型可用动作组重建复选列表（已选中的自定义值也会保留，避免保存后丢失）。</summary>
    public void RebuildOptions(IEnumerable<string> catalogGroups)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in catalogGroups)
        {
            if (string.IsNullOrWhiteSpace(group))
            {
                continue;
            }

            var trimmed = group.Trim();
            if (seen.Add(trimmed))
            {
                ordered.Add(trimmed);
            }
        }

        foreach (var group in _selected)
        {
            if (seen.Add(group))
            {
                ordered.Add(group);
            }
        }

        _syncing = true;
        try
        {
            Options.Clear();
            foreach (var group in ordered)
            {
                var option = new MotionGroupOptionViewModel(group, Contains(group));
                option.PropertyChanged += OnOptionPropertyChanged;
                Options.Add(option);
            }
        }
        finally
        {
            _syncing = false;
        }

        RaiseSummary();
    }

    /// <summary>从配置恢复：空/null = 自动；<c>__none__</c> = 不播放；其它 = 叠加的动作组。</summary>
    public void SetGroups(IEnumerable<string>? groups)
    {
        var list = (groups ?? Array.Empty<string>())
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .ToList();
        _disabled = list.Any(group => string.Equals(group, Live2DMotionMap.NoneMarker, StringComparison.Ordinal));
        _selected.Clear();
        if (!_disabled)
        {
            foreach (var group in list.Take(Live2DMotionMap.MaxGroups))
            {
                if (!Contains(group))
                {
                    _selected.Add(group);
                }
            }
        }

        OnPropertyChanged(nameof(Disabled));
        OnPropertyChanged(nameof(SelectionEnabled));
        SyncOptions();
        RaiseSummary();
    }

    /// <summary>保存到配置：不播放 → <c>["__none__"]</c>；一个都不勾 → null（自动同名匹配）。</summary>
    public List<string>? ToGroupList()
    {
        if (_disabled)
        {
            return new List<string> { Live2DMotionMap.NoneMarker };
        }

        return _selected.Count == 0 ? null : _selected.Take(Live2DMotionMap.MaxGroups).ToList();
    }

    private bool Contains(string group) =>
        _selected.Any(value => string.Equals(value, group, StringComparison.OrdinalIgnoreCase));

    private void OnOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing
            || e.PropertyName != nameof(MotionGroupOptionViewModel.IsSelected)
            || sender is not MotionGroupOptionViewModel option)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (option.IsSelected)
            {
                if (!Contains(option.Group))
                {
                    _selected.Add(option.Group);
                }

                // 超过上限时自动取消一个较早勾选的项，避免保存时静默丢配置。
                while (_selected.Count > Live2DMotionMap.MaxGroups)
                {
                    var victim = _selected.FirstOrDefault(value =>
                        !string.Equals(value, option.Group, StringComparison.OrdinalIgnoreCase));
                    if (victim is null)
                    {
                        break;
                    }

                    _selected.Remove(victim);
                    var victimOption = Options.FirstOrDefault(item =>
                        string.Equals(item.Group, victim, StringComparison.OrdinalIgnoreCase));
                    if (victimOption is not null)
                    {
                        victimOption.IsSelected = false;
                    }
                }
            }
            else
            {
                _selected.RemoveAll(value => string.Equals(value, option.Group, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _syncing = false;
        }

        RaiseSummary();
    }

    private void SyncOptions()
    {
        _syncing = true;
        try
        {
            foreach (var option in Options)
            {
                option.IsSelected = Contains(option.Group);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedGroups));
    }
}
