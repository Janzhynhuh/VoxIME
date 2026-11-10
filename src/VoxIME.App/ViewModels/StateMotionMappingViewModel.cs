using System.Collections.ObjectModel;
using System.ComponentModel;
using VoxIME.App.Infrastructure;
using VoxIME.Core.Configuration;

namespace VoxIME.App.ViewModels;

/// <summary>设置页里的单个动作组复选项：勾选状态 + 每类角色各自的参数。</summary>
public sealed class MotionGroupOptionViewModel : ObservableObject
{
    public const string ModeLoop = "loop";
    public const string ModeOnce = "once";

    /// <summary>状态动作模式：循环 / 进入时播一次。</summary>
    public static IReadOnlyList<Choice> AllModes { get; } = new[]
    {
        new Choice("循环", ModeLoop),
        new Choice("进入时播一次", ModeOnce)
    };

    /// <summary>触发事件选项。</summary>
    public static IReadOnlyList<Choice> AllEvents { get; } = new[]
    {
        new Choice("点击桌宠", Live2DTriggerEvents.Tap),
        new Choice("模型加载时", Live2DTriggerEvents.Start)
    };

    private bool _isSelected;
    private Choice _selectedMode;
    private int _weight = 1;
    private bool _hold;
    private Choice _triggerEvent = AllEvents[0];

    public MotionGroupOptionViewModel(string group, bool isSelected, bool loop)
    {
        Group = group;
        _isSelected = isSelected;
        _selectedMode = loop ? AllModes[0] : AllModes[1];
    }

    public string Group { get; }

    /// <summary>XAML 里模式下拉的选项。</summary>
    public IReadOnlyList<Choice> ModeChoices => AllModes;

    /// <summary>XAML 里触发事件下拉的选项。</summary>
    public IReadOnlyList<Choice> EventChoices => AllEvents;

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

    /// <summary>循环播放 / 进入时播一次（状态动作）。</summary>
    public bool Loop => string.Equals(_selectedMode.Value, ModeLoop, StringComparison.Ordinal);

    /// <summary>随机池权重（1..10）。</summary>
    public int Weight
    {
        get => _weight;
        set => SetProperty(ref _weight, Math.Clamp(value, 1, 10));
    }

    /// <summary>单次动作播完是否保持末帧。</summary>
    public bool Hold
    {
        get => _hold;
        set => SetProperty(ref _hold, value);
    }

    /// <summary>触发事件（点击桌宠 / 模型加载时）。</summary>
    public Choice TriggerEvent
    {
        get => _triggerEvent;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _triggerEvent, value);
            }
        }
    }
}

/// <summary>
/// 动作组多选映射的通用逻辑（静默循环 / 待机随机池 / 触发动作 / 状态动作共用）：
/// 勾选多个组时同时叠加，最多 <see cref="Live2DMotionMap.MaxGroups"/> 个。
/// </summary>
public abstract class MotionGroupSelectionViewModel : ObservableObject
{
    private readonly List<string> _selected = new();
    private readonly Dictionary<string, bool> _loops = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _weights = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _holds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _events = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>从配置条目恢复；<c>Loop</c> 为 null 时按名字猜（含 idle → 循环，否则单次）。</summary>
    public virtual void SetGroups(IEnumerable<Live2DMotionEntry>? groups)
    {
        BeginRestore();
        foreach (var entry in groups ?? Array.Empty<Live2DMotionEntry>())
        {
            if (!AddRestoredGroup(entry?.Group))
            {
                continue;
            }

            SetLoop(entry!.Group.Trim(), entry.Loop ?? Live2DMotionEntry.DefaultLoopFor(entry.Group));
        }

        FinishRestore();
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
                var option = new MotionGroupOptionViewModel(group, isSelected, loop)
                {
                    Weight = isSelected ? GetWeight(group) : 1,
                    Hold = isSelected && GetHold(group),
                    TriggerEvent = isSelected ? EventChoice(GetEvent(group)) : MotionGroupOptionViewModel.AllEvents[0]
                };
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

    protected virtual string BuildSummary()
    {
        if (_selected.Count == 0)
        {
            return AutoSummary;
        }

        var parts = _selected.Select(group =>
            $"{group}（{(GetLoop(group) ? "循环" : "进入时播一次")}）").ToList();
        return _selected.Count == 1
            ? $"将播放：{parts[0]}"
            : $"将同时叠加 {_selected.Count} 个动作：{string.Join("、", parts)}";
    }

    protected void BeginRestore()
    {
        _selected.Clear();
        _loops.Clear();
        _weights.Clear();
        _holds.Clear();
        _events.Clear();
    }

    /// <summary>恢复一个已选组（trim、去重、限制数量）；返回是否加入。</summary>
    protected bool AddRestoredGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return false;
        }

