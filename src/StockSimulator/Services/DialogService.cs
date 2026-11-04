using System.Windows;
using Microsoft.Win32;

namespace StockSimulator.Services;

/// <summary>极简对话框服务（把 WPF 的文件/消息框集中到一处，ViewModel 只调用这里）。</summary>
public static class DialogService
{
    public static string? OpenFile(string filter, string title = "选择文件")
    {
        var dlg = new OpenFileDialog { Filter = filter, Title = title, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public static string? SaveFile(string filter, string defaultName, string title = "保存")
    {
        var dlg = new SaveFileDialog { Filter = filter, Title = title, FileName = defaultName, OverwritePrompt = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public static string? PickFolder(string title = "选择输出目录")
    {
        var dlg = new OpenFolderDialog { Title = title };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public static void Info(string message, string title = "提示")
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Warn(string message, string title = "警告")
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void Error(string message, string title = "错误")
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static bool Confirm(string message, string title = "确认")
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
