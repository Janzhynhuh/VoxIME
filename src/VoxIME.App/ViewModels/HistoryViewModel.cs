using System.Collections.ObjectModel;
using VoxIME.App.Infrastructure;
using VoxIME.Core.Abstractions;
using VoxIME.Core.History;
using VoxIME.Core.Logging;

namespace VoxIME.App.ViewModels;

/// <summary>历史记录窗口 ViewModel。</summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly IHistoryStore _historyStore;
    private readonly IAppLogger _logger;
    private readonly int _limit;
    private List<TranscriptionRecord> _allRecords = new();
    private string _searchText = "";
    private string _statusText = "";

    public HistoryViewModel(IHistoryStore historyStore, IAppLogger logger, int limit = 500)
    {
        _historyStore = historyStore;
        _logger = logger;
        _limit = Math.Clamp(limit, 20, 2000);

        RefreshCommand = new RelayCommand(Load);
        ClearCommand = new RelayCommand(ClearAll);
        Load();
    }

    public ObservableCollection<TranscriptionRecord> Records { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ClearCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsEmpty => Records.Count == 0;

    public void Load()
    {
        try
        {
            _allRecords = _historyStore.Load(_limit).ToList();
            ApplyFilter();
            StatusText = _allRecords.Count == 0 ? "" : $"共 {_allRecords.Count} 条记录";
        }
        catch (Exception ex)
        {
            _logger.Warn($"加载历史记录失败：{ex.Message}");
            StatusText = "加载历史记录失败：" + ex.Message;
        }
    }

    public void ClearAll()
    {
        try
        {
            _historyStore.Clear();
            _allRecords = new List<TranscriptionRecord>();
            ApplyFilter();
            StatusText = "历史记录已清空";
        }
        catch (Exception ex)
        {
            _logger.Warn($"清空历史记录失败：{ex.Message}");
            StatusText = "清空失败：" + ex.Message;
        }
    }

    /// <summary>在底部状态栏显示一条临时提示。</summary>
    public void ReportStatus(string text) => StatusText = text;

    private void ApplyFilter()
    {
        var keyword = _searchText?.Trim();
        var filtered = string.IsNullOrEmpty(keyword)
            ? _allRecords
            : _allRecords.Where(r =>
                    (r.Text?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (r.ProviderName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();

        Records.Clear();
        foreach (var record in filtered)
        {
            Records.Add(record);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
