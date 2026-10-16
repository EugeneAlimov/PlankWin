using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace PlankWin
{
    // Автообновление из GitHub Releases. Версия exe прошивается в CI из тега (v1.2.3 -> 1.2.3.0).
    // Применение: скачиваем новый exe рядом (PlankWin.exe.new), работающий exe ПЕРЕИМЕНОВЫВАЕМ в .old
    // (Windows разрешает переименовать запущенный файл), новый ставим на его место, перезапускаемся.
    // Хвост .old удаляется при следующем старте.
    public static class Updater
    {
        private const string Repo = "EugeneAlimov/PlankWin";

        public sealed class ReleaseInfo
        {
            public Version Version = new(0, 0);
            public string Tag = "";
            public string Notes = "";
            public string PageUrl = "";
            public string AssetUrl = "";
            public long AssetSize;
            public string? Sha256;
        }

        public static ReleaseInfo? Available { get; private set; }
        public static bool Busy { get; private set; }

        private static readonly HttpClient Http = CreateHttp();
        private static HttpClient CreateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PlankWin-Updater", "1.0"));
            h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return h;
        }

        public static Version Current => Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0));
        public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

        // Сборка разработчика — автоматически не проверяем и молча не обновляемся:
        //  - запуск из bin\Debug|Release;
        //  - версия 1.0.0.0 — значение по умолчанию у локальной сборки (CI всегда прошивает версию из тега
        //    или 0.0.N, поэтому тег v1.0.0 не использовать).
        public static bool IsDevBuild
        {
            get
            {
                string p = Environment.ProcessPath ?? "";
                return p.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
                    || p.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase)
                    || Current == new Version(1, 0, 0, 0);
            }
        }

        private static Version Normalize(Version v) =>
            new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

        // Удалить хвост прошлого обновления.
        public static void CleanupOld()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (exe == null) return;
                foreach (var f in new[] { exe + ".old", exe + ".new" })
                    if (File.Exists(f)) File.Delete(f);
            }
            catch { }
        }

        // Проверка последнего релиза. Возвращает найденную новую версию или null.
        public static async Task<ReleaseInfo?> CheckAsync()
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (!resp.IsSuccessStatusCode) return null; // 404 — релизов ещё нет
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;

            var info = new ReleaseInfo
            {
                Version = Normalize(ver),
                Tag = tag,
                Notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                PageUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : ""
            };
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase)) continue;
                info.AssetUrl = a.GetProperty("browser_download_url").GetString() ?? "";
                info.AssetSize = a.GetProperty("size").GetInt64();
                if (a.TryGetProperty("digest", out var dg) && dg.GetString() is string d && d.StartsWith("sha256:"))
                    info.Sha256 = d.Substring(7);
                break;
            }
            if (info.AssetUrl.Length == 0) return null;

            Available = info.Version > Current ? info : null;
            return Available;
        }

        // Скачать и установить. false — нет прав на папку программы (тогда открываем страницу релиза).
        public static async Task<bool> DownloadAndApplyAsync(ReleaseInfo info)
        {
            if (Busy) return true;
            Busy = true;
            try
            {
                string exe = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath");
                string tmp = exe + ".new", old = exe + ".old";

                try { using (File.Create(tmp)) { } }
                catch (UnauthorizedAccessException) { OpenPage(info); return false; }

                using (var resp = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                    await src.CopyToAsync(dst);
                }

                // Целостность: размер и (если GitHub отдал) SHA-256.
                if (new FileInfo(tmp).Length != info.AssetSize) throw new InvalidDataException("size mismatch");
                if (info.Sha256 != null)
                {
                    await using var fs = File.OpenRead(tmp);
                    string h = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
                    if (!string.Equals(h, info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("sha256 mismatch");
                }

                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                try { File.Move(tmp, exe); }
                catch { File.Move(old, exe); throw; } // откат: вернуть рабочий exe на место

                MainWindow.RestartApp();
                return true;
            }
            catch
            {
                try { string? e = Environment.ProcessPath; if (e != null && File.Exists(e + ".new")) File.Delete(e + ".new"); } catch { }
                throw;
            }
            finally { Busy = false; }
        }

        public static void OpenPage(ReleaseInfo info)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(info.PageUrl) { UseShellExecute = true }); } catch { }
        }

        // Диалог «обновить сейчас?» с описанием изменений.
        public static async Task PromptAndUpdate(ReleaseInfo info)
        {
            string notes = info.Notes.Trim();
            if (notes.Length > 700) notes = notes.Substring(0, 700) + "…";
            string msg = string.Format(Loc.T("upd_prompt"), info.Version.ToString(3), CurrentText, notes);
            if (MessageBox.Show(msg, "PlankWin", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            MainWindow.ShowTrayBalloon(Loc.T("upd_title"), Loc.T("upd_downloading"), null);
            try
            {
                if (!await DownloadAndApplyAsync(info))
                    MessageBox.Show(Loc.T("upd_noaccess"), "PlankWin", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Loc.T("upd_failed"), ex.Message), "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Фоновая проверка: о новой версии сообщаем один раз за сеанс.
        private static Version? _notified;
        public static async Task BackgroundCheck()
        {
            if (!GlobalConfig.CheckUpdates || IsDevBuild || Busy) return;
            try
            {
                var info = await CheckAsync();
                if (info == null || (_notified != null && _notified >= info.Version)) return;
                _notified = info.Version;
                MainWindow.ShowTrayBalloon(Loc.T("upd_title"), string.Format(Loc.T("upd_balloon"), info.Version.ToString(3)),
                    () => _ = PromptAndUpdate(info));
            }
            catch { }
        }

        // Ручная проверка из трея: сообщаем и «у вас последняя».
        public static async Task ManualCheck()
        {
            try
            {
                var info = await CheckAsync();
                if (info != null) await PromptAndUpdate(info);
                else MessageBox.Show(string.Format(Loc.T("upd_uptodate"), CurrentText), "PlankWin", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Loc.T("upd_failed"), ex.Message), "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}