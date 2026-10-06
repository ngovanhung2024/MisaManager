using System.Net.Http;
using System.Windows;
using System.Windows.Controls;

namespace MisaManager.App;

/// <summary>UI glue for <see cref="UpdateService"/>: prompts, progress in the status line and the settings dialog.</summary>
public static class UpdatePrompt
{
    private static bool busy;

    /// <param name="releaseCheckpoint">Releases the old Thêm vào MISA checkpoint before installing; returns an error or null.</param>
    public static async Task CheckAsync(Window owner, UpdateService updates, bool interactive, Action<string> status,
        Func<Task<string?>> releaseCheckpoint)
    {
        if (busy || !interactive && !updates.Settings.CheckOnStartup) return;
        busy = true;
        try
        {
            if (interactive) status("Đang kiểm tra bản cập nhật…");
            var update = await updates.CheckAsync();
            if (update is null)
            {
                if (interactive)
                {
                    status($"Đang dùng bản mới nhất ({UpdateService.CurrentVersion}).");
                    MessageBox.Show(owner, $"Đang dùng bản mới nhất ({UpdateService.CurrentVersion}).", "Cập nhật", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }
            var notes = update.Notes.Length > 1200 ? update.Notes[..1200] + "…" : update.Notes;
            if (MessageBox.Show(owner, $"Có bản mới {update.Version} (đang dùng {UpdateService.CurrentVersion}).\n\n{notes}\n\nTải và cài đặt ngay? App sẽ tự đóng và mở lại sau khi cài.",
                    "Có bản cập nhật", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            {
                status($"Có bản mới {update.Version}; bấm ••• → Kiểm tra cập nhật khi muốn cài.");
                return;
            }
            var progress = new Progress<double>(value => status($"Đang tải bản {update.Version}: {value:P0}"));
            var path = await updates.DownloadAsync(update, progress);
            var publisher = UpdateService.Publisher(path);
            status($"Đã tải và kiểm tra SHA-256 bản {update.Version}.");
            if (MessageBox.Show(owner, $"Đã tải bản {update.Version} và kiểm tra mã SHA-256.\nChữ ký số: {publisher ?? "chưa ký"}\n\nĐóng app và cài đặt ngay?",
                    "Cài đặt cập nhật", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            if (await releaseCheckpoint() is { } blocked) throw new UpdateException(blocked);
            updates.InstallAfterExit(path);
            Application.Current.Shutdown();
        }
        catch (Exception error) when (error is UpdateException or HttpRequestException or TaskCanceledException
            or System.Text.Json.JsonException or System.IO.IOException or InvalidOperationException or KeyNotFoundException)
        {
            status("Không cập nhật được: " + error.Message);
            if (interactive) MessageBox.Show(owner, error.Message, "Cập nhật", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { busy = false; }
    }

    public static void EditSettings(Window owner, UpdateService updates, Action<string> status)
    {
        var settings = updates.Settings;
        var repository = new TextBox { Text = settings.Repository, Margin = new Thickness(0, 2, 0, 8) };
        var token = new PasswordBox { Margin = new Thickness(0, 2, 0, 2) };
        var tokenHint = new TextBlock
        {
            FontSize = 11, Foreground = System.Windows.Media.Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = settings.TokenFromEnvironment ? $"Đang dùng token từ biến môi trường {UpdateSettings.TokenVariable}."
                : settings.HasStoredToken ? "Đã lưu token (mã hóa DPAPI cho tài khoản Windows này). Để trống để giữ nguyên."
                : "Chỉ cần khi repo private: fine-grained token, quyền Contents: Read-only cho repo này."
        };
        var clearToken = new CheckBox { Content = "Xóa token đã lưu", IsEnabled = settings.HasStoredToken, Margin = new Thickness(0, 0, 0, 8) };
        var startup = new CheckBox { Content = "Tự kiểm tra khi mở app", IsChecked = settings.CheckOnStartup, Margin = new Thickness(0, 0, 0, 12) };
        var save = new Button { Content = "Lưu", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Hủy", IsCancel = true, Width = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { save, cancel } };
        var panel = new StackPanel
        {
            Margin = new Thickness(14),
            Children =
            {
                new TextBlock { Text = $"Phiên bản hiện tại: {UpdateService.CurrentVersion}", Margin = new Thickness(0, 0, 0, 10) },
                new TextBlock { Text = "Repository GitHub chứa bản phát hành (owner/tên-repo):" }, repository,
                new TextBlock { Text = "GitHub token:" }, token, tokenHint, clearToken, startup, buttons
            }
        };
        var dialog = new Window
        {
            Title = "Cài đặt cập nhật", Owner = owner, Content = panel, Width = 440, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        save.Click += (_, _) =>
        {
            if (!UpdateSettings.IsValidRepository(repository.Text.Trim()))
            {
                MessageBox.Show(dialog, "Repository phải có dạng owner/tên-repo.", "Cài đặt cập nhật", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            settings.Repository = repository.Text.Trim();
            settings.CheckOnStartup = startup.IsChecked == true;
            if (clearToken.IsChecked == true) settings.SetToken(null);
            else if (token.Password.Length > 0) settings.SetToken(token.Password);
            try
            {
                settings.Save();
                dialog.DialogResult = true;
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
                MessageBox.Show(dialog, error.Message, "Cài đặt cập nhật", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        if (dialog.ShowDialog() == true) status("Đã lưu cài đặt cập nhật.");
    }
}