        var trimmed = group.Trim();
        if (_selected.Count >= Live2DMotionMap.MaxGroups || Contains(trimmed))
        {
            return false;
        }

        _selected.Add(trimmed);
        return true;
    }

    protected void SetLoop(string group, bool loop) => _loops[group] = loop;

    protected void SetWeight(string group, int weight) => _weights[group] = Math.Clamp(weight, 1, 10);

    protected void SetHold(string group, bool hold) => _holds[group] = hold;

    protected void SetEvent(string group, string triggerEvent) =>
        _events[group] = Live2DTriggerEvents.Normalize(triggerEvent);

    protected void FinishRestore()
    {
        SyncOptions();
        RaiseSummary();
    }

    protected bool Contains(string group) =>
        _selected.Any(value => string.Equals(value, group, StringComparison.OrdinalIgnoreCase));

    protected bool GetLoop(string group) =>
        _loops.TryGetValue(group, out var loop) ? loop : Live2DMotionEntry.DefaultLoopFor(group);

    protected int GetWeight(string group) =>
        _weights.TryGetValue(group, out var weight) ? Math.Clamp(weight, 1, 10) : 1;

    protected bool GetHold(string group) =>
        _holds.TryGetValue(group, out var hold) && hold;

    protected string GetEvent(string group) =>
        _events.TryGetValue(group, out var triggerEvent) ? Live2DTriggerEvents.Normalize(triggerEvent) : Live2DTriggerEvents.Tap;

    protected void RaiseSummary()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedGroups));
    }

    private static Choice EventChoice(string triggerEvent) =>
        MotionGroupOptionViewModel.AllEvents.First(choice =>
            string.Equals(choice.Value, Live2DTriggerEvents.Normalize(triggerEvent), StringComparison.Ordinal));

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

        if (e.PropertyName == nameof(MotionGroupOptionViewModel.Weight))
        {
            if (Contains(option.Group))
            {
                _weights[option.Group] = Math.Clamp(option.Weight, 1, 10);
            }

            RaiseSummary();
            return;
        }

        if (e.PropertyName == nameof(MotionGroupOptionViewModel.Hold))
        {
            if (Contains(option.Group))
            {
                _holds[option.Group] = option.Hold;
            }

            RaiseSummary();
            return;
        }

        if (e.PropertyName == nameof(MotionGroupOptionViewModel.TriggerEvent))
        {
            if (Contains(option.Group))
            {
                _events[option.Group] = Live2DTriggerEvents.Normalize(option.TriggerEvent.Value);
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
                _weights[option.Group] = Math.Clamp(option.Weight, 1, 10);
                _holds[option.Group] = option.Hold;
                _events[option.Group] = Live2DTriggerEvents.Normalize(option.TriggerEvent.Value);

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
                    _weights.Remove(victim);
                    _holds.Remove(victim);
                    _events.Remove(victim);
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
                _weights.Remove(option.Group);
                _holds.Remove(option.Group);
                _events.Remove(option.Group);
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
                if (!option.IsSelected)
                {
                    continue;
                }

                option.SelectedMode = GetLoop(option.Group)
                    ? MotionGroupOptionViewModel.AllModes[0]
                    : MotionGroupOptionViewModel.AllModes[1];
                option.Weight = GetWeight(option.Group);
                option.Hold = GetHold(option.Group);
                option.TriggerEvent = EventChoice(GetEvent(option.Group));
            }
        }
        finally
        {
            _syncing = false;
        }
    }
}

/// <summary>状态动作映射：可勾选多个组叠加，每个组可选循环 / 进入时播一次，或勾“不播放”。</summary>
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

