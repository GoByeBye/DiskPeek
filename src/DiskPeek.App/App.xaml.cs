using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DiskPeek.App;

public partial class App : Application
{
    public static void SetTheme(string selection)
    {
        bool dark = selection == "Dark";
        if (selection == "System")
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        string[] keys = ["WindowBrush", "PanelBrush", "RaisedBrush", "BorderBrush", "TextBrush", "MutedBrush", "AccentBrush", "AccentTextBrush", "SelectionBrush", "WarningBrush", "DeletionBrush"];
        string[] colors = dark
            ? ["#10151E", "#171F2B", "#202C3C", "#2A3749", "#E7EDF5", "#92A1B8", "#63D8CC", "#0C2926", "#244D58", "#F5BE72", "#F28C95"]
            : ["#F1F4F8", "#FFFFFF", "#E8EDF4", "#D8E0EA", "#17263A", "#5D6F86", "#087F75", "#FFFFFF", "#D7EEEB", "#995D0C", "#B52636"];
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze();
            Current.Resources[keys[i]] = brush;
        }
    }
}
