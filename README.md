# Assist Quest Editor

Песочница и рабочий прототип переносимой системы создания и исполнения квестов.

Проект развивается отдельно от ETS2 Assist: сначала полностью отрабатываем архитектуру, редактор, симулятор, runtime и визуальные интерфейсы на искусственных данных. Подключение ETS2 Assist и других игровых источников данных происходит только после стабилизации контрактов.

Документация проекта находится в `MemoryAI/`.

## Сборка

Для обычной разработки запускайте:

    powershell -ExecutionPolicy Bypass -File .\compile.ps1

Скрипт очищает старую публикацию, восстанавливает зависимости и публикует self-contained приложение под Windows x64. Проверяется, что каталог публикации содержит ровно один файл:

    bin\Release\net10.0-windows\win-x64\publish\AssistQuestEditor.exe

HTML/CSS/JavaScript включаются в single-file публикацию.

## Регистрация файлов проекта в Windows

Откройте **Настройки** (шестерёнка в основном окне) и нажмите **«Зарегистрировать расширения»**.

Сейчас регистрируются только реальные standalone resources прототипа:

| Расширение | Редактор |
|---|---|
| `.aqquest` | Нодовый редактор |
| `.aqscene` | Редактор сцен и диалогов |

Регистрация выполняется для текущего пользователя в `HKCU\Software\Classes`, без прав администратора. Для каждого типа создаётся отдельный versioned ProgID, `OpenWithProgids`, команда открытия и `DefaultIcon`. citeturn802462search0turn802462search1turn802462search4

Файлы иконок хранятся постоянно в:

    %LOCALAPPDATA%\AssistQuestEditor\FileIcons

Приложение создаёт многобитные `.ico` с несколькими размерами (16–256 px) при регистрации и обновляет Shell через `SHChangeNotify(SHCNE_ASSOCCHANGED)`. Если другое приложение уже выбрано пользователем по умолчанию, регистрация не перехватывает этот выбор — новый тип лишь добавляется в `Open with`, что соответствует рекомендациям Windows по user-driven default applications. citeturn802462search0turn802462search2turn802462search7

Аргумент:

    AssistQuestEditor.exe --unregister-file-associations

удаляет созданные приложением ProgID и ссылки из `OpenWithProgids`, не ломая чужую ассоциацию, если пользователь уже выбрал другое приложение.

## Проверки

Те же проверки, которые выполняет CI, можно прогнать локально без GitHub:

    powershell -ExecutionPolicy Bypass -File .\ci\run_local.ps1

Скрипт повторяет шаги `ci.yml` (Playwright, Chromium, smoke, контракт, синтаксис JS, .NET build, domain tests) и печатает итоговую таблицу. При падении он завершается кодом 1 и называет упавшие проверки — как это делает `final_gate` в CI.

**Это основной барьер перед сборкой:** `pull.ps1` вызывает его автоматически и запускает `compile.ps1` только при успехе.

Автозапуск GitHub Actions по push отключён. Workflow `Проверки` запускается вручную (`workflow_dispatch`) и нужен для чистой среды `windows-latest`.

Каждый прогон перезаписывает отчёт `MemoryAI/LOGS/CI_errors.md`.

После публикации `compile.ps1` очищает `MemoryAI/LOGS`.
