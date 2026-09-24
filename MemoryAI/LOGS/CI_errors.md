# Отчёт локальных проверок CI

Файл перезаписывается каждым прогоном `ci/run_local.ps1`.
CI его не проверяет и не загружает: это диагностический материал для агента.

- Время: 2026-09-24 21:08:39
- Ветка: main
- Commit: 33c18c1
- Итог: ОШИБКА
- Упавшие проверки: Окружение и подписи карты, Хранение мира и папки, Архив демо-мира, Архив aqezip: упаковка и импорт, Заставка и модальные диалоги, Экспорт ресурсов, Свойства мира и кампании, Импорт кампании и квеста, Создание мира и кампании, Контроль правок UI и инвентаря, Сборка проектов .NET, Тесты домена

## Сводка

| Проверка | Результат | Время, с | Причина |
|---|---|---|---|
| Установить Playwright | успех | 0 |  |
| Установить Chromium | успех | 0 |  |
| Playwright + Chromium smoke | успех | 5.4 |  |
| Контракт simulator <-> editor | успех | 1 |  |
| Горячие клавиши Simulator | успех | 1.4 |  |
| Обновление каталога квестов | успех | 0.8 |  |
| Читаемость плашки квеста | успех | 1.3 |  |
| Обводка текста без пиков | успех | 0.9 |  |
| Сохранения симуляции | успех | 2.1 |  |
| Окружение и подписи карты | ОШИБКА | 4.3 | код выхода 1 |
| Маркер игрока Simulator | успех | 1 |  |
| Quest Graph Playwright smoke | успех | 2.2 |  |
| Перестроение нод Quest Graph | успех | 0.7 |  |
| Scene Graph UI smoke | успех | 1.7 |  |
| Dialogue Workspace UI smoke | успех | 1.3 |  |
| Sidebar pane switch | успех | 0.7 |  |
| Scene interface UI smoke | успех | 1.1 |  |
| Resource navigation smoke | успех | 0.8 |  |
| Location editor smoke | успех | 7.1 |  |
| Location visualisation smoke | успех | 1.5 |  |
| Дорожный слой карты | успех | 1.4 |  |
| Ручная проверка перекрёстков | успех | 7.2 |  |
| Черты городов | успех | 0.9 |  |
| Pan следует за курсором | успех | 1 |  |
| Целостность Quest Graph и сцен | успех | 0.2 |  |
| Правила репутации в контенте | успех | 0.1 |  |
| Портрет репутации загружается | успех | 1.8 |  |
| Хранение мира и папки | ОШИБКА | 0.2 | код выхода 1 |
| Архив демо-мира | ОШИБКА | 0.6 | код выхода 1 |
| Архив aqezip: упаковка и импорт | ОШИБКА | 5.1 | код выхода 1 |
| Заставка и модальные диалоги | ОШИБКА | 8.1 | код выхода 1 |
| Селектор мир/кампания | успех | 1.7 |  |
| Экспорт ресурсов | ОШИБКА | 1.6 | код выхода 1 |
| Свойства мира и кампании | ОШИБКА | 0.8 | код выхода 1 |
| Импорт кампании и квеста | ОШИБКА | 1.8 | код выхода 1 |
| Создание мира и кампании | ОШИБКА | 0.8 | код выхода 1 |
| Автосохранение прохождения | успех | 1.2 |  |
| Инвентарь отдельным окном | успех | 1.2 |  |
| Иконки приложения и файлов | успех | 0.5 |  |
| Читаемость диалогов | успех | 5.4 |  |
| Контроль правок UI и инвентаря | ОШИБКА | 51.1 | код выхода 1 |
| Синхронизация ресурсов data | успех | 1.1 |  |
| Синтаксис web JavaScript | успех | 0.5 |  |
| .NET SDK | успех | 0.1 |  |
| Сборка проектов .NET | ОШИБКА | 2.3 | build не удался: AssistQuestEditor.App.csproj |
| Тесты домена | ОШИБКА | 4.4 | тесты не прошли: AssistQuestEditor.Domain.Tests.csproj |
| Single-file publish | пропущено | 0 | пропущено |
| Single instance probe | успех | 0.3 |  |
| Проверка памяти (вне CI) | успех | 0.1 |  |

