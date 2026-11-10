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
/// 多选动作组映射的通用逻辑（状态动作与待机动作共用）：
/// 勾选多个组时同时叠加播放，一个都不勾表示“自动”，最多 <see cref="Live2DMotionMap.MaxGroups"/> 个。
/// </summary>
public abstract class MotionGroupSelectionViewModel : ObservableObject
{
    private readonly List<string> _selected = new();
    private bool _syncing;

    protected MotionGroupSelectionViewModel(string autoSummary)
    {
        AutoSummary = autoSummary;
    }

    /// <summary>一个都不勾时的摘要文案（各页面自行描述“自动”的含义）。</summary>
    public string AutoSummary { get; }

    public ObservableCollection<MotionGroupOptionViewModel> Options { get; } = new();

    /// <summary>当前勾选的动作组（按勾选顺序，最多 <see cref="Live2DMotionMap.MaxGroups"/> 个）。</summary>
    public IReadOnlyList<string> SelectedGroups => _selected.ToList();

    public virtual string Summary => BuildSummary();

    public virtual void SetGroups(IEnumerable<string>? groups)
    {
        var list = (groups ?? Array.Empty<string>())
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .ToList();
        ApplyGroups(list);
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

    /// <summary>保存到配置：一个都不勾 → null（自动）。派生类可覆盖为“不播放”等特殊语义。</summary>
    public virtual List<string>? ToGroupList() =>
        _selected.Count == 0 ? null : _selected.Take(Live2DMotionMap.MaxGroups).ToList();

    protected virtual string BuildSummary()
    {
        if (_selected.Count == 0)
        {
            return AutoSummary;
        }

        return _selected.Count == 1
            ? $"将播放：{_selected[0]}"
            : $"将同时叠加 {_selected.Count} 个动作：{string.Join("、", _selected)}";
    }

    /// <summary>把解析后的组列表写入选区（派生类解析完特殊标记后调用）。</summary>
    protected void ApplyGroups(IReadOnlyList<string> groups)
    {
        _selected.Clear();
        foreach (var group in groups.Take(Live2DMotionMap.MaxGroups))
        {
            if (!Contains(group))
            {
                _selected.Add(group);
            }
        }

        SyncOptions();
        RaiseSummary();
    }

    protected void RaiseSummary()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedGroups));
    }

    protected bool Contains(string group) =>
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
}

/// <summary>状态动作映射：可勾选多个组叠加，或勾“不播放”；一个都不勾 = 自动同名动作组。</summary>
public sealed class StateMotionMappingViewModel : MotionGroupSelectionViewModel
{
    private bool _disabled;

    public StateMotionMappingViewModel(string autoGroupName)
        : base($"自动：使用与状态同名的动作组「{autoGroupName}」，找不到时继续待机。")
    {
        AutoGroupName = autoGroupName;
    }

    /// <summary>“自动”模式会查找的动作组名（如 Listening）。</summary>
    public string AutoGroupName { get; }

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
            RaiseSummary();
        }
    }

    public bool SelectionEnabled => !_disabled;

    public override string Summary => _disabled ? "不播放：切到该状态时不触发动作。" : base.Summary;

    public override void SetGroups(IEnumerable<string>? groups)
    {
        var list = (groups ?? Array.Empty<string>())
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .ToList();
        var disabled = list.Any(group => string.Equals(group, Live2DMotionMap.NoneMarker, StringComparison.Ordinal));
        if (disabled)
        {
            list = new List<string>();
        }

        _disabled = disabled;
        OnPropertyChanged(nameof(Disabled));
        OnPropertyChanged(nameof(SelectionEnabled));
        ApplyGroups(list);
    }

    public override List<string>? ToGroupList() =>
        _disabled ? new List<string> { Live2DMotionMap.NoneMarker } : base.ToGroupList();
}

/// <summary>待机动作映射：可勾选多个组循环叠加；一个都不勾 = 自动找名字含 idle 的组。</summary>
public sealed class IdleMotionMappingViewModel : MotionGroupSelectionViewModel
{
    public IdleMotionMappingViewModel()
        : base("自动：使用模型里名字含 idle 的动作组（没有则不播放待机动作）。")
    {
    }

    protected override string BuildSummary()
    {
        var selected = SelectedGroups;
        if (selected.Count == 0)
        {
            return AutoSummary;
        }

        return selected.Count == 1
            ? $"待机将播放：{selected[0]}"
            : $"待机将叠加 {selected.Count} 个动作组：{string.Join("、", selected)}";
    }
}
