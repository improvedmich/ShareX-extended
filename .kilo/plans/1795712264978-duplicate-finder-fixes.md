# Duplicate Finder: доведение до ТЗ (по имени / по SHA-256)

## Цель

Довести новый инструмент «Поиск дубликатов файлов» (`ShareX.Tools/Tools/DuplicateFinder/`, сейчас untracked) до рабочего состояния по ТЗ:

- поиск дубликатов **по имени файла**;
- поиск дубликатов **по SHA-256 хешу**.

Интеграция (пункт Tools-меню, `ToolsIntegration`, строки меню) уже сделана и компилируется. Нужно исправить функциональные баги и недоделки в самом окне/VM.

## Решения (согласовано/принято)

- **ByName**: группировка только по имени файла, **с учётом регистра** (`StringComparer.Ordinal`), без проверки размера/содержимого — буквально по ТЗ. «Можно освободить» в этом режиме — оценка (сумма минус самый большой файл группы).
- **ByHash**: SHA-256 по содержимому. Предварительная группировка по размеру — это только отсев заведомо невозможных дубликатов: если SHA-256 совпадает, то размеры файлов обязательно совпадают, поэтому файл с уникальным размером не может иметь дубликат по содержимому и его можно не хешировать. Это не предположение «одинаковый размер = одинаковое содержимое»: **все** файлы, у которых есть хотя бы один «сосед» по размеру, хешируются полностью, и совпадением считается только равный SHA-256.
- Сканирование всегда выполняется в фоне; UI не блокируется; Stop отменяет скан.
- Явная кнопка **Scan**; Browse по-прежнему сразу запускает скан; drag&drop папки — устанавливает папку и сразу запускает скан.
- Настройки между запусками не сохраняем (вне ТЗ).
- Новые строки локализации добавляем в нейтральный `Strings.resx` и `Strings.ru.resx`; остальные языки падают на английский fallback (как принято в репозитории).

## Затрагиваемые файлы

- `ShareX.Tools/Tools/DuplicateFinder/DuplicateFinderViewModel.cs`
- `ShareX.Tools/Tools/DuplicateFinder/DuplicateFinderWindow.axaml`
- `ShareX.Tools/Tools/DuplicateFinder/DuplicateFinderWindow.axaml.cs`
- `ShareX.Tools/Localization/Strings.resx`, `Strings.ru.resx`, `Strings.Designer.cs`
- (опционально) `.gitignore`
- без изменений: `ShareX.Tools/Integration/ToolsIntegration.cs`, `ShareX/Presentation/MainWindow/MainMenuBuilder.cs`, `ShareX/TaskHelpers.cs`, `ShareX/Localization/*` — уже корректны.

## Задачи (по порядку)

### 1. Комбобокс режима поиска (сейчас не работает)

`DuplicateFinderWindow.axaml:52` биндит `SelectedItem` к `Mode` (enum), а `ItemsSource` — `SearchModeItem` (`ViewModel:72`). Выбор режима не применяется.

- В VM заменить `DuplicateFinderSearchMode _mode` на:
  `[ObservableProperty] private SearchModeItem _selectedMode = DuplicateFinderSearchModeExtensions.All[0];`
- Скан использовать `SelectedMode.Mode`.
- В XAML: `SelectedItem="{Binding SelectedMode}"`.
- Добавить `partial void OnSelectedModeChanged(SearchModeItem value)` — сбрасывать результаты/статус (`Groups = []`, `TotalDuplicates = 0`, `TotalReclaimable = 0`, `HasScanned = false`, `StatusText = ""`), чтобы старые результаты не выглядели результатами другого режима.

### 2. Кнопка Scan и запуск скана

Сейчас `ScanCommand` есть, но нигде не привязан; папку из drag&drop/ручного ввода запустить нельзя.

- В XAML рядом с Browse добавить кнопку: `Command="{Binding ScanCommand}"`, `IsEnabled="{Binding CanScan}"`, текст из новой строки `DuplicateFinderWindow_Scan`.
- `BrowseFolderAsync` оставить с автосканом после выбора.
- `SetFolder` оставить без скана; в `OnDrop` после `SetFolder` вызывать `ScanCommand.Execute(null)`.

### 3. Drag & drop (сейчас не работает)

`DuplicateFinderWindow.axaml.cs:85`: условие инвертировано (`!_viewModel.IsIdle`), а `SetFolder` при сканировании отказывает — drop не делает ничего.

- Исправить на `if (_viewModel.IsIdle && folder != null)` (эталон: `DirectoryIndexerWindow.axaml.cs:134`).
- После `SetFolder` запускать скан (см. п.2).
- `OnDragOver` уже корректен.

### 4. Фоновое сканирование, прогресс, отмена

Сейчас `FindByNameAsync`/`FindByHashAsync` выполняют перебор и хеширование в UI-потоке (`ViewModel:213-339`) — окно зависает, Stop не нажимается.

- Сделать `FindByName`/`FindByHash` синхронными методами и запускать их через `await Task.Run(...)`.
- Прогресс/статус передавать через `System.Progress<ScanProgress>` (создавать на UI-потоке до `Task.Run`):
  `private readonly record struct ScanProgress(double Percent, string Status);`
- В `ScanAsync`: `IProgress<ScanProgress> progress = new System.Progress<ScanProgress>(p => { Progress = p.Percent; StatusText = p.Status; });`
- Оставить `CancellationTokenSource` и `Stop`; в `finally` — dispose CTS, `IsScanning = false`, `Progress = 0`.
- Обработка: `OperationCanceledException` → «остановлено», `Exception` → «ошибка» + `ToolsDiagnostics.ReportWarning`.
- После успешного скана (включая 0 результатов): `HasScanned = true`, итоговые `Groups`/`TotalDuplicates`/`TotalReclaimable` выставлять на UI-потоке.

### 5. Надёжный обход папок

`ViewModel:365-423`: `Directory.EnumerateDirectories` вне try — одна недоступная папка роняет весь скан; рекурсия следует по junction/symlink (риск цикла).

- Использовать `EnumerationOptions`:
  - файлы: `RecurseSubdirectories = false`, `IgnoreInaccessible = true`, `AttributesToSkip = SkipHiddenItems ? Hidden|System : 0`;
  - папки: `IgnoreInaccessible = true`, `AttributesToSkip = ReparsePoint | (SkipHiddenItems ? Hidden|System : 0)` — reparse point'ы пропускать всегда.
- Каждый вызов `EnumerateFiles`/`EnumerateDirectories` обернуть в `try/catch` (`UnauthorizedAccessException`, `IOException`, `DirectoryNotFoundException`, `SecurityException`) и продолжать обход.
- Логику глубины сохранить: `0` = без ограничений, иначе рекурсия только при `currentDepth < maxDepth`.
- Статус на фазе сбора: `DuplicateFinderViewModel_Reading_files` с количеством найденных файлов; прогресс-бар на этой фазе не двигать.
- Если файлов 0 — `StatusText = DuplicateFinderViewModel_No_files_in_folder` (строка уже есть), пустые результаты.

### 6. ByName

- Группировка по `Path.GetFileName(file)`; размер через `FileInfo.Length`; ошибки по отдельному файлу (`UnauthorizedAccessException`, `FileNotFoundException`, `IOException`) пропускать.
- Компаратор имён: `StringComparer.Ordinal` — строгое посимвольное совпадение, `File.txt` и `file.txt` считаются разными именами. Пути в результатах показываются как есть, регистр не нормализуется.
- В группы только `Count > 1`; `ReclaimableSize = сумма - max`.
- Прогресс 0–100 по числу файлов.
- Сортировка групп по `ReclaimableSize` убыванию.

### 7. ByHash

- Сначала группировка по размеру (только `FileInfo.Length`), пропуская ошибки по файлам; прогресс до ~30%, статус `Reading_files`.
- Хешировать только группы размера `Count > 1`; файлы с уникальным размером не хешируются, т.к. дубликата по содержимому у них быть не может (равный SHA-256 ⇒ равный размер). Файлы одного размера **не считаются** дубликатами: для них обязателен полный SHA-256, и в группу попадают только совпавшие хеши.
- `ComputeSha256(string path, CancellationToken ct)`: заменить текущую реализацию (неиспользуемый `SHA256.Create()` + `SHA256.HashData(stream)`), читать поток чанками (например 1 МБ) с `ct.ThrowIfCancellationRequested()` на каждом чанке и `TransformBlock`/`TransformFinalBlock` — так Stop срабатывает даже на больших файлах.
- Ловить `UnauthorizedAccessException`, `FileNotFoundException`, `IOException`; при ошибке файл не учитывать.
- Группировка по хешу с `StringComparer.Ordinal`; в группу только `Count > 1`; `ReclaimableSize = сумма - размер первого файла`.
- Прогресс 30–100 по числу групп размера; статус `Hashing_files`.
- В модель группы добавить хеш для отображения: `DuplicateGroup(..., string? Hash = null)` + `HashText`/`HasHash` (см. п.8); `Title` — имя первого файла группы.

### 8. Отображение и локализация в XAML/VM

- Убрать захардкоженный английский:
  - `axaml:122` `"{0} duplicates found"` → свойство VM `TotalDuplicatesText` (новая строка `DuplicateFinderViewModel_Total_duplicates`);
  - `axaml:234` `"Total reclaimable space: {0}"` → свойство VM `TotalReclaimableDescription` (новая строка `DuplicateFinderWindow_Total_reclaimable`), внутри форматировать `TotalReclaimableText` (`ToSizeString`).
- Добавить уведомления:
  - `[NotifyPropertyChangedFor(nameof(TotalReclaimableText))]` и `[NotifyPropertyChangedFor(nameof(TotalReclaimableDescription))]` к `_totalReclaimable`;
  - `[NotifyPropertyChangedFor(nameof(TotalDuplicatesText))]` к `_totalDuplicates`;
  - `HasStatus => !string.IsNullOrWhiteSpace(StatusText)` + уведомление от `_statusText`; показывать `StatusText` в футере и после окончания скана (например, `TextBlock` рядом с итогом, `IsVisible="{Binding HasStatus}"`).
