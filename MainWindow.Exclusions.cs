using System;
using System.Collections.Generic;
using System.Linq;

namespace PlankWin
{
    public partial class MainWindow
    {
        // Применить список исключений сразу, а не только к новым окнам:
        // включили — кнопка программы уходит с панели (и закреплённая тоже, как «Не показывать в доке»),
        // выключили — уже открытые окна программы возвращаются на панель.
        public void ApplyExclusions()
        {
            var ignored = new HashSet<string>(Settings.IgnoreProcessNames ?? new System.Collections.ObjectModel.ObservableCollection<string>(),
                StringComparer.OrdinalIgnoreCase);

            bool pinnedRemoved = false;
            foreach (var app in Apps.ToList())
            {
                if (app.IsFavorites || app.IsPlaceholder || app.IsDeleting) continue;
                if (!IsExcludedApp(app, ignored)) continue;
                if (!app.IsDynamic) pinnedRemoved = true;
                _ = RemoveExcludedAsync(app);
            }
            if (pinnedRemoved) SaveAppsConfig();

            // Снятые исключения: подобрать уже открытые окна (OnShellWindowCreated пропустит известные).
            RefreshRunningApps();
        }

        private bool IsExcludedApp(DockAppConfig app, HashSet<string> ignored)
        {
            if (ignored.Count == 0) return false;
            if (!string.IsNullOrEmpty(app.ProcessName) && ignored.Contains(app.ProcessName)) return true;
            if (!string.IsNullOrEmpty(app.Path) && ignored.Contains(System.IO.Path.GetFileNameWithoutExtension(app.Path))) return true;
            foreach (var h in app.WindowHandles.ToList())
            {
                string exe = GetProcessExecutablePath(h);
                if (!string.IsNullOrEmpty(exe) && ignored.Contains(System.IO.Path.GetFileNameWithoutExtension(exe))) return true;
            }
            return false;
        }

        private async System.Threading.Tasks.Task RemoveExcludedAsync(DockAppConfig app)
        {
            app.IsDeleting = true;
            PositionDock(skipAnimation: false);
            await AnimateItemContainerAsync(app, targetSize: 0, animateOpacity: true, durationMs: 350);
            Apps.Remove(app);
        }
    }
}