## ОШИБКА: Окружение и подписи карты

Причина: код выхода 1

```text
Окружение и подписи: FAIL
 - Метка «(пауза)» должна зависеть от состояния симуляции, а не от флага канала.
 - При выключенной симуляции метка «(пауза)» должна быть: Симуляция: ВЫКЛ01.01.2026 · 12:00:00☀ 07:50–15:53Световой день: после полудня · восход 07:50 · закат 15:53 · длина 8 ч 03 мин · солнце 11.5°ЗимаСДО 1Города 0Квесты 0X 60Y 0Z 60Точка не выбрана
ОШИБКА: код выхода 1
```

## ОШИБКА: Хранение мира и папки

Причина: код выхода 1

```text
Хранение мира: FAIL
 - В разделе мира должна быть кнопка «ПАПКА».
ОШИБКА: код выхода 1
```

## ОШИБКА: Архив демо-мира

Причина: код выхода 1

```text
Демо-мир: FAIL
 - Пустой демо-мир не должен содержать старые ссылки на Common/demo quest.
ОШИБКА: код выхода 1
```

## ОШИБКА: Архив aqezip: упаковка и импорт

Причина: код выхода 1

```text
Архив aqezip: FAIL
 - Поставляемый data/DemoWorld.aqezip не совпадает с тем, что собирает текущий код (в репозитории 2419 Б, собрано 3315 Б). Демо-мир нужно пересобрать: приложение --build-demo-world.
 - Удаление существующей папки обязано быть под проверкой её наличия.
ОШИБКА: код выхода 1
```

## ОШИБКА: Заставка и модальные диалоги

Причина: код выхода 1

```text
Заставка и модальные диалоги: FAIL
 - Не удалось собрать или запустить приложение: Command failed: dotnet publish F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj -c Release -o F:\repo\assist_quest_editor\bin\splash-smoke-1790266036394 --nologo -v q -p:AssistQuestRepositoryRoot=F:\repo\assist_quest_editor
ОШИБКА: код выхода 1
```

## ОШИБКА: Экспорт ресурсов

Причина: код выхода 1

```text
Экспорт ресурсов: FAIL
 - Диалог не объясняет разницу между папкой и архивом.
 - Архив экспорта обязан включать зависимости: иначе пересланный ресурс не работает.
ОШИБКА: код выхода 1
```

## ОШИБКА: Свойства мира и кампании

Причина: код выхода 1

```text
Свойства мира и кампании: FAIL
 - Правка кампании меняет игровое поле «Quests =»: это не свойство описания.
ОШИБКА: код выхода 1
```

## ОШИБКА: Импорт кампании и квеста

Причина: код выхода 1

```text
Импорт кампании и квеста: FAIL
 - Квест копируется не как отдельный файл: соседние квесты кампании пострадают.
 - Имя файла квеста не приводится к каноническому: каталог его не увидит.
 - Импорт распаковывает сразу на место: прерванный импорт оставит битый ресурс.
ОШИБКА: код выхода 1
```

## ОШИБКА: Создание мира и кампании

Причина: код выхода 1

```text
Создание мира и кампании: FAIL
 - Создание мира молча обещает переход в него, хотя он не выполняется.
ОШИБКА: код выхода 1
```

## ОШИБКА: Контроль правок UI и инвентаря

Причина: код выхода 1

