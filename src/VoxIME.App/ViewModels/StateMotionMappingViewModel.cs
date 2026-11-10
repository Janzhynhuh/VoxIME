using System.Collections.ObjectModel;
using System.ComponentModel;
using VoxIME.App.Infrastructure;
using VoxIME.Core.Configuration;

namespace VoxIME.App.ViewModels;

/// <summary>设置页里的单个动作组复选项：是否勾选 + 播放模式（循环 / 单次保持）。</summary>
public sealed class MotionGroupOptionViewModel : ObservableObject
{
    public const string ModeLoop = "loop";
    public const string ModeOnce = "once";

    /// <summary>模式下拉的共享选项。</summary>
    public static IReadOnlyList<Choice> AllModes { get; } = new[]
    {
        new Choice("循环", ModeLoop),
        new Choice("单次保持", ModeOnce)
    };

    /// <summary>实例包装，便于 XAML 在每行里直接绑定下拉项。</summary>
    public IReadOnlyList<Choice> ModeChoices => AllModes;

    private bool _isSelected;
    private Choice _selectedMode;

    public MotionGroupOptionViewModel(string group, bool isSelected, bool loop)
    {
        Group = group;
        _isSelected = isSelected;
        _selectedMode = loop ? AllModes[0] : AllModes[1];
    }

    public string Group { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public Choice SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedMode, value);
            }
        }
    }

    /// <summary>true=循环播放；false=播一次并保持末帧。</summary>
    public bool Loop => string.Equals(_selectedMode.Value, ModeLoop, StringComparison.Ordinal);

    public string ModeLabel => Loop ? "循环" : "单次保持";
}

/// <summary>
/// 多选动作组映射的通用逻辑（状态动作与待机动作共用）：
/// 勾选多个组时同时叠加播放，每个组可单独选择循环 / 播一次并保持末帧。
/// </summary>
public abstract class MotionGroupSelectionViewModel : ObservableObject
{
    private readonly List<string> _selected = new();
    private readonly Dictionary<string, bool> _loops = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncing;

    protected MotionGroupSelectionViewModel(string autoSummary)
    {
        AutoSummary = autoSummary;
    }

    /// <summary>一个都不勾时的摘要文案（各页面自行描述“自动”的含义）。</summary>
    public string AutoSummary { get; }

    public ObservableCollection<MotionGroupOptionViewModel> Options { get; } = new();

    /// <summary>当前勾选的动作组名（按勾选顺序，最多 <see cref="Live2DMotionMap.MaxGroups"/> 个）。</summary>
    public IReadOnlyList<string> SelectedGroups => _selected.ToList();

    public virtual string Summary => BuildSummary();

    /// <summary>从配置条目恢复；<c>Loop</c> 为 null 时按名字猜（含 idle → 循环，否则单次保持）。</summary>
    public virtual void SetGroups(IEnumerable<Live2DMotionEntry>? groups)
    {
        _selected.Clear();
        _loops.Clear();
        foreach (var entry in groups ?? Array.Empty<Live2DMotionEntry>())
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Group))
            {
                continue;
            }

            var group = entry.Group.Trim();
            if (_selected.Count >= Live2DMotionMap.MaxGroups)
            {
                break;
            }

            if (_selected.Contains(group, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            _selected.Add(group);
            _loops[group] = entry.Loop ?? Live2DMotionEntry.DefaultLoopFor(group);
        }

        SyncOptions();
        RaiseSummary();
    }

    /// <summary>保存到配置：一个都不勾 → null（自动）。</summary>
    public virtual List<Live2DMotionEntry>? ToGroupList()
    {
        if (_selected.Count == 0)
        {
            return null;
        }

        return _selected
            .Take(Live2DMotionMap.MaxGroups)
            .Select(group => new Live2DMotionEntry(group, GetLoop(group)))
            .ToList();
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
                var isSelected = Contains(group);
                var loop = isSelected ? GetLoop(group) : Live2DMotionEntry.DefaultLoopFor(group);
                var option = new MotionGroupOptionViewModel(group, isSelected, loop);
                option.PropertyChanged += OnOptionPropertyChanged;
                Options.Add(option);
                if (isSelected)
                {
                    _loops[group] = loop;
                }
            }
        }
        finally
        {
            _syncing = false;
        }

        RaiseSummary();
    }

    protected virtual string BuildSummary()
    {
        if (_selected.Count == 0)
        {
            return AutoSummary;
        }

        var parts = _selected.Select(FormatEntry).ToList();
        return _selected.Count == 1
            ? $"将播放：{parts[0]}"
            : $"将同时叠加 {_selected.Count} 个动作：{string.Join("、", parts)}";
    }

    protected string FormatEntry(string group) =>
        $"{group}（{(GetLoop(group) ? "循环" : "单次保持")}）";

    protected void RaiseSummary()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedGroups));
    }

    protected bool Contains(string group) =>
        _selected.Any(value => string.Equals(value, group, StringComparison.OrdinalIgnoreCase));

    private bool GetLoop(string group) =>
        _loops.TryGetValue(group, out var loop) ? loop : Live2DMotionEntry.DefaultLoopFor(group);

    private void OnOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || sender is not MotionGroupOptionViewModel option)
        {
            return;
        }

        if (e.PropertyName == nameof(MotionGroupOptionViewModel.SelectedMode))
        {
            if (Contains(option.Group))
            {
                _loops[option.Group] = option.Loop;
            }

            RaiseSummary();
            return;
        }

        if (e.PropertyName != nameof(MotionGroupOptionViewModel.IsSelected))
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

                _loops[option.Group] = option.Loop;

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
                    _loops.Remove(victim);
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
                _loops.Remove(option.Group);
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
                if (option.IsSelected)
                {
                    option.SelectedMode = GetLoop(option.Group)
                        ? MotionGroupOptionViewModel.AllModes[0]
                        : MotionGroupOptionViewModel.AllModes[1];
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }
}

/// <summary>状态动作映射：可勾选多个组叠加，每个组可选循环/单次保持，或勾“不播放”。</summary>
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

    public override void SetGroups(IEnumerable<Live2DMotionEntry>? groups)
    {
        var list = (groups ?? Array.Empty<Live2DMotionEntry>())
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Group))
            .ToList();
        var disabled = list.Any(entry =>
            string.Equals(entry.Group.Trim(), Live2DMotionMap.NoneMarker, StringComparison.Ordinal));
        if (disabled)
        {
            list = new List<Live2DMotionEntry>();
        }

        _disabled = disabled;
        OnPropertyChanged(nameof(Disabled));
        OnPropertyChanged(nameof(SelectionEnabled));
        base.SetGroups(list);
    }

    public override List<Live2DMotionEntry>? ToGroupList() =>
        _disabled ? new List<Live2DMotionEntry> { new(Live2DMotionMap.NoneMarker) } : base.ToGroupList();
}

/// <summary>待机动作映射：可勾选多个组循环或单次保持；一个都不勾 = 自动找名字含 idle 的组。</summary>
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

        var parts = selected.Select(FormatEntry).ToList();
        return selected.Count == 1
            ? $"待机将播放：{parts[0]}"
            : $"待机将叠加 {selected.Count} 个动作组：{string.Join("、", parts)}";
    }
}
