# Assist Quest Editor

Песочница и рабочий прототип переносимой системы создания и исполнения квестов.

Проект развивается отдельно от ETS2 Assist: сначала полностью отрабатываем архитектуру, редактор, симулятор, runtime и визуальные интерфейсы на искусственных данных. Подключение ETS2 Assist и других игровых источников данных происходит только после стабилизации контрактов.

Документация проекта находится в `MemoryAI/`.

## Сборка

Для обычной разработки запускайте:

    powershell -ExecutionPolicy Bypass -File .\compile.ps1

Скрипт очищает старую публикацию, восстанавливает зависимости и публикует self-contained приложение под Windows x64. Проверяется, что каталог публикации содержит ровно один файл:

    bin\Release\net10.0-windows\win-x64\publish\AssistQuestEditor.exe

HTML/CSS/JavaScript включаются в single-file публикацию. При запуске .NET извлекает содержимое, необходимое приложению и WebView2. Это соответствует режиму single-file с IncludeAllContentForSelfExtract. citeturn623619search1

Для обновления исходников и сборки:

    powershell -ExecutionPolicy Bypass -File .\pull.ps1

Для проверки публикации без запуска приложения:

    powershell -ExecutionPolicy Bypass -File .\compile.ps1 -NoLaunch
