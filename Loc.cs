using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PlankWin
{
    // Локализация: ru / en / uk / de. Строки XAML берутся через {DynamicResource L_ключ}
    // (переключение на лету), строки кода — через Loc.T("ключ"), значения enum — через EnumLocConverter.
    public static class Loc
    {
        public static readonly string[] Codes = { "ru", "en", "uk", "de" };
        public static readonly string[] NativeNames = { "Русский", "English", "Українська", "Deutsch" };

        public static string Lang { get; private set; } = "en";
        private static int Idx => System.Array.IndexOf(Codes, Lang) is int i && i >= 0 ? i : 1;

        public static event System.Action? Changed;

        // Счётчик смены языка: привязки к (local:Loc.Version) пересчитываются при переключении.
        public static int Version { get; private set; }
        public static event System.EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

        // setting: "auto" или код языка. Авто: язык интерфейса Windows, если поддержан, иначе английский.
        public static void Apply(string? setting)
        {
            string code = string.IsNullOrEmpty(setting) || setting == "auto"
                ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                : setting;
            Lang = System.Array.IndexOf(Codes, code) >= 0 ? code : "en";

            var res = Application.Current?.Resources;
            if (res != null)
                foreach (var kv in S) res["L_" + kv.Key] = kv.Value[Idx];
            Version++;
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(Version)));
            Changed?.Invoke();
        }

        public static string T(string key) => S.TryGetValue(key, out var v) ? v[Idx] : key;

        private static readonly System.Collections.Generic.Dictionary<string, string[]> S = new()
        {
            ["set_title"] = new[] { "Настройки PlankWin", "PlankWin Settings", "Налаштування PlankWin", "PlankWin-Einstellungen" },
            ["tab_behavior"] = new[] { "Поведение и Положение", "Behavior & Position", "Поведінка й положення", "Verhalten & Position" },
            ["inst_offer"] = new[] {
                "Установить PlankWin на этот компьютер?\n\nПрограмма переедет в {0}, в меню Пуск появится ярлык, а удалить её можно будет через «Приложения» Windows. Текущий файл будет перенесён туда.\n\n«Нет» — работать из текущей папки (вопрос больше не появится, установить можно из меню в трее).",
                "Install PlankWin on this computer?\n\nThe app will move to {0}, a Start menu shortcut will be added, and it can be removed via Windows “Apps”. The current file will be moved there.\n\n“No” — keep running from the current folder (you won't be asked again; install later from the tray menu).",
                "Встановити PlankWin на цей комп'ютер?\n\nПрограма переїде до {0}, у меню Пуск з'явиться ярлик, а видалити її можна буде через «Програми» Windows. Поточний файл буде перенесено туди.\n\n«Ні» — працювати з поточної теки (питання більше не з'явиться, встановити можна з меню в треї).",
                "PlankWin auf diesem Computer installieren?\n\nDie App wird nach {0} verschoben, ein Startmenü-Eintrag wird angelegt und sie lässt sich über „Apps“ in Windows entfernen. Die aktuelle Datei wird dorthin verschoben.\n\n„Nein“ — aus dem aktuellen Ordner weiter ausführen (keine erneute Nachfrage; Installation später über das Tray-Menü)." },
            ["inst_failed"] = new[] { "Не удалось установить: {0}", "Installation failed: {0}", "Не вдалося встановити: {0}", "Installation fehlgeschlagen: {0}" },
            ["inst_menu"] = new[] { "Установить PlankWin…", "Install PlankWin…", "Встановити PlankWin…", "PlankWin installieren…" },
            ["uninst_menu"] = new[] { "Удалить PlankWin…", "Uninstall PlankWin…", "Видалити PlankWin…", "PlankWin deinstallieren…" },
            ["uninst_confirm"] = new[] { "Удалить PlankWin с этого компьютера?", "Remove PlankWin from this computer?", "Видалити PlankWin з цього комп'ютера?", "PlankWin von diesem Computer entfernen?" },
            ["uninst_data"] = new[] {
                "Удалить также настройки, закреплённые кнопки и избранное?\n\n«Нет» — оставить их на случай повторной установки.",
                "Also delete settings, pinned buttons and favorites?\n\n“No” — keep them in case you reinstall.",
                "Видалити також налаштування, закріплені кнопки та обране?\n\n«Ні» — залишити їх на випадок повторного встановлення.",
                "Auch Einstellungen, angeheftete Schaltflächen und Favoriten löschen?\n\n„Nein“ — für eine erneute Installation behalten." },
            ["uninst_done"] = new[] { "PlankWin удалён.", "PlankWin has been removed.", "PlankWin видалено.", "PlankWin wurde entfernt." },
            ["badges"] = new[] { "Счётчики уведомлений на иконках", "Notification counters on icons", "Лічильники сповіщень на іконках", "Benachrichtigungszähler auf Symbolen" },
            ["badges_hint"] = new[] {
                "Число берётся из заголовка окна: Telegram, Viber, WhatsApp и почта в браузере и т.п.",
                "The number is taken from the window title: Telegram, Viber, WhatsApp, web mail etc.",
                "Число береться із заголовка вікна: Telegram, Viber, WhatsApp, пошта в браузері тощо.",
                "Die Zahl stammt aus dem Fenstertitel: Telegram, Viber, WhatsApp, Web-Mail usw." },
            ["hk_label"] = new[] { "Горячие клавиши", "Hotkeys", "Гарячі клавіші", "Tastenkürzel" },
            ["hk_off"] = new[] { "Выключены", "Off", "Вимкнено", "Aus" },
            ["hk_hint"] = new[] { "1…9 — иконки по порядку (как клик), 0 — избранное.", "1…9 — icons in order (same as a click), 0 — favorites.", "1…9 — іконки по порядку (як клік), 0 — обране.", "1…9 — Symbole der Reihe nach (wie ein Klick), 0 — Favoriten." },
            ["hk_altgr"] = new[] {
                "Внимание: Ctrl+Alt — это AltGr. На раскладках с AltGr (немецкая, венгерская, польская…) перестанут печататься символы AltGr+цифра, например { [ ] }.",
                "Note: Ctrl+Alt is AltGr. On layouts with AltGr (German, Hungarian, Polish…) AltGr+digit characters such as { [ ] } will stop working.",
                "Увага: Ctrl+Alt — це AltGr. На розкладках з AltGr (німецька, угорська, польська…) перестануть друкуватися символи AltGr+цифра, наприклад { [ ] }.",
                "Achtung: Strg+Alt ist AltGr. Auf Layouts mit AltGr (Deutsch, Ungarisch, Polnisch…) funktionieren AltGr+Ziffer-Zeichen wie { [ ] } nicht mehr." },
            ["hk_busy"] = new[] { "Заняты другой программой или Windows: {0}. Выберите другое сочетание.", "Taken by another app or Windows: {0}. Pick another combination.", "Зайняті іншою програмою або Windows: {0}. Оберіть інше поєднання.", "Von einer anderen App oder Windows belegt: {0}. Bitte andere Kombination wählen." },
            ["crash_title"] = new[] { "PlankWin — отчёт об ошибке", "PlankWin — error report", "PlankWin — звіт про помилку", "PlankWin — Fehlerbericht" },
            ["crash_text"] = new[] {
                "В прошлый раз в PlankWin произошла ошибка. Если не сложно, скопируйте отчёт и перешлите автору — это поможет её исправить. В отчёте только версия программы и Windows, мониторы и техническое описание ошибки.",
                "PlankWin ran into an error last time. If you don't mind, copy the report and send it to the author — it helps fix the problem. It contains only the app and Windows versions, monitor setup and a technical error description.",
                "Минулого разу в PlankWin сталася помилка. Якщо не складно, скопіюйте звіт і перешліть авторові — це допоможе її виправити. У звіті лише версія програми й Windows, монітори та технічний опис помилки.",
                "Beim letzten Mal ist in PlankWin ein Fehler aufgetreten. Bitte kopieren Sie den Bericht und senden Sie ihn dem Autor — das hilft bei der Behebung. Er enthält nur App- und Windows-Version, Monitore und eine technische Fehlerbeschreibung." },
            ["crash_copy"] = new[] { "Скопировать", "Copy", "Скопіювати", "Kopieren" },
            ["crash_copied"] = new[] { "Скопировано ✓", "Copied ✓", "Скопійовано ✓", "Kopiert ✓" },
            ["crash_issue"] = new[] { "Открыть Issue на GitHub", "Open GitHub issue", "Відкрити Issue на GitHub", "GitHub-Issue öffnen" },
            ["crash_close"] = new[] { "Закрыть", "Close", "Закрити", "Schließen" },
            ["upd_title"] = new[] { "Обновление PlankWin", "PlankWin update", "Оновлення PlankWin", "PlankWin-Update" },
            ["upd_balloon"] = new[] { "Доступна версия {0}. Нажмите, чтобы обновить.", "Version {0} is available. Click to update.", "Доступна версія {0}. Натисніть, щоб оновити.", "Version {0} ist verfügbar. Zum Aktualisieren klicken." },
            ["upd_menu"] = new[] { "Обновить до {0}", "Update to {0}", "Оновити до {0}", "Auf {0} aktualisieren" },
            ["upd_check"] = new[] { "Проверить обновления", "Check for updates", "Перевірити оновлення", "Nach Updates suchen" },
            ["upd_uptodate"] = new[] { "У вас последняя версия ({0}).", "You have the latest version ({0}).", "У вас остання версія ({0}).", "Sie haben die neueste Version ({0})." },
            ["upd_prompt"] = new[] {
                "Доступна версия {0} (у вас {1}).\n\n{2}\n\nОбновить сейчас? PlankWin перезапустится.",
                "Version {0} is available (you have {1}).\n\n{2}\n\nUpdate now? PlankWin will restart.",
                "Доступна версія {0} (у вас {1}).\n\n{2}\n\nОновити зараз? PlankWin перезапуститься.",
                "Version {0} ist verfügbar (installiert: {1}).\n\n{2}\n\nJetzt aktualisieren? PlankWin wird neu gestartet." },
            ["upd_downloading"] = new[] { "Загружается обновление…", "Downloading update…", "Завантажується оновлення…", "Update wird heruntergeladen…" },
            ["upd_failed"] = new[] { "Не удалось обновить: {0}", "Update failed: {0}", "Не вдалося оновити: {0}", "Update fehlgeschlagen: {0}" },
            ["upd_noaccess"] = new[] {
                "Нет прав на запись в папку программы. Открыта страница релиза — скачайте файл вручную.",
                "No write access to the program folder. The release page has been opened — download the file manually.",
                "Немає прав на запис у теку програми. Відкрито сторінку релізу — завантажте файл вручну.",
                "Kein Schreibzugriff auf den Programmordner. Die Release-Seite wurde geöffnet — bitte manuell herunterladen." },
            ["upd_auto"] = new[] { "Проверять обновления автоматически", "Check for updates automatically", "Перевіряти оновлення автоматично", "Automatisch nach Updates suchen" },
            ["upd_version"] = new[] { "Версия {0}", "Version {0}", "Версія {0}", "Version {0}" },
            ["upd_dev"] = new[] { "сборка разработчика, автопроверка выключена", "developer build, auto-check disabled", "збірка розробника, автоперевірку вимкнено", "Entwickler-Build, Auto-Prüfung aus" },
            ["cfg_export"] = new[] { "Экспорт настроек…", "Export settings…", "Експорт налаштувань…", "Einstellungen exportieren…" },
            ["cfg_import"] = new[] { "Импорт настроек…", "Import settings…", "Імпорт налаштувань…", "Einstellungen importieren…" },
            ["cfg_export_done"] = new[] { "Настройки сохранены.", "Settings exported.", "Налаштування збережено.", "Einstellungen exportiert." },
            ["cfg_bad_file"] = new[] { "Это не файл настроек PlankWin.", "This is not a PlankWin settings file.", "Це не файл налаштувань PlankWin.", "Dies ist keine PlankWin-Einstellungsdatei." },
            ["cfg_import_confirm"] = new[] {
                "Текущие настройки, кнопки и избранное будут заменены (резервная копия сохранится рядом с конфигом). PlankWin перезапустится. Продолжить?",
                "Current settings, buttons and favorites will be replaced (a backup is kept next to the config). PlankWin will restart. Continue?",
                "Поточні налаштування, кнопки та обране буде замінено (резервна копія збережеться поруч із конфігом). PlankWin перезапуститься. Продовжити?",
                "Aktuelle Einstellungen, Schaltflächen und Favoriten werden ersetzt (eine Sicherung bleibt neben der Konfiguration). PlankWin wird neu gestartet. Fortfahren?" },
            ["sec_main"] = new[] { "Главные настройки", "General", "Головні налаштування", "Allgemein" },
            ["language"] = new[] { "Язык:", "Language:", "Мова:", "Sprache:" },
            ["lang_auto"] = new[] { "Авто (как в Windows)", "Auto (as in Windows)", "Авто (як у Windows)", "Automatisch (wie Windows)" },
            ["runAtStartup"] = new[] { "Запускать вместе с Windows", "Start with Windows", "Запускати разом з Windows", "Mit Windows starten" },
            ["dockMode"] = new[] { "Режим панели:", "Dock mode:", "Режим панелі:", "Dock-Modus:" },
            ["mode_always"] = new[] { "Всегда видима", "Always visible", "Завжди видима", "Immer sichtbar" },
            ["mode_auto"] = new[] { "Автоскрытие", "Auto-hide", "Автоприховування", "Automatisch ausblenden" },
            ["mode_smart"] = new[] { "Умное скрытие", "Smart hide", "Розумне приховування", "Intelligent ausblenden" },
            ["reserve"] = new[] { "Резервировать место на экране", "Reserve screen space", "Резервувати місце на екрані", "Bildschirmplatz reservieren" },
            ["reserve_hint"] = new[] { "Ограничение Windows: при изменении зарезервированной области Проводник может переставить ярлыки рабочего стола, в том числе между мониторами. Так же ведёт себя панель задач Windows.", "Windows limitation: when the reserved area changes, Explorer may rearrange desktop icons, including across monitors. The Windows taskbar behaves the same way.", "Обмеження Windows: під час зміни зарезервованої області Провідник може переставити ярлики робочого столу, зокрема між моніторами. Так само поводиться панель завдань Windows.", "Windows-Einschränkung: Ändert sich der reservierte Bereich, kann der Explorer Desktopsymbole neu anordnen – auch zwischen Monitoren. Die Windows-Taskleiste verhält sich genauso." },
            ["sec_timings"] = new[] { "Тайминги автоскрытия", "Auto-hide timing", "Таймінги автоприховування", "Timing für Ausblenden" },
            ["showDelay"] = new[] { "Задержка появления (мс):", "Show delay (ms):", "Затримка появи (мс):", "Einblendverzögerung (ms):" },
            ["hideDelay"] = new[] { "Задержка скрытия (мс):", "Hide delay (ms):", "Затримка приховування (мс):", "Ausblendverzögerung (ms):" },
            ["sec_geometry"] = new[] { "Геометрия", "Geometry", "Геометрія", "Geometrie" },
            ["floating"] = new[] { "Летающая панель (отступ от края)", "Floating dock (edge margin)", "Плаваюча панель (відступ від краю)", "Schwebendes Dock (Randabstand)" },
            ["adaptive"] = new[] { "Адаптивный размер (стягивание)", "Adaptive size (shrink to fit)", "Адаптивний розмір (стягування)", "Adaptive Größe (an Inhalt anpassen)" },
            ["position"] = new[] { "Расположение панели:", "Dock position:", "Розташування панелі:", "Dock-Position:" },
            ["alignment"] = new[] { "Выравнивание значков:", "Icon alignment:", "Вирівнювання значків:", "Symbolausrichtung:" },
            ["tab_preview"] = new[] { "Превью и Аэро", "Previews & Aero", "Превʼю та Аеро", "Vorschau & Aero" },
            ["sec_previews"] = new[] { "Превью окон", "Window previews", "Превʼю вікон", "Fenstervorschau" },
            ["showPreviews"] = new[] { "Показывать превью окон при наведении", "Show window previews on hover", "Показувати превʼю вікон під час наведення", "Fenstervorschau beim Überfahren anzeigen" },
            ["previewDelay"] = new[] { "Задержка превью (мс):", "Preview delay (ms):", "Затримка превʼю (мс):", "Vorschauverzögerung (ms):" },
            ["previewOrient"] = new[] { "Ориентация превью:", "Preview orientation:", "Орієнтація превʼю:", "Vorschau-Ausrichtung:" },
            ["orient_auto"] = new[] { "Авто (по стороне панели)", "Auto (by dock side)", "Авто (за стороною панелі)", "Automatisch (nach Dock-Seite)" },
            ["orient_h"] = new[] { "Горизонтальное", "Horizontal", "Горизонтальна", "Horizontal" },
            ["orient_grid"] = new[] { "Сетка (квадратом)", "Grid (square)", "Сітка (квадратом)", "Raster (quadratisch)" },
            ["orient_v"] = new[] { "Вертикальное", "Vertical", "Вертикальна", "Vertikal" },
            ["sec_aero"] = new[] { "Матовый слой (Аэро)", "Frosted layer (Aero)", "Матовий шар (Аеро)", "Milchglas-Ebene (Aero)" },
            ["dim"] = new[] { "Затемнение:", "Dimming:", "Затемнення:", "Abdunkeln:" },
            ["blur"] = new[] { "Размытие:", "Blur:", "Розмиття:", "Unschärfe:" },
            ["duration"] = new[] { "Длительность (мс):", "Duration (ms):", "Тривалість (мс):", "Dauer (ms):" },
            ["easing"] = new[] { "Кривая анимации:", "Animation curve:", "Крива анімації:", "Animationskurve:" },
            ["ease_linear"] = new[] { "Линейно", "Linear", "Лінійно", "Linear" },
            ["ease_in"] = new[] { "EaseIn (разгон)", "EaseIn (accelerate)", "EaseIn (розгін)", "EaseIn (beschleunigen)" },
            ["ease_out"] = new[] { "EaseOut (торможение)", "EaseOut (decelerate)", "EaseOut (гальмування)", "EaseOut (abbremsen)" },
            ["ease_inout"] = new[] { "EaseInOut (плавно)", "EaseInOut (smooth)", "EaseInOut (плавно)", "EaseInOut (sanft)" },
            ["ease_sine"] = new[] { "Синусоида", "Sine", "Синусоїда", "Sinus" },
            ["tint"] = new[] { "Оттенок слоя (RGB):", "Layer tint (RGB):", "Відтінок шару (RGB):", "Ebenenfarbton (RGB):" },
            ["tab_fav"] = new[] { "Избранное", "Favorites", "Обране", "Favoriten" },
            ["sec_fav"] = new[] { "Избранное (папки)", "Favorites (folders)", "Обране (теки)", "Favoriten (Ordner)" },
            ["favEnable"] = new[] { "Включить избранное", "Enable favorites", "Увімкнути обране", "Favoriten aktivieren" },
            ["favLayout"] = new[] { "Раскладка избранного:", "Favorites layout:", "Розкладка обраного:", "Favoriten-Layout:" },
            ["layout_list"] = new[] { "Список", "List", "Список", "Liste" },
            ["layout_grid"] = new[] { "Сетка плиток", "Tile grid", "Сітка плиток", "Kachelraster" },
            ["favOpen"] = new[] { "Открытие избранного:", "Open favorites:", "Відкриття обраного:", "Favoriten öffnen:" },
            ["open_click"] = new[] { "По клику", "On click", "Клацанням", "Per Klick" },
            ["open_hover"] = new[] { "По наведению", "On hover", "Наведенням", "Beim Überfahren" },
            ["tab_style"] = new[] { "Стиль и Оформление", "Style & Appearance", "Стиль і оформлення", "Stil & Aussehen" },
            ["sec_sizes"] = new[] { "Размеры (пиксели)", "Sizes (pixels)", "Розміри (пікселі)", "Größen (Pixel)" },
            ["thickness"] = new[] { "Толщина панели:", "Dock thickness:", "Товщина панелі:", "Dock-Dicke:" },
            ["iconSize"] = new[] { "Размер иконок:", "Icon size:", "Розмір іконок:", "Symbolgröße:" },
            ["iconGap"] = new[] { "Зазор между иконками:", "Icon spacing:", "Проміжок між іконками:", "Symbolabstand:" },
            ["edgeMargin"] = new[] { "Отступ от экрана:", "Screen margin:", "Відступ від екрана:", "Bildschirmabstand:" },
            ["innerPad"] = new[] { "Внутренний зазор:", "Inner padding:", "Внутрішній проміжок:", "Innenabstand:" },
            ["corner"] = new[] { "Скругление углов:", "Corner radius:", "Заокруглення кутів:", "Eckenradius:" },
            ["sec_colors"] = new[] { "Цвета (HEX)", "Colors (HEX)", "Кольори (HEX)", "Farben (HEX)" },
            ["bgColor"] = new[] { "Цвет фона (RGB):", "Background color (RGB):", "Колір тла (RGB):", "Hintergrundfarbe (RGB):" },
            ["followWin"] = new[] { "Цвет как у Windows (авто)", "Match Windows color (auto)", "Колір як у Windows (авто)", "Windows-Farbe übernehmen (auto)" },
            ["accentShadow"] = new[] { "Акцент Windows в тени панели", "Windows accent in dock shadow", "Акцент Windows у тіні панелі", "Windows-Akzent im Dock-Schatten" },
            ["accentInt"] = new[] { "Интенсивность акцента:", "Accent intensity:", "Інтенсивність акценту:", "Akzentintensität:" },
            ["opacity"] = new[] { "Прозрачность:", "Opacity:", "Прозорість:", "Deckkraft:" },
            ["tab_excl"] = new[] { "Исключения", "Exclusions", "Винятки", "Ausnahmen" },
            ["sec_filter"] = new[] { "Фильтр приложений", "App filter", "Фільтр застосунків", "App-Filter" },
            ["filter_hint1"] = new[] { "Отметьте тумблером приложения, которые нужно скрыть с панели.", "Toggle the apps you want to hide from the dock.", "Позначте перемикачем застосунки, які треба приховати з панелі.", "Schalten Sie die Apps ein, die im Dock ausgeblendet werden sollen." },
            ["filter_hint2"] = new[] { "(Здесь показаны уже скрытые программы, а также все запущенные сейчас)", "(Shows already hidden apps and everything currently running)", "(Тут показано вже приховані програми, а також усі запущені зараз)", "(Zeigt bereits ausgeblendete und alle laufenden Programme)" },
            ["running"] = new[] { "Запущено", "Running", "Запущено", "Läuft" },
            ["refreshList"] = new[] { "Обновить список запущенных окон", "Refresh running windows", "Оновити список запущених вікон", "Laufende Fenster aktualisieren" },
            ["done"] = new[] { "Готово", "Done", "Готово", "Fertig" },
            ["m_newWindow"] = new[] { "Новое окно", "New window", "Нове вікно", "Neues Fenster" },
            ["m_recent"] = new[] { "Недавнее", "Recent", "Нещодавні", "Zuletzt verwendet" },
            ["rc_tasks"] = new[] { "Задачи", "Tasks", "Завдання", "Aufgaben" },
            ["rc_session"] = new[] { "сессия", "session", "сесія", "Sitzung" },
            ["rc_open"] = new[] { "открыт", "open", "відкрито", "geöffnet" },
            ["rc_missing"] = new[] { "Файл не найден", "File not found", "Файл не знайдено", "Datei nicht gefunden" },
            ["rc_pin"] = new[] { "Закрепить в списке", "Pin to list", "Закріпити у списку", "An Liste anheften" },
            ["rc_unpin"] = new[] { "Открепить", "Unpin from list", "Відкріпити", "Von Liste lösen" },
            ["rc_folder"] = new[] { "Открыть расположение", "Open file location", "Відкрити розташування", "Dateispeicherort öffnen" },
            ["m_runAdmin"] = new[] { "Запуск от имени администратора", "Run as administrator", "Запуск від імені адміністратора", "Als Administrator ausführen" },
            ["m_addFolderFav"] = new[] { "Добавить папку в избранное", "Add folder to favorites", "Додати теку до обраного", "Ordner zu Favoriten hinzufügen" },
            ["m_pin"] = new[] { "Закрепить на панели", "Pin to dock", "Закріпити на панелі", "An Dock anheften" },
            ["m_unpin"] = new[] { "Открепить от панели", "Unpin from dock", "Відкріпити від панелі", "Vom Dock lösen" },
            ["m_minimizeAll"] = new[] { "Свернуть окна", "Minimize windows", "Згорнути вікна", "Fenster minimieren" },
            ["m_close"] = new[] { "Закрыть", "Close", "Закрити", "Schließen" },
            ["m_closeAll"] = new[] { "Закрыть все окна", "Close all windows", "Закрити всі вікна", "Alle Fenster schließen" },
            ["m_hide"] = new[] { "Не показывать в доке", "Don't show in dock", "Не показувати в доку", "Nicht im Dock anzeigen" },
            ["m_hideTip"] = new[] { "Скрыть программу с панели", "Hide this app from the dock", "Приховати програму з панелі", "App aus dem Dock ausblenden" },
            ["m_minimize"] = new[] { "Свернуть", "Minimize", "Згорнути", "Minimieren" },
            ["m_restore"] = new[] { "Восстановить", "Restore", "Відновити", "Wiederherstellen" },
            ["m_maximize"] = new[] { "Развернуть", "Maximize", "Розгорнути", "Maximieren" },
            ["preview_minimized"] = new[] { "свёрнуто", "minimized", "згорнуто", "minimiert" },
            ["fav_empty"] = new[] { "Нет папок. Перетащите папку на звезду.", "No folders. Drag a folder onto the star.", "Немає тек. Перетягніть теку на зірку.", "Keine Ordner. Ziehen Sie einen Ordner auf den Stern." },
            ["favFilesEnable"] = new[] { "Избранные файлы (отдельная кнопка)", "Favorite files (separate button)", "Обрані файли (окрема кнопка)", "Favorisierte Dateien (eigene Schaltfläche)" },
            ["fav_folders_title"] = new[] { "Избранное", "Favorites", "Обране", "Favoriten" },
            ["fav_files_title"] = new[] { "Избранные файлы", "Favorite files", "Обрані файли", "Favorisierte Dateien" },
            ["fav_files_empty"] = new[] { "Нет файлов. Перетащите файл на эту кнопку.", "No files. Drag a file onto this button.", "Немає файлів. Перетягніть файл на цю кнопку.", "Keine Dateien. Ziehen Sie eine Datei auf diese Schaltfläche." },
            ["fav_addFiles"] = new[] { "Добавить файлы…", "Add files…", "Додати файли…", "Dateien hinzufügen…" },
            ["f_open"] = new[] { "Открыть", "Open", "Відкрити", "Öffnen" },
            ["f_showInFolder"] = new[] { "Показать в папке", "Show in folder", "Показати в теці", "Im Ordner anzeigen" },
            ["m_addFileFav"] = new[] { "Добавить файл в избранное", "Add file to favorites", "Додати файл до обраного", "Datei zu Favoriten hinzufügen" },
            ["rc_addFav"] = new[] { "В избранные файлы", "Add to favorite files", "До обраних файлів", "Zu favorisierten Dateien" },
            ["f_rename"] = new[] { "Переименовать для панели…", "Rename for the dock…", "Перейменувати для панелі…", "Für das Dock umbenennen…" },
            ["f_restoreName"] = new[] { "Вернуть оригинальное имя", "Restore original name", "Повернути оригінальну назву", "Originalnamen wiederherstellen" },
            ["f_missing"] = new[] { "Файл не найден", "File not found", "Файл не знайдено", "Datei nicht gefunden" },
            ["fav_remove"] = new[] { "Убрать из избранного", "Remove from favorites", "Прибрати з обраного", "Aus Favoriten entfernen" },
            ["fav_addFolder"] = new[] { "Добавить папку…", "Add folder…", "Додати теку…", "Ordner hinzufügen…" },
            ["fav_dlgTitle"] = new[] { "Добавить в избранное", "Add to favorites", "Додати до обраного", "Zu Favoriten hinzufügen" },
            ["tray_title"] = new[] { "⚙ PlankWin (Настройки: Ctrl+Shift+ПКМ)", "⚙ PlankWin (Settings: Ctrl+Shift+Right-click)", "⚙ PlankWin (Налаштування: Ctrl+Shift+ПКМ)", "⚙ PlankWin (Einstellungen: Strg+Umschalt+Rechtsklick)" },
            ["tray_monitor"] = new[] { "Монитор", "Monitor", "Монітор", "Monitor" },
            ["tray_primary"] = new[] { "основной", "primary", "основний", "Hauptmonitor" },
            ["tray_off"] = new[] { "выкл.", "off", "вимк.", "aus" },
            ["tray_panelHere"] = new[] { "Панель на этом мониторе", "Dock on this monitor", "Панель на цьому моніторі", "Dock auf diesem Monitor" },
            ["tray_mode"] = new[] { "Режим панели", "Dock mode", "Режим панелі", "Dock-Modus" },
            ["tray_forceHide"] = new[] { "Принудительно скрыть панель", "Force hide dock", "Примусово приховати панель", "Dock erzwungen ausblenden" },
            ["tray_settings"] = new[] { "Настройки", "Settings", "Налаштування", "Einstellungen" },
            ["tray_exit"] = new[] { "Выход", "Exit", "Вихід", "Beenden" },
            ["err_launch"] = new[] { "Ошибка запуска:", "Launch error:", "Помилка запуску:", "Startfehler:" },
            ["err_readApps"] = new[] { "Ошибка чтения конфигурации приложений:", "Error reading app configuration:", "Помилка читання конфігурації застосунків:", "Fehler beim Lesen der App-Konfiguration:" },
            ["err_saveApps"] = new[] { "Ошибка сохранения apps:", "Error saving apps:", "Помилка збереження apps:", "Fehler beim Speichern der Apps:" },
            ["DockPosition_Bottom"] = new[] { "Снизу", "Bottom", "Знизу", "Unten" },
            ["DockPosition_Top"] = new[] { "Сверху", "Top", "Згори", "Oben" },
            ["DockPosition_Left"] = new[] { "Слева", "Left", "Ліворуч", "Links" },
            ["DockPosition_Right"] = new[] { "Справа", "Right", "Праворуч", "Rechts" },
            ["DockAlignment_Center"] = new[] { "По центру", "Center", "По центру", "Zentriert" },
            ["DockAlignment_Start"] = new[] { "В начале", "Start", "На початку", "Am Anfang" },
            ["DockAlignment_End"] = new[] { "В конце", "End", "В кінці", "Am Ende" },
        };
    }

    // Перевод значения enum: ключ "ИмяТипа_Значение". values[0] — значение, values[1] — Loc.Version (для обновления).
    public class EnumLocConverter : IMultiValueConverter
    {
        public object Convert(object[] values, System.Type targetType, object parameter, CultureInfo culture)
        {
            var v = values.Length > 0 ? values[0] : null;
            if (v == null) return string.Empty;
            string key = v.GetType().Name + "_" + v;
            string t = Loc.T(key);
            return t == key ? v.ToString() ?? string.Empty : t;
        }

        public object[] ConvertBack(object value, System.Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new System.NotSupportedException();
    }
}
