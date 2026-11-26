# План: переделать статистику Image Browser на правила группировки

## Цель

Заменить текущую схему «один regex с именованными группами» на настраиваемый список явных правил группировки и переделать подачу: отдельное окно, карточки в две колонки, top-N значений с настоящим разворачиванием, примеры и понятная подсказка.

## Принятые решения

- Правила — настраиваемый список, сохраняется в настройках.
- Полный каталог типов: дата/время, расширение, папка, часть имени по разделителю, размерный бакет, regex.
- Правила по умолчанию: дата изменения по дням, расширение, папка.
- Редактирование правил — прямо в окне статистики (добавить/удалить/изменить параметры).
- Окно отдельное, модальное (`ShowDialog`), resizable; статистика — снимок текущего списка.
- Охват: текущий фильтр Image Browser по умолчанию + переключатель «Все файлы».
- Каждая карточка показывает top-10 значений; кнопка разворачивает полный список в ограниченной по высоте прокручиваемой области.
- Старый regex из `StatsRegexPattern` мигрирует в одно regex-правило, чтобы не потерять настройку.

## Текущее состояние

- `ShareX/TaskSettings.cs:537-543` — `ImageBrowserOptions.StatsRegexPattern`, `RecursiveSearch`.
- `ShareX/Presentation/ImageBrowser/ImageBrowserViewModel.cs:69-87` — модели `ImageBrowserGroupValue` / `ImageBrowserGroupStats` / `ImageBrowserRegexStats`.
- `ShareX/Presentation/ImageBrowser/ImageBrowserViewModel.cs:343-450` — `ComputeRegexStatsAsync`: матчинг только по имени файла, разбивка по именованным группам.
- `ShareX/Presentation/ImageBrowser/ImageBrowserWindow.axaml:310-341` — модалка `StatsDialog` внутри окна.
- `ShareX/Presentation/ImageBrowser/ImageBrowserWindow.axaml.cs:53-72, 477-722` — таймеры, отмена, ручная сборка строк статистики, `MaxDisplayedGroups`, `ValuesPerPage`.
- `ShareX/Localization/Strings.resx:1544-1624` — существующие строки статистики.
- Для визуала можно переиспользовать публичный `ShareX.HistoryLib.StatsMeter` (проект `ShareX` уже ссылается на `ShareX.HistoryLib`).

## Архитектура

### Новые файлы

1. `ShareX/Presentation/ImageBrowser/ImageBrowserStatsModels.cs`
   - `ImageBrowserStatsRuleKind`: `Date`, `Extension`, `Folder`, `FileNamePart`, `Size`, `Regex`.
   - `ImageBrowserStatsDateSource`: `Modified`, `Created`.
   - `ImageBrowserStatsDateBucket`: `DayOfWeek`, `Hour`, `Day`, `Week`, `Month`, `Year`.
   - `ImageBrowserStatsRule` — сериализуемая модель правила:
     ```csharp
     public ImageBrowserStatsRuleKind Kind;
     public ImageBrowserStatsDateSource DateSource = Modified;
     public ImageBrowserStatsDateBucket DateBucket = Day;
     public string Separator = "_";
     public int PartIndex = 1;          // 1-based; отрицательный — с конца; 0 недопустим
     public string RegexPattern = "";
     public string RegexGroup = "";     // пусто = всё совпадение; имя или номер группы
     ```
   - `CreateDefaultRules()` — дата изменения по дням, расширение, папка.
   - Результаты: `ImageBrowserStatsValue` (`Label`, `Count`, `Size`, `Share`, `IsNoValue`, `IsPeak`), `ImageBrowserStatsRuleResult` (`Rule`, `TotalFiles`, `TotalSize`, `Values`, `NoValueCount`, `NoValueSize`, `Error`), `ImageBrowserStatsSnapshot`.

2. `ShareX/Presentation/ImageBrowser/ImageBrowserStatsCalculator.cs`
   - `public static Task<ImageBrowserStatsSnapshot> ComputeAsync(IReadOnlyList<ImageFileInfo> files, string rootFolder, IReadOnlyList<ImageBrowserStatsRule> rules, CancellationToken token)`.
   - Считает каждое правило независимо, один проход по файлам на правило.
   - Regex компилируется один раз на правило, `RegexOptions.IgnoreCase | CultureInvariant`, timeout 500 ms.
   - Ошибка одного правила не ломает остальные: она попадает в `RuleResult.Error`.