```text
Негативные контроли R32: FAIL
 - базовая линия «environment» не проходит на чистом дереве: сама проверка сломана.
 - базовая линия «storage» не проходит на чистом дереве: сама проверка сломана.
 - мутация «окно инвентаря без проверки сохранённой геометрии»: копия не собралась — tor.App\Program.cs(869,50): error CS7036: Отсутствует аргумент, соответствующий требуемому параметру "overwrite" из "ResourceImportService.ImportQuest(CampaignStore, CampaignStore.CampaignRecord, ArchiveInspection, bool)". [C:\Users\Admin\AppData\Local\Temp\aq-mutant-fjvk5G\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
    Предупреждений: 1
    Ошибок: 2

Прошло времени 00:00:02.75

 - мутация «инвентарь без обработки закрытия»: копия не собралась — tor.App\Program.cs(869,50): error CS7036: Отсутствует аргумент, соответствующий требуемому параметру "overwrite" из "ResourceImportService.ImportQuest(CampaignStore, CampaignStore.CampaignRecord, ArchiveInspection, bool)". [C:\Users\Admin\AppData\Local\Temp\aq-mutant-VxHXZI\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
    Предупреждений: 1
    Ошибок: 2

Прошло времени 00:00:02.16

 - мутация «обводка строки квеста прямоугольная»: копия не собралась — 17): error CS0136: Локальная переменная или параметр с именем "dependencies" нельзя объявить в данной области, так как это имя используется во включающей локальной области для определения локальной переменной или параметра [C:\Users\Admin\AppData\Local\Temp\aq-mutant-hoy0Vy\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
    Предупреждений: 1
    Ошибок: 2

Прошло времени 00:00:01.89

 - мутация «кампании без отступа вложенности»: копия не собралась — tor.App\Program.cs(869,50): error CS7036: Отсутствует аргумент, соответствующий требуемому параметру "overwrite" из "ResourceImportService.ImportQuest(CampaignStore, CampaignStore.CampaignRecord, ArchiveInspection, bool)". [C:\Users\Admin\AppData\Local\Temp\aq-mutant-kH1Ci1\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
    Предупреждений: 1
    Ошибок: 2

Прошло времени 00:00:01.88

 - мутация «строка квеста с подобранными отступами»: копия не собралась — tor.App\Program.cs(869,50): error CS7036: Отсутствует аргумент, соответствующий требуемому параметру "overwrite" из "ResourceImportService.ImportQuest(CampaignStore, CampaignStore.CampaignRecord, ArchiveInspection, bool)". [C:\Users\Admin\AppData\Local\Temp\aq-mutant-cibscF\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
    Предупреждений: 1
    Ошибок: 2

Прошло времени 00:00:01.79

 - мутация «выключенные кнопки без своей отрисовки»: копия не собралась — 17): error CS0136: Локальная переменная или параметр с именем "dependencies" нельзя объявить в данной области, так как это имя используется во включающей локальной области для определения локальной переменной или параметра [C:\Users\Admin\AppData\Local\Temp\aq-mutant-Fg15ht\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
    Предупреждений: 1
    Ошибок: 2

Прошло времени 00:00:01.85

 - мутация «выключенный текст тёмным цветом»: копия не собралась — tor.App\Program.cs(869,50): error CS7036: Отсутствует аргумент, соответствующий требуемому параметру "overwrite" из "ResourceImportService.ImportQuest(CampaignStore, CampaignStore.CampaignRecord, ArchiveInspection, bool)". [C:\Users\Admin\AppData\Local\Temp\aq-mutant-bYIw6D\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
    Предупреждений: 1
    Ошибок: 2

Прошло времени 00:00:01.78

ОШИБКА: код выхода 1
```

## ОШИБКА: Сборка проектов .NET

Причина: build не удался: AssistQuestEditor.App.csproj

