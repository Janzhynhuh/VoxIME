using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using VoxIME.App.ViewModels;

namespace VoxIME.App.Views.SettingsPages;

/// <summary>设置页：服务（ASR 适配器列表 + 编辑 + 测试识别）。</summary>
public partial class ProvidersPage : UserControl
{
    private SettingsViewModel? _viewModel;
    private bool _syncingCredentials;

    public ProvidersPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => AttachViewModel(DataContext as SettingsViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e) => AttachViewModel(null);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        AttachViewModel(DataContext as SettingsViewModel);

    private void AttachViewModel(SettingsViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SyncCredentialBoxes();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SelectedProvider)
            or nameof(SettingsViewModel.HasSelectedProvider))
        {
            SyncCredentialBoxes();
        }
    }

    /// <summary>
    /// 把当前选中服务的凭据填进 PasswordBox（PasswordBox.Password 不能直接绑定，改为代码同步）。
    /// 用户输入时直接写回 ViewModel，保存/测试识别才能拿到 Key。
    /// </summary>
    private void SyncCredentialBoxes()
    {
        _syncingCredentials = true;
        try
        {
            var provider = _viewModel?.SelectedProvider;
            ApiKeyBox.Password = provider?.ApiKey ?? "";
            SecretIdBox.Password = provider?.SecretId ?? "";
            ApiSecretBox.Password = provider?.ApiSecret ?? "";
        }
        finally
        {
            _syncingCredentials = false;
        }
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingCredentials || _viewModel?.SelectedProvider is not { } provider)
        {
            return;
        }

        provider.ApiKey = ApiKeyBox.Password;
    }

    private void SecretIdBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingCredentials || _viewModel?.SelectedProvider is not { } provider)
        {
            return;
        }

        provider.SecretId = SecretIdBox.Password;
    }

    private void ApiSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingCredentials || _viewModel?.SelectedProvider is not { } provider)
        {
            return;
        }

        provider.ApiSecret = ApiSecretBox.Password;
    }
}