3. `ShareX/Presentation/ImageBrowser/ImageBrowserStatsWindow.axaml` + `.axaml.cs`
   - Отдельное окно, `Width=1100`, `Height=800`, `MinWidth=900`, `MinHeight=600`, `WindowStartupLocation=CenterOwner`.
   - Тонкий code-behind: открытие, закрытие по Esc, обработчики кнопок.

4. `ShareX/Presentation/ImageBrowser/ImageBrowserStatsViewModel.cs`
   - `ObservableCollection<ImageBrowserStatsRuleCardViewModel> Cards`.
   - `UseFiltered` (по умолчанию `true`), `ScopeText`, `SummaryText`, `IsBusy`, `HasError`.
   - `AddRule(kind)`, `RemoveRule(card)`, `RecomputeAsync()`.
   - Дебаунс 250–300 ms на изменение параметров; отмена предыдущего расчёта.
   - Изменения правил сразу пишутся в `ImageBrowserOptions.StatsRules`.

5. `ImageBrowserStatsRuleCardViewModel` (в том же файле или отдельно)
   - Обёртка правила: `Title`, `Example`, `Result`, `Values`, `VisibleValues`, `IsExpanded`, `HasMore`, `ToggleText`, `ErrorText`.
   - Флаги видимости редакторов: `ShowDateOptions`, `ShowPartOptions`, `ShowRegexOptions`.

### Изменяемые файлы

- `ShareX/TaskSettings.cs`:
  - Добавить `public List<ImageBrowserStatsRule> StatsRules = ImageBrowserStatsRule.CreateDefaultRules();`
  - Оставить `StatsRegexPattern` как legacy-поле только для миграции; в UI его больше не использовать.
- `ShareX/Presentation/ImageBrowser/ImageBrowserViewModel.cs`:
  - Удалить старые stats-модели и `ComputeRegexStatsAsync`.
  - Добавить публичное представление всех файлов для режима «Все файлы» (например, `IReadOnlyList<ImageFileInfo> AllFiles => _allFiles;`).
- `ShareX/Presentation/ImageBrowser/ImageBrowserWindow.axaml`:
  - Удалить `ModalOverlay` и `StatsDialog`.
  - Кнопка «Статистика» остаётся в тулбаре.
- `ShareX/Presentation/ImageBrowser/ImageBrowserWindow.axaml.cs`:
  - Удалить `_statsTimer`, `_statsCancellation`, `_lastStats`, `_groupValueLimits`, `MaxDisplayedGroups`, `ValuesPerPage` и все методы `RenderStats` / `CreateStats*`.
  - `OnStatsClick` открывает `ImageBrowserStatsWindow` через `ShowDialog(this)`, передавая `_viewModel`, `Options` и путь корневой папки.

## Семантика правил

| Правило | Значение | Сортировка | Пример |
|---|---|---|---|
| Дата/время | `ModifiedAt` или `CreatedAt`, бакет: день недели, час, день, неделя, месяц, год | час — 0→23; день недели — от первого дня недели культуры; день/неделя/месяц/год — новые сверху | `27.11.2026`, `ноябрь 2026`, `14:00` |
| Расширение | расширение без точки, lower-case | по количеству убыв., затем по алфавиту | `png` |
| Папка | путь подпапки относительно корневой папки, `/`; корень — отдельная метка | по количеству убыв., затем по алфавиту | `2026/11` |
| Часть имени | имя без расширения, split по разделителю, часть N (1-based; отрицательная — с конца) | по количеству убыв., затем по алфавиту | `Screenshot_2026-11-27_14-30-00` + `_` + часть 2 → `2026-11-27` |
| Размер | фиксированные бакеты: `< 100 KB`, `100–500 KB`, `500 KB–1 MB`, `1–5 MB`, `5–20 MB`, `≥ 20 MB` | по возрастанию порога | `1–5 MB` |
| Regex | `FileName` целиком, pattern + группа (имя или номер; пусто = весь match) | по количеству убыв., затем по алфавиту | `^(?<app>.+?)_\d{4}-` + группа `app` |

Правила отображения значений:

- В каждой карточке есть бакет «Нет значения» для файлов, где правило не дало значения (нет совпадения regex, нет разделителя/части, пустое расширение и т.п.). Он показывается последним и участвует в общих счётчиках.
- Строка значения: метка, горизонтальный `StatsMeter`, количество, размер, доля в процентах.
- Бар строится по количеству; размер показывается числом. `IsPeak` — максимальное количество.
- Значения с одинаковым количеством сортируются по метке без учёта регистра.

## UI

### Шапка окна

- Заголовок «Статистика» + иконка.
- Строка охвата: «N из M файлов, размер X».
- Переключатель `Текущий фильтр` / `Все файлы`.
- Кнопка `Добавить правило` с `MenuFlyout` по типам правил.
- Кнопка `Закрыть`.

### Подсказка

Под шапкой — постоянная строка-подсказка, а не только плейсхолдер:

> Правила группируют текущий список файлов. Например, «Дата изменения → Месяц» покажет, сколько снимков сделано в каждом месяце.

Плюс у каждого правила — короткий пример:

- Дата: `Пример: 27.11.2026` / `Пример: ноябрь 2026`.
- Расширение: `Пример: png`.
- Папка: `Пример: 2026/11`.
- Часть имени: `Пример: Screenshot_2026-11-27_14-30-00 → часть 2 = 2026-11-27`.
- Размер: `Пример: 1–5 MB`.
- Regex: `Пример: ^(?<app>.+?)_\d{4}- , группа app`.

### Карточки правил

- Сетка в две колонки (`ItemsControl` + `UniformGrid Columns="2"` внутри `ScrollViewer`).
- Карточка: `Border` с фоном панели, рамкой и скруглением, как `section-card` в `ShareX.HistoryLib/Views/StatsWindow.axaml`.
- Шапка карточки: название правила, тип, кнопка удаления.
- Параметры — компактно в шапке/под шапкой, только для выбранного типа:
  - Дата: источник (изменён/создан) + бакет.
  - Часть имени: разделитель + номер части (`NumericUpDown`, без нуля).
  - Regex: pattern + группа.
  - Остальные — без параметров.
- Сводка: `{files} файлов · {size} · {values} значений`; отдельно `{n} без значения`, если есть.
- Список значений:
  - По умолчанию первые 10.
  - Если значений больше 10 — кнопка `Показать все ({n})`; по нажатию список разворачивается в прокручиваемую область с `MaxHeight≈340` и кнопка меняется на `Свернуть`.
  - Для списка использовать виртуализированный `ListBox` с локальным стилем без лишнего выделения, чтобы regex с тысячами уникальных значений не вешал окно.
  - Никаких текстовых заглушек «… ещё N значений»: только реальное разворачивание.
- Ошибка правила (невалидный regex, неверная группа) показывается текстом в карточке; остальные карточки продолжают работать.
- Пустой список правил — пустое состояние с кнопкой добавления.
- Пока идёт расчёт — `IsBusy`, блокировка повторного запуска и индикатор загрузки.

### Взаимодействие

- При открытии окно считает снимок `FilteredFiles` (или `AllFiles` при переключении).
- Любое изменение правила, добавление/удаление, переключение охвата запускает пересчёт с дебаунсом и отменой предыдущего.
- Esc закрывает окно.
- Клик по значению не фильтрует браузер — это вне рамок задачи.

## Сохранение и миграция

- `ImageBrowserOptions.StatsRules` хранит список правил глобально, как сейчас хранится regex.
- При загрузке:
  - если `StatsRules` равен `null` (старый JSON) — подставить `CreateDefaultRules()`;
  - пустой список — валидное состояние «пользователь удалил все правила», дефолты не возвращать;
  - если `StatsRegexPattern` непустой и такого regex-правила ещё нет — добавить `ImageBrowserStatsRuleKind.Regex` в конец списка;
  - после миграции очистить `StatsRegexPattern`, чтобы удалённое пользователем правило не возвращалось при следующем открытии;
  - старый `StatsRegexPattern` больше не показывать в UI.
- Enum-значения только добавлять в конец, не переставлять: они сериализуются числами.
- Удаление правил и правки параметров сохраняются сразу в объект настроек (как сейчас сохранялся regex).

## Локализация

