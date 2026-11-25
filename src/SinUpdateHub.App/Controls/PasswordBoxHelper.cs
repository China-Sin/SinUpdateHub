using System.Windows;
using System.Windows.Controls;

namespace SinUpdateHub.App.Controls;

/// <summary>
/// 让 PasswordBox 支持 MVVM 绑定（WPF 的 Password 不是依赖属性）。
/// 注意：这里给附加属性的默认值用了一个哨兵字符串，
/// 否则「ViewModel 里的密码初始为空 + 用户手输」时，绑定值与默认值相同 → 属性变更回调不触发 →
/// PasswordChanged 永远不订阅 → 输入的密码传不回 ViewModel（曾导致 WebDAV 一直 401、发送空密码）。
/// </summary>
public static class PasswordBoxHelper
{
    private const string Unset = "\u0000__SinUpdateHub_Unset__";

    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(string),
            typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(Unset, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBindPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached("IsUpdating", typeof(bool), typeof(PasswordBoxHelper));

    public static string GetBindPassword(DependencyObject obj) => (string)obj.GetValue(BindPasswordProperty);

    public static void SetBindPassword(DependencyObject obj, string value) => obj.SetValue(BindPasswordProperty, value);

    private static void OnBindPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
        {
            return;
        }

        box.PasswordChanged -= OnPasswordChanged;
        if (!(bool)box.GetValue(IsUpdatingProperty))
        {
            var newValue = e.NewValue as string ?? string.Empty;
            if (newValue == Unset)
            {
                newValue = string.Empty;
            }

            if (box.Password != newValue)
            {
                box.Password = newValue;
            }
        }

        box.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box)
        {
            return;
        }

        box.SetValue(IsUpdatingProperty, true);
        SetBindPassword(box, box.Password);
        box.SetValue(IsUpdatingProperty, false);
    }
}
