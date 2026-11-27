using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.WPF.Utils;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LenovoLegionToolkit.WPF.Windows;

public class BaseWindow : UiWindow
{
    private static readonly ImageSource? AppWindowIcon = LoadAppWindowIcon();

    private static ImageSource? LoadAppWindowIcon()
    {
        try
        {
            var icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon.ico", UriKind.Absolute));
            icon.Freeze();
            return icon;
        }
        catch
        {
            return null;
        }
    }

    protected BaseWindow()
    {
        SnapsToDevicePixels = true;
        ExtendsContentIntoTitleBar = true;

        if (AppWindowIcon is not null)
            Icon = AppWindowIcon;

        var settings = IoCContainer.Resolve<ApplicationSettings>();
        WindowBackdropType = ThemeManager.GetBackgroundType(settings.Store.BackdropType);

        DpiChanged += BaseWindow_DpiChanged;

        PreviewKeyDown += (s, e) => {
            if (e.Key == Key.System && e.SystemKey == Key.LeftAlt)
            {
                e.Handled = true;
                Keyboard.ClearFocus();
            }
        };  
    }

    private void BaseWindow_DpiChanged(object sender, DpiChangedEventArgs e) => VisualTreeHelper.SetRootDpi(this, e.NewDpi);
}
