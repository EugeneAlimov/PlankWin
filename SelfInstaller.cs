using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace PlankWin
{
    // Самоустановка без отдельного инсталлятора: exe переносит себя в %LocalAppData%\Programs\PlankWin,
    // создаёт ярлык в Пуске и запись в «Приложениях» (HKCU — без прав администратора).
    // Удаление (из «Приложений» или трея): автозапуск, ярлык, запись, по желанию настройки, затем сам exe.
    public static class SelfInstaller
    {
        private const string AppName = "PlankWin";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PlankWin";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        public static string InstalledExe => Path.Combine(InstallDir, AppName + ".exe");
        private static string StartMenuLnk => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        private static string CurrentExe => Environment.ProcessPath ?? "";

        public static bool IsInstalled => string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string path);

        // Первый запуск не из места установки — предложить установку (один раз; «Нет» запоминается).
        public static void OfferIfNeeded()
        {
            if (IsInstalled || Updater.IsDevBuild || GlobalConfig.InstallPromptDismissed) return;
            var r = MessageBox.Show(string.Format(Loc.T("inst_offer"), InstallDir), "PlankWin", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes) Install();
            else GlobalConfig.InstallPromptDismissed = true;
        }

        public static void Install()
        {
            try
            {
                string src = CurrentExe;
                Directory.CreateDirectory(InstallDir);
                if (!IsInstalled)
                {
                    File.Copy(src, InstalledExe, overwrite: true);
                    // Метка «скачано из интернета» копируется вместе с файлом — снимаем, как Unblock-File:
                    // запуск уже разрешён пользователем, повторное предупреждение SmartScreen ни к чему.
                    DeleteFileW(InstalledExe + ":Zone.Identifier");
                }

                CreateStartMenuShortcut();
                RegisterUninstall();
                FixAutostartPath(InstalledExe);

                if (!IsInstalled)
                {
                    // Перезапуск из новой папки; новый процесс удалит исходный файл (перенос, а не копия).
                    Process.Start(new ProcessStartInfo(InstalledExe, $"--wait-pid {Environment.ProcessId} --remove-old \"{src}\"") { UseShellExecute = false });
                    MainWindow.ShutdownForRestart();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Loc.T("inst_failed"), ex.Message), "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Хвост переноса: исходный exe (например, из «Загрузок») удаляем, если это действительно PlankWin.
        public static void RemoveOld(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(CurrentExe), StringComparison.OrdinalIgnoreCase)) return;
                if (!Path.GetFileName(path).StartsWith(AppName, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
                File.Delete(path);
            }
            catch { }
        }

        // Автозапуск указывает на другой exe (перенос, переименование) — переписываем на текущий.
        public static void FixAutostartPath(string? exe = null)
        {
            try
            {
                exe ??= CurrentExe;
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key?.GetValue(AppName) is string v && !v.Trim('"').Equals(exe, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(AppName, $"\"{exe}\"");
            }
            catch { }
        }

        private static void CreateStartMenuShortcut()
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic lnk = shell.CreateShortcut(StartMenuLnk);
            lnk.TargetPath = InstalledExe;
            lnk.WorkingDirectory = InstallDir;
            lnk.IconLocation = InstalledExe + ",0";
            lnk.Description = "PlankWin dock";
            lnk.Save();
        }

        private static void RegisterUninstall()
        {
            using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
            k.SetValue("DisplayName", AppName);
            k.SetValue("DisplayVersion", Updater.CurrentText);
            k.SetValue("Publisher", "Eugene Alimov");
            k.SetValue("DisplayIcon", InstalledExe + ",0");
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            k.SetValue("URLInfoAbout", "https://github.com/EugeneAlimov/PlankWin");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try { k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); } catch { }
        }

        // После обновления версия в «Приложениях» должна совпадать с exe.
        public static void RefreshRegistration()
        {
            if (!IsInstalled) return;
            try { using var k = Registry.CurrentUser.OpenSubKey(UninstallKey, true); k?.SetValue("DisplayVersion", Updater.CurrentText); } catch { }
        }

        // Удаление. Вызывается из «Приложений» (PlankWin.exe --uninstall, отдельный процесс) или из трея.
        public static void Uninstall()
        {
            if (MessageBox.Show(Loc.T("uninst_confirm"), "PlankWin", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            bool wipeData = MessageBox.Show(Loc.T("uninst_data"), "PlankWin", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

            // Другие запущенные копии (панель, пока удаление идёт из «Приложений») — завершаем.
            try
            {
                foreach (var p in Process.GetProcessesByName(AppName).Where(p => p.Id != Environment.ProcessId))
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }
            }
            catch { }

            try { using var run = Registry.CurrentUser.OpenSubKey(RunKey, true); run?.DeleteValue(AppName, false); } catch { }
            try { if (File.Exists(StartMenuLnk)) File.Delete(StartMenuLnk); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            if (wipeData) { try { Directory.Delete(PlankConfig.GetDataDir(), true); } catch { } }

            MessageBox.Show(Loc.T("uninst_done"), "PlankWin", MessageBoxButton.OK, MessageBoxImage.Information);

            // Запущенный exe сам себя не удалит — поручаем cmd после нашего выхода.
            string exe = CurrentExe;
            string dir = Path.GetDirectoryName(exe) ?? "";
            bool ownDir = string.Equals(Path.GetFullPath(dir), Path.GetFullPath(InstallDir), StringComparison.OrdinalIgnoreCase);
            string cmd = $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{exe}\" \"{exe}.old\"" + (ownDir ? $" & rmdir \"{dir}\"" : "");
            try { Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }); } catch { }

            MainWindow.ShutdownForRestart();
        }
    }
}