/// <summary>静默循环动作映射：勾选的组持续循环叠加；一个都不勾 = 自动找名字含 idle 的组。</summary>
public sealed class IdleMotionMappingViewModel : MotionGroupSelectionViewModel
{
    public IdleMotionMappingViewModel()
        : base("自动：使用模型里名字含 idle 的动作组（没有则不播放静默动作）。")
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
            ? $"静默循环：{selected[0]}"
            : $"静默循环叠加 {selected.Count} 个动作组：{string.Join("、", selected)}";
    }

    /// <summary>静默层一律循环。</summary>
    public override List<Live2DMotionEntry>? ToGroupList()
    {
        var groups = SelectedGroups;
        return groups.Count == 0
            ? null
            : groups.Take(Live2DMotionMap.MaxGroups)
                .Select(group => new Live2DMotionEntry(group, true))
                .ToList();
    }
}

/// <summary>待机随机池：每隔一段时间按权重随机播一个单次动作，默认播完回静默。</summary>
public sealed class IdlePoolMappingViewModel : MotionGroupSelectionViewModel
{
    public IdlePoolMappingViewModel()
        : base("随机池为空：不会自动插入待机小动作，只播放静默循环动作。")
    {
    }

    /// <summary>从配置恢复随机池条目（权重 + 是否保持末帧）。</summary>
    public void SetEntries(IEnumerable<Live2DPoolEntry>? entries)
    {
        BeginRestore();
        foreach (var entry in entries ?? Array.Empty<Live2DPoolEntry>())
        {
            if (!AddRestoredGroup(entry?.Group))
            {
                continue;
            }

            SetWeight(entry!.Group.Trim(), entry.Weight);
            SetHold(entry.Group.Trim(), entry.Hold);
        }

        FinishRestore();
    }

    public List<Live2DPoolEntry>? ToPoolList()
    {
        var groups = SelectedGroups;
        return groups.Count == 0
            ? null
            : groups.Take(Live2DMotionMap.MaxGroups)
                .Select(group => new Live2DPoolEntry(group, GetWeight(group), GetHold(group)))
                .ToList();
    }

    protected override string BuildSummary()
    {
        var groups = SelectedGroups;
        if (groups.Count == 0)
        {
            return AutoSummary;
        }

        var parts = groups.Select(group =>
            group + "（权重 " + GetWeight(group) + (GetHold(group) ? "，保持末帧" : "") + "）");
        return "随机池：" + string.Join("、", parts);
    }
}

/// <summary>触发式单次动作：点击桌宠 / 模型加载时触发，可设保持末帧。</summary>
public sealed class TriggerMappingViewModel : MotionGroupSelectionViewModel
{
    public TriggerMappingViewModel()
        : base("未配置触发动作：点击时若模型有名字含 tap 的动作组会自动播放。")
    {
    }

    public void SetEntries(IEnumerable<Live2DTriggerEntry>? entries)
    {
        BeginRestore();
        foreach (var entry in entries ?? Array.Empty<Live2DTriggerEntry>())
        {
            if (!AddRestoredGroup(entry?.Group))
            {
                continue;
            }

            SetEvent(entry!.Group.Trim(), entry.Event);
            SetHold(entry.Group.Trim(), entry.Hold);
            SetWeight(entry.Group.Trim(), entry.Weight);
        }

        FinishRestore();
    }

    public List<Live2DTriggerEntry>? ToTriggerList()
    {
        var groups = SelectedGroups;
        return groups.Count == 0
            ? null
            : groups.Take(Live2DMotionMap.MaxGroups)
                .Select(group => new Live2DTriggerEntry(group, GetEvent(group), GetHold(group), GetWeight(group)))
                .ToList();
    }

    protected override string BuildSummary()
    {
        var groups = SelectedGroups;
        if (groups.Count == 0)
        {
            return AutoSummary;
        }

        var parts = groups.Select(group =>
        {
            var eventLabel = GetEvent(group) == Live2DTriggerEvents.Start ? "模型加载时" : "点击桌宠";
            return group + "（" + eventLabel + (GetHold(group) ? "，保持末帧" : "") + "）";
        });
        return "触发动作：" + string.Join("、", parts);
    }
}
