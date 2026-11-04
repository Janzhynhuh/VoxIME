using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// ComboBox 弹出层左对齐（全局）。
///
/// WPF 的 ComboBox 在"弹层内容比输入框宽"时会向左溢出（第一版就存在的历史行为）：
/// 弹层右边缘贴着输入框右侧，左边多出一截。默认的 Bottom 放置 + HorizontalOffset 在打开后
/// 再修正会被 WPF 重新布局覆盖，因此这里改用 <see cref="PlacementMode.Custom"/>：
/// 自定义回调把弹层水平居中在输入框下方，弹层比输入框宽时左右对称溢出。
///
/// 通过全局 ComboBox 样式设置附加属性 <see cref="EnabledProperty"/>，每个 ComboBox 加载时
/// 自动套用；现有和以后新增的下拉框都生效，不需要逐页/逐个控件处理。
/// </summary>
public static class ComboBoxPopupAlignment
{
    /// <summary>是否启用弹出层左对齐；由全局 ComboBox 样式统一设为 True。</summary>
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(ComboBoxPopupAlignment),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo || e.NewValue is not true)
        {
            return;
        }

        if (combo.IsLoaded)
        {
            Apply(combo);
        }
        else
        {
            combo.Loaded += OnComboBoxLoaded;
        }
    }

    private static void OnComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo)
        {
            return;
        }

        combo.Loaded -= OnComboBoxLoaded;
        Apply(combo);
    }

    private static void Apply(ComboBox combo)
    {
        if (combo.Template.FindName("PART_Popup", combo) is not Popup popup)
        {
            return;
        }

        popup.PlacementTarget = combo;
        popup.Placement = PlacementMode.Custom;
        popup.CustomPopupPlacementCallback = PlacePopup;
    }

    private static CustomPopupPlacement[] PlacePopup(Size popupSize, Size targetSize, Point offset)
    {
        // 默认水平居中：弹层比输入框宽时左右对称溢出；
        // 同时提供左/右对齐与上翻候选，让 WPF 在贴近屏幕边缘时仍能选一个不超出工作区的方案。
        var center = (targetSize.Width - popupSize.Width) / 2;
        var left = 0d;
        var right = targetSize.Width - popupSize.Width;
        var below = targetSize.Height;
        var above = -popupSize.Height;

        return new[]
        {
            new CustomPopupPlacement(new Point(center, below), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(center, above), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(left, below), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(right, below), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(left, above), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(right, above), PopupPrimaryAxis.Horizontal)
        };
    }
}