- Новые ключи добавлять с префиксом `ImageBrowserWindow_Stats_*` в `ShareX/Localization/Strings.resx`.
- Переиспользовать существующие ключи, где смысл совпадает: `ImageBrowserWindow_Size`, `_Folder`, `_Close`, `_Total`, `_Filtered`, `_Statistics`.
- После добавления ключей синхронизировать `Strings.Designer.cs`:
  `pwsh -File Scripts/Resx.ps1 designer -Project ShareX`
- Перевести новые ключи по инструкции skill `sharex-resx` (`export`/`import`/`check` с префиксом `ImageBrowserWindow_Stats_`), затем прогнать `Scripts/ValidateTranslations.ps1` — валидатор требует наличие каждого ключа во всех каталогах.
- Старые stats-ключи (`Group_*`, `Groups_*`, `Matched`, `Unmatched`, `Regex_pattern`, `More_values_format`, `More_groups_format`) в этой задаче не удалять, чтобы не трогать все каталоги; удаление — отдельная уборка.

## Порядок работ

1. Добавить модели правил и `CreateDefaultRules()`, поле `StatsRules` в `ImageBrowserOptions`, миграцию `StatsRegexPattern`.
2. Написать `ImageBrowserStatsCalculator` с извлечением значений для всех типов правил, бакетом «нет значения», сортировкой и per-rule ошибками.
3. Добавить view model карточек и окна статистики: добавление/удаление правил, параметры, дебаунс, переключение охвата.
4. Свёрстать `ImageBrowserStatsWindow.axaml`: шапка, подсказка, сетка карточек, top-10 + expand, состояния загрузки/ошибки/пустоты.
5. Подключить кнопку Statistics, удалить старую модалку и весь старый stats-код из `ImageBrowserWindow`.
6. Удалить старые stats-модели/метод из `ImageBrowserViewModel`, добавить доступ к `AllFiles`.
7. Добавить строки в `Strings.resx`, синхронизировать Designer, перевести новые ключи.
8. Собрать проект и пройти ручной чек-лист.

## Крайние случаи и защита

- Regex: пустой pattern — карточка с подсказкой «введите pattern», без ложного совпадения всех файлов.
- Regex: невалидный pattern или несуществующая группа — ошибка только этой карточки.
- Regex timeout (500 ms на match) — ошибка карточки, окно не падает.
- Разделитель пустой, часть 0 или вне диапазона — значение попадает в «Нет значения».
- Файлов нет / фильтр ничего не нашёл — пустое состояние, а не пустые карточки.
- Путь подпапки вне корня — показывать имя папки, а не падать.
- Очень много уникальных значений — top-10 + виртуализированный развёрнутый список с ограничением высоты.
- До 20 000 файлов (`ImageBrowserViewModel.MaxFileCount`) — расчёт асинхронный, с отменой; при многих regex-правилах считать последовательно.
- Переключение темы — карточки и `StatsMeter` используют ресурсы темы.

## Проверка

- `dotnet build ShareX.sln -c Debug`.
- `pwsh -File Scripts/ValidateTranslations.ps1`.
- Ручной чек-лист:
  - Открыть статистику на папке скриншотов: видны три правила по умолчанию, дата и расширение не разнесены на 200 строк.
  - Развернуть/свернуть список дат, убедиться, что расширения остаются рядом и доступны без прокрутки через все даты.
  - Добавить каждое правило, проверить параметры и примеры.
  - Проверить, что сумма по значениям + «нет значения» сходится с охватом.
  - Переключить «Текущий фильтр» / «Все файлы».
  - Ввести невалидный regex и неверную группу.
  - Удалить все правила и добавить заново.
  - Перезапустить приложение: правила сохранились; старый `StatsRegexPattern` превратился в regex-правило.
  - Проверить светлую/тёмную тему и изменение размера окна.

## Вне рамок

- Клик по значению с фильтрацией Image Browser.
- Экспорт отчёта и дополнительные типы графиков (кроме горизонтальных баров).
- Немодальное «живое» окно статистики.
- Настраиваемые пороги размерных бакетов.
- Drag-and-drop сортировка правил.
- Удаление старых неиспользуемых строк локализации.

## TODO.md

Пункта про статистику Image Browser в `TODO.md` нет; изменения его не затрагивают. По правилам `AGENTS.md` / `sharex-todo-audit` оставить `TODO.md` без изменений.