```text
-> AssistQuestEditor.App.csproj
  Определение проектов для восстановления...
  Все проекты обновлены для восстановления.
  AssistQuestEditor.Domain -> F:\repo\assist_quest_editor\src\AssistQuestEditor.Domain\bin\Release\net10.0\AssistQuestEditor.Domain.dll
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: обнаружены конфликты между разными версиями "WindowsBase", разрешить которые не удалось. [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Произошел конфликт между "WindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" и "WindowsBase, Version=5.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35". [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     Выбрано "WindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35", поскольку оно являлось основным, в отличии от "WindowsBase, Version=5.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35". [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     Ссылки, зависящие от "WindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" [C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.11\ref\net10.0\WindowsBase.dll]. [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.11\ref\net10.0\WindowsBase.dll [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Элемент файла проекта содержит, что вызвало ссылку "C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.11\ref\net10.0\WindowsBase.dll". [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.11\ref/net10.0/WindowsBase.dll [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     Зависимые или унифицированные ссылки для "WindowsBase, Version=5.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" []. [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         C:\Users\Admin\.nuget\packages\microsoft.web.webview2\1.0.4191.47\lib_manual\net5.0-windows10.0.17763.0\Microsoft.Web.WebView2.Wpf.dll [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Элемент файла проекта содержит, что вызвало ссылку "C:\Users\Admin\.nuget\packages\microsoft.web.webview2\1.0.4191.47\lib_manual\net5.0-windows10.0.17763.0\Microsoft.Web.WebView2.Wpf.dll". [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             C:\Users\Admin\.nuget\packages\microsoft.web.webview2\1.0.4191.47\buildTransitive\..\\lib_manual\net5.0-windows10.0.17763.0\Microsoft.Web.WebView2.Wpf.dll [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
F:\repo\assist_quest_editor\src\AssistQuestEditor.App\Host\MainForm.cs(962,17): error CS0136: Локальная переменная или параметр с именем "dependencies" нельзя объявить в данной области, так как это имя используется во включающей локальной области для определения локальной переменной или параметра [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
F:\repo\assist_quest_editor\src\AssistQuestEditor.App\Program.cs(869,50): error CS7036: Отсутствует аргумент, соответствующий требуемому параметру "overwrite" из "ResourceImportService.ImportQuest(CampaignStore, CampaignStore.CampaignRecord, ArchiveInspection, bool)". [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]

Ошибка сборки.

C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: обнаружены конфликты между разными версиями "WindowsBase", разрешить которые не удалось. [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Произошел конфликт между "WindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" и "WindowsBase, Version=5.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35". [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     Выбрано "WindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35", поскольку оно являлось основным, в отличии от "WindowsBase, Version=5.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35". [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     Ссылки, зависящие от "WindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" [C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.11\ref\net10.0\WindowsBase.dll]. [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.11\ref\net10.0\WindowsBase.dll [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Элемент файла проекта содержит, что вызвало ссылку "C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.11\ref\net10.0\WindowsBase.dll". [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.11\ref/net10.0/WindowsBase.dll [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     Зависимые или унифицированные ссылки для "WindowsBase, Version=5.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" []. [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         C:\Users\Admin\.nuget\packages\microsoft.web.webview2\1.0.4191.47\lib_manual\net5.0-windows10.0.17763.0\Microsoft.Web.WebView2.Wpf.dll [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Элемент файла проекта содержит, что вызвало ссылку "C:\Users\Admin\.nuget\packages\microsoft.web.webview2\1.0.4191.47\lib_manual\net5.0-windows10.0.17763.0\Microsoft.Web.WebView2.Wpf.dll". [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
C:\Program Files\dotnet\sdk\10.0.400\Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             C:\Users\Admin\.nuget\packages\microsoft.web.webview2\1.0.4191.47\buildTransitive\..\\lib_manual\net5.0-windows10.0.17763.0\Microsoft.Web.WebView2.Wpf.dll [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
F:\repo\assist_quest_editor\src\AssistQuestEditor.App\Host\MainForm.cs(962,17): error CS0136: Локальная переменная или параметр с именем "dependencies" нельзя объявить в данной области, так как это имя используется во включающей локальной области для определения локальной переменной или параметра [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
F:\repo\assist_quest_editor\src\AssistQuestEditor.App\Program.cs(869,50): error CS7036: Отсутствует аргумент, соответствующий требуемому параметру "overwrite" из "ResourceImportService.ImportQuest(CampaignStore, CampaignStore.CampaignRecord, ArchiveInspection, bool)". [F:\repo\assist_quest_editor\src\AssistQuestEditor.App\AssistQuestEditor.App.csproj]
    Предупреждений: 1
    Ошибок: 2

Прошло времени 00:00:01.03
ОШИБКА: build не удался: AssistQuestEditor.App.csproj
```

## ОШИБКА: Тесты домена

Причина: тесты не прошли: AssistQuestEditor.Domain.Tests.csproj