- Пустое состояние показывать только когда не идёт скан: `ShowEmptyState => IsIdle && !HasResults`; уведомлять от `_isScanning`, `_totalDuplicates`; в XAML заменить `IsVisible="{Binding !HasResults}"` на `ShowEmptyState`.
- `NoDuplicatesMessage`: пока скан ни разу не запускался (`HasScanned == false`) — новая строка `DuplicateFinderViewModel_Press_scan`; после скана без результатов — `No_duplicates_found`. Сбрасывать `HasScanned` при смене папки/режима.
- В карточке группы при `HasHash` показывать строку `HashText` (новая строка `DuplicateFinderViewModel_Hash`, моноширинно, вторичный цвет).
- Убрать несуществующие CSS-классы кнопок `primary`/`danger`/`compact` (стилей нет в `ToolStyles.axaml`/`ControlStyles.axaml`); использовать стиль кнопок по умолчанию. (Не добавлять новые глобальные стили в рамках этой задачи.)
- Проверить читаемость в обеих темах (Main/Secondary brush'ы уже используются).

### 9. Строки локализации

В `ShareX.Tools/Localization/Strings.resx` и `Strings.ru.resx` (+ соответствующие свойства в `Strings.Designer.cs`):

- `DuplicateFinderWindow_Scan` — "Scan" / "Сканировать";
- `DuplicateFinderWindow_Total_reclaimable` — "Total reclaimable space: {0}" / "Всего можно освободить: {0}";
- `DuplicateFinderViewModel_Total_duplicates` — "{0} duplicate files" / "{0} файлов-дубликатов";
- `DuplicateFinderViewModel_Hash` — "SHA-256: {0}" / "SHA-256: {0}";
- `DuplicateFinderViewModel_Press_scan` — "Select a folder and press Scan." / "Выберите папку и нажмите «Сканировать».";
- удалить неиспользуемую `DuplicateFinderViewModel_Processing_file` (и её свойство в Designer), если не будет использована.

`Strings.Designer.cs` править согласованно с resx (в репозитории он обновляется вручную/генератором).

### 10. Git-гигиена

- Добавить в индекс `ShareX.Tools/Tools/DuplicateFinder/` (сейчас untracked — `git commit -a` его пропустит) вместе с изменёнными resx/Designer.
- `.gigacode_vsc/` — посторонний локальный артефакт; не коммитить. При желании добавить `.gigacode_vsc/` в `.gitignore`.
- Коммит — только по отдельному запросу пользователя.

## Риски и краевые случаи

- Очень большие файлы: остановка должна происходить между чанками (п.7), иначе Stop «зависает» на одном файле.
- Junction/symlink и недоступные папки не должны прерывать скан (п.5).
- Регистр в именах: `StringComparer.Ordinal` — `File.txt` и `file.txt` в ByName не попадут в одну группу (осознанное решение; в ByHash они будут найдены, если содержимое идентично).
- Одинаковые имена с разным содержимым в ByName — это ожидаемое поведение; цифра «освобождаемо» оценочная.
- ByHash не даёт ложных пропусков из-за отсева по размеру: файлы с уникальным размером физически не могут иметь дубликат по содержимому.
- Файл удалён/переименован во время скана — пропускать, не ронять скан.
- Повторный запуск скана: старый CTS отменять/освобождать перед новым.

## Проверка

Сборка:

- `dotnet build ShareX.Tools/ShareX.Tools.csproj -v q --nologo`
- `dotnet build ShareX/ShareX.csproj -v q --nologo`

Ручные сценарии:

1. Tools → «Поиск дубликатов файлов...»: окно открывается, меню и заголовок локализованы (en/ru).
2. Browse → папка выбрана → скан стартовал; кнопка Scan пересканирует; Stop останавливает, статус «сканирование остановлено».
3. Перетащить папку в окно → папка установлена и скан стартовал; при активном скане drop игнорируется.
4. ByName на папке с одинаковыми именами разных размеров → группы по имени; имена, отличающиеся только регистром (`A.txt` / `a.txt`), в одну группу не попадают. ByHash на той же папке → только реальные совпадения содержимого (включая файлы с разными именами); переключение режима очищает старые результаты.
5. Skip hidden + Max depth (0 и >0) дают ожидаемый набор файлов.
6. Папка с недоступной подпапкой/junction: скан завершается без ошибки и бесконечной рекурсии.
7. Большой файл (гигабайт+): UI не зависает, прогресс/статус обновляются, Stop срабатывает быстро.
8. Размеры в шапке/футере отформатированы (`KB/MB/GB`), итог обновляется после скана; копирование пути и «открыть папку» работают.
9. Смена темы Main/Secondary — текст и иконки читаемы; пустое состояние не висит поверх идущего скана.
10. В RU-локали нет английских захардкоженных подписей.

## Вне scope

- Сохранение настроек инструмента между запусками.
- Параллельное хеширование/многопоточность сверх одного фонового потока.
- Переводы на языки кроме ru (fallback на английский).
- Изменения в остальных инструментах и в интеграции меню.