```text
-> AssistQuestEditor.Domain.Tests.csproj
  Определение проектов для восстановления...
  Все проекты обновлены для восстановления.
  AssistQuestEditor.Domain -> F:\repo\assist_quest_editor\src\AssistQuestEditor.Domain\bin\Release\net10.0\AssistQuestEditor.Domain.dll
  AssistQuestEditor.Domain.Tests -> F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\bin\Release\net10.0\AssistQuestEditor.Domain.Tests.dll
Тестовый запуск для F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\bin\Release\net10.0\AssistQuestEditor.Domain.Tests.dll (.NETCoreApp,Version=v10.0)
Общее количество тестовых файлов (1), соответствующих указанному шаблону.
[xUnit.net 00:00:00.33]     AssistQuestEditor.Domain.Tests.QuestRuntimeTests.Scenario05_Event [FAIL]
  Не пройден AssistQuestEditor.Domain.Tests.QuestRuntimeTests.Scenario05_Event [34 ms]
  Сообщение об ошибке:
   Assert.Equal() Failure: Values differ
Expected: Completed
Actual:   Waiting
  Трассировка стека:
     at AssistQuestEditor.Domain.Tests.QuestRuntimeTests.WaitForEventResumesOnMatchingSimulatorEvent() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeTests.cs:line 238
   at AssistQuestEditor.Domain.Tests.QuestRuntimeTests.Scenario05_Event() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeTests.cs:line 297
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
[xUnit.net 00:00:00.33]     AssistQuestEditor.Domain.Tests.QuestRuntimeStressTests.Stress_ConcurrentEventWaiters_ScalesAcrossRuntimeCounts [FAIL]
  Не пройден AssistQuestEditor.Domain.Tests.QuestRuntimeStressTests.Stress_ConcurrentEventWaiters_ScalesAcrossRuntimeCounts [61 ms]
  Сообщение об ошибке:
   Assert.Equal() Failure: Values differ
Expected: 100
Actual:   0
  Трассировка стека:
     at AssistQuestEditor.Domain.Tests.QuestRuntimeStressTests.Stress_ConcurrentEventWaiters_ScalesAcrossRuntimeCounts() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeStressTests.cs:line 30
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
  Стандартные выходные сообщения:
 === Quest Runtime stress test ===
 Тиры: 100, 250, 500


[xUnit.net 00:00:00.34]     AssistQuestEditor.Domain.Tests.QuestRuntimeTests.DialogueSceneOrchestratesSceneRuntimeAndStoresChoice [FAIL]
  Не пройден AssistQuestEditor.Domain.Tests.QuestRuntimeTests.DialogueSceneOrchestratesSceneRuntimeAndStoresChoice [7 ms]
  Сообщение об ошибке:
   Assert.Equal() Failure: Values differ
Expected: Completed
Actual:   Waiting
  Трассировка стека:
     at AssistQuestEditor.Domain.Tests.QuestRuntimeTests.DialogueSceneOrchestratesSceneRuntimeAndStoresChoice() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeTests.cs:line 549
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
[xUnit.net 00:00:00.35]     AssistQuestEditor.Domain.Tests.QuestRuntimeTests.ChoiceInterfaceSupportsBothBranchesAcrossRuns [FAIL]
  Не пройден AssistQuestEditor.Domain.Tests.QuestRuntimeTests.ChoiceInterfaceSupportsBothBranchesAcrossRuns [1 ms]
  Сообщение об ошибке:
   Assert.Equal() Failure: Values differ
Expected: Completed
Actual:   Waiting
  Трассировка стека:
     at AssistQuestEditor.Domain.Tests.QuestRuntimeTests.ChoiceInterfaceSupportsBothBranchesAcrossRuns() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeTests.cs:line 377
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
[xUnit.net 00:00:00.35]     AssistQuestEditor.Domain.Tests.QuestRuntimeTests.InteractionUsesLocationRadiusInsteadOfNodeDefault [FAIL]
  Не пройден AssistQuestEditor.Domain.Tests.QuestRuntimeTests.InteractionUsesLocationRadiusInsteadOfNodeDefault [1 ms]
  Сообщение об ошибке:
   Assert.Equal() Failure: Values differ
Expected: Completed
Actual:   Waiting
  Трассировка стека:
     at AssistQuestEditor.Domain.Tests.QuestRuntimeTests.InteractionUsesLocationRadiusInsteadOfNodeDefault() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeTests.cs:line 169
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
[xUnit.net 00:00:00.35]     AssistQuestEditor.Domain.Tests.QuestRuntimeTests.ConditionWaitsUntilPlayerReachesWorldPoint [FAIL]
[xUnit.net 00:00:00.36]     AssistQuestEditor.Domain.Tests.QuestRuntimeTests.WaitForEventResumesOnMatchingSimulatorEvent [FAIL]
  Не пройден AssistQuestEditor.Domain.Tests.QuestRuntimeTests.ConditionWaitsUntilPlayerReachesWorldPoint [< 1 ms]
  Сообщение об ошибке:
   Assert.Equal() Failure: Values differ
Expected: Completed
Actual:   Waiting
  Трассировка стека:
     at AssistQuestEditor.Domain.Tests.QuestRuntimeTests.ConditionWaitsUntilPlayerReachesWorldPoint() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeTests.cs:line 76
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
  Не пройден AssistQuestEditor.Domain.Tests.QuestRuntimeTests.WaitForEventResumesOnMatchingSimulatorEvent [< 1 ms]
  Сообщение об ошибке:
   Assert.Equal() Failure: Values differ
Expected: Completed
Actual:   Waiting
  Трассировка стека:
     at AssistQuestEditor.Domain.Tests.QuestRuntimeTests.WaitForEventResumesOnMatchingSimulatorEvent() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeTests.cs:line 238
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
[xUnit.net 00:00:00.36]     AssistQuestEditor.Domain.Tests.QuestRuntimeTests.Scenario06_Choice [FAIL]
[xUnit.net 00:00:00.36]     AssistQuestEditor.Domain.Tests.QuestRuntimeTests.Scenario04_Interaction [FAIL]
  Не пройден AssistQuestEditor.Domain.Tests.QuestRuntimeTests.Scenario06_Choice [< 1 ms]
  Сообщение об ошибке:
   Assert.Equal() Failure: Values differ
Expected: Completed
Actual:   Waiting
  Трассировка стека:
     at AssistQuestEditor.Domain.Tests.QuestRuntimeTests.Scenario06_Choice() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeTests.cs:line 338
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
  Не пройден AssistQuestEditor.Domain.Tests.QuestRuntimeTests.Scenario04_Interaction [< 1 ms]
  Сообщение об ошибке:
   Assert.Equal() Failure: Values differ
Expected: Completed
Actual:   Waiting
  Трассировка стека:
     at AssistQuestEditor.Domain.Tests.QuestRuntimeTests.ConditionWaitsUntilPlayerReachesWorldPoint() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeTests.cs:line 76
   at AssistQuestEditor.Domain.Tests.QuestRuntimeTests.Scenario04_Interaction() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeTests.cs:line 291
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
[xUnit.net 00:00:00.57]     AssistQuestEditor.Domain.Tests.QuestRuntimeTests.WaitTimerResumesThroughOutputAndDoesNotReenterWait [FAIL]
[xUnit.net 00:00:00.57]     AssistQuestEditor.Domain.Tests.QuestRuntimeTests.InteractionCanWaitOnDynamicLocation [FAIL]
  Не пройден AssistQuestEditor.Domain.Tests.QuestRuntimeTests.WaitTimerResumesThroughOutputAndDoesNotReenterWait [255 ms]
  Сообщение об ошибке:
   Assert.Equal() Failure: Values differ
Expected: Completed
Actual:   Waiting
  Трассировка стека:
     at AssistQuestEditor.Domain.Tests.QuestRuntimeTests.WaitTimerResumesThroughOutputAndDoesNotReenterWait() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeTests.cs:line 201
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
  Не пройден AssistQuestEditor.Domain.Tests.QuestRuntimeTests.InteractionCanWaitOnDynamicLocation [< 1 ms]
  Сообщение об ошибке:
   Assert.Equal() Failure: Values differ
Expected: Completed
Actual:   Waiting
  Трассировка стека:
     at AssistQuestEditor.Domain.Tests.QuestRuntimeTests.InteractionCanWaitOnDynamicLocation() in F:\repo\assist_quest_editor\tests\AssistQuestEditor.Domain.Tests\QuestRuntimeTests.cs:line 118
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
Внимание! Перезапись файла с результатами: F:\repo\assist_quest_editor\ci-results\domain-tests\AssistQuestEditor.Domain.Tests.trx
Файл результатов: F:\repo\assist_quest_editor\ci-results\domain-tests\AssistQuestEditor.Domain.Tests.trx

Не пройден!: не пройдено    11, пройдено   346, пропущено     0, всего   357, длительность 354 ms. - AssistQuestEditor.Domain.Tests.dll (net10.0)
ОШИБКА: тесты не прошли: AssistQuestEditor.Domain.Tests.csproj
```
