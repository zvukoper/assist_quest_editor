// Хранение мира: единый источник контента и раскладка папок.
//
// История бага: контент жил в ДВУХ местах — папка `data` рядом с EXE (поставка)
// и установочная копия в пользовательской папке. Правка одной из них не была
// видна в другой, и «квест не появился на карте» объяснялось именно этим. Теперь
// хранение сведено к мирам, где мир = проект, кампания внутри мира, квест —
// внутри кампании.
//
// Проверка статическая (читает исходники) и без файловой системы: она стережёт
// КОНТРАКТ путей и то, что приложение читает и пишет ОДНО дерево.
import fs from "node:fs";
import path from "node:path";

const read = rel => fs.readFileSync(rel, "utf8");

const worldPaths = read("src/AssistQuestEditor.Domain/WorldPaths.cs");
const appPaths = read("src/AssistQuestEditor.App/Core/AppPaths.cs");
const worldStore = read("src/AssistQuestEditor.App/WorldStore.cs");
const campaignStore = read("src/AssistQuestEditor.App/CampaignStore.cs");
const program = read("src/AssistQuestEditor.App/Program.cs");
const prefs = read("src/AssistQuestEditor.App/Core/AppUiPreferencesStore.cs");

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

const worldStoreText = worldStore;
const worldPathsText = worldPaths;

// --- 1. Раскладка папок мира ---

check(/WorldsFolder\s*=\s*"worlds"/.test(worldPathsText),
  "Корень миров должен называться worlds.");
check(/WorldFileName\s*=\s*"world\.aqworld"/.test(worldPathsText),
  "Файл мира должен называться world.aqworld.");
check(/CampaignFileName\s*=\s*"campaign\.aqcampaign"/.test(worldPathsText),
  "Раскладка мира должна задавать имя файла кампании сама: Domain не может ссылаться на App.");
check(/SavesFolder\s*=\s*"Saves"/.test(worldPathsText),
  "Сохранения должны лежать в папке Saves внутри мира.");
check(/ExportedFolder\s*=\s*"Exported"/.test(worldPathsText),
  "Экспорт должен складываться в папку Exported.");
check(/CampaignsFolder\s*=\s*"campaigns"/.test(worldPathsText),
  "Кампании должны лежать в папке campaigns внутри мира.");

// Дерево: мир → campaigns → кампания → quests/scenes. Проверяется ПОРЯДОК
// сегментов, а не отдельные строки: перепутанный порядок ломает раскладку так
// же, как переименование.
check(/WorldFilePath\(string worldFolder\)[\s\S]{0,160}Path\.Combine\(worldFolder, WorldFileName\)/.test(worldPathsText),
  "Файл мира должен лежать прямо в папке мира.");
check(/CampaignFolder\(string worldFolder, string campaignFolderName\)[\s\S]{0,180}Path\.Combine\(CampaignsRoot\(worldFolder\), campaignFolderName\)/.test(worldPathsText),
  "Папка кампании должна лежать внутри campaigns мира.");
check(/QuestsFolderPath\(string campaignFolder\)[\s\S]{0,140}Path\.Combine\(campaignFolder, QuestsFolder\)/.test(worldPathsText),
  "Квесты должны лежать внутри папки кампании, а не рядом с ней.");

// Сохранения — внутри СВОЕГО мира: иначе снимок одного мира можно загрузить в
// другой, где другие квесты и точки.
check(/SavesFolderPath\(string worldFolder\)[\s\S]{0,140}Path\.Combine\(worldFolder, SavesFolder\)/.test(worldPathsText),
  "Сохранения должны принадлежать своему миру.");

// --- 2. Единое хранение: приложение читает и пишет дерево миров ---

check(/WorldsRoot\s*=>\s*WorldPaths\.WorldsRoot\(UserRoot\)/.test(appPaths),
  "AppPaths.WorldsRoot обязан брать корень миров из контракта Domain.");
check(/UserQuestRoot\s*=>\s*WorldsRoot/.test(appPaths),
  "Каталог кампаний обязан совпадать с корнем миров: два дерева — это и есть дублирование.");
check(/WorldsRoot/.test(worldStoreText) && !/Path\.Combine\([^)]*"data"/.test(worldStoreText),
  "WorldStore не должен знать про папку data: контент живёт в пользовательских мирах.");

// Рекурсивный обход — условие того, что старые вызовы CampaignStore находят
// кампании внутри миров без переписывания.
check(/SearchOption\.AllDirectories/.test(campaignStore),
  "Обход кампаний обязан быть рекурсивным: кампании лежат внутри папок миров.");
check(/SearchOption\.AllDirectories/.test(worldStoreText),
  "Обход миров обязан быть рекурсивным.");

// --- 3. Пользовательская папка переехала в Документы ---

check(/SpecialFolder\.MyDocuments/.test(appPaths),
  "Пользовательская папка должна быть в Документах, а не в AppData.");
check(/UserFolderName\s*=\s*"Assist Quest Editor"/.test(appPaths),
  "Имя пользовательской папки должно оставаться стабильным: по нему её найдут после обновления.");

// Настройки интерфейса — тоже пользовательские данные: псевдоним автора входит
// в подпись ресурсов, и в AppData он терялся бы при переустановке.
check(/Path\.Combine\(AppPaths\.SettingsDirectory, "ui-settings\.json"\)/.test(prefs),
  "Настройки интерфейса должны лежать в пользовательской папке Документов.");

// --- 4. Общая кампания создаётся автоматически и имеет демо-контент ---

check(/CommonCampaignIdValue\s*=\s*"common"/.test(read("src/AssistQuestEditor.Domain/WorldModels.cs")),
  "Id общей кампании должен быть фиксированным, а не Guid.");
check(/static class WorldContentSeeder/.test(worldStoreText),
  "Демо-контент общей кампании должен сеяться отдельным классом.");
check(/DemoQuestId\s*=\s*"common_intro"/.test(worldStoreText),
  "У общей кампании обязан быть демонстрационный квест с предсказуемым Id.");
check(/CreateWorld[\s\S]{0,2400}SeedCommonCampaign/.test(worldStoreText),
  "Создание мира обязано сразу заводить общую кампанию: пустой мир выглядит сломанным.");

// --- 5. Первичная настройка и выбор мира ДО главного окна ---

check(/private static bool RunSetupPhase/.test(program),
  "Первичная настройка должна быть отдельной фазой запуска.");
check(/FirstRunSetupForm/.test(program) && /WorldChooserForm/.test(program),
  "Фаза настройки обязана спрашивать псевдоним и мир.");
check(/if \(!ciTest && !RunSetupPhase\(ref preferences\)\)/.test(program),
  "Отказ от настройки обязан завершать запуск: без псевдонима ресурсы создавались бы без подписи.");
// Мир выбирается ПОСЛЕ настройки: псевдоним нужен, чтобы подписать сам мир.
check(program.indexOf("RunSetupPhase") < program.indexOf("new MainForm("),
  "Настройка и выбор мира должны происходить ДО создания главного окна.");

// Настройка не должна спрашиваться в CI: прогон обязан быть неинтерактивным.
// Проверяется СРЕЗ вокруг ВЫЗОВА (первое вхождение), потому что ниже в файле
// есть определение метода — поиск по всему файлу нашёл бы условие «!ciTest»
// внутри тела и прошёл бы даже при снятом условии у вызова.
const setupCallIndex = program.indexOf("RunSetupPhase(ref preferences)");
const setupCall = program.slice(Math.max(0, setupCallIndex - 120), setupCallIndex + 60);
check(/!ciTest/.test(setupCall),
  "В режиме CI первичная настройка спрашиваться не должна.");

// --- 6. Подпись ресурсов ---

const worldModels = read("src/AssistQuestEditor.Domain/WorldModels.cs");
check(/static class WorldDisplayRules/.test(worldModels),
  "Правила подписей мира/кампании должны жить в домене: их читают и список, и селектор, и импорт.");
check(/const string UnnamedLabel/.test(worldModels),
  "Отсутствие имени обязано иметь явную подпись, а не пустую строку.");
check(/EffectiveModifiedOn/.test(read("src/AssistQuestEditor.Domain/ResourceMetadata.cs")),
  "Подпись изменения обязана уметь брать момент создания, если правок ещё не было.");

// --- 7. Кампании ограничены СВОИМ миром ---

check(/public CampaignStore ScopedTo\(string\? worldFolder\)/.test(campaignStore),
  "Стор кампаний обязан уметь ограничиваться папкой мира: два мира могут иметь " +
  "кампанию с одним id, и без ограничения они наложатся друг на друга.");

const simulatorForm = read("src/AssistQuestEditor.App/Host/SimulatorForm.cs");
check(/\.ScopedTo\(world\?\.FolderPath\)/.test(simulatorForm),
  "Симулятор обязан ограничивать каталог кампаний выбранным миром.");
check(/WorldRecord\? world = null/.test(simulatorForm),
  "Симулятор должен принимать мир: он показывает его кампании и его папку.");
check(/private void OpenWorldFolder/.test(simulatorForm),
  "Кнопка «ПАПКА» раздела мира обязана открывать папку мира.");

const mainForm = read("src/AssistQuestEditor.App/Host/MainForm.cs");
// Конструктор принимает автора: подпись проставляется ВСЕМИ правками кампании,
// и передавать её параметром в каждый вызов значило бы однажды забыть.
check(/new CampaignStore\(AppPaths\.UserQuestRoot, readOnly: false, author\)[\s\S]{0,80}?\.ScopedTo\(SelectedWorld\?\.FolderPath\)/.test(mainForm),
  "Главная форма обязана ограничивать каталог кампаний выбранным миром и " +
  "передавать автору подпись для правок.");
check(/var author = string\.IsNullOrWhiteSpace\(_preferences\.Author\)/.test(mainForm),
  "Подпись пользователя обязана вычисляться один раз и читаться обоими сторами: " +
  "иначе правка кампании подписывалась бы другим автором, чем правка мира.");
check(/private WorldRecord\? SelectedWorld/.test(mainForm),
  "Главная форма обязана хранить текущий мир и восстанавливать его из настроек.");
// Падение на отсутствующий мир недопустимо: папку могли удалить. Тогда
// выбирается первый доступный, и это ФИКСИРУЕТСЯ в настройках, а не молча.
check(/сохранённый мир недоступен, выбран первый доступный/.test(mainForm),
  "Недоступный сохранённый мир обязан приводить к выбору первого доступного с записью в журнал.");

// --- 8. Окно кампаний: мир — первый раздел с ПАПКА, заголовок с именем мира ---

const campaignsForm = read("src/AssistQuestEditor.App/Host/CampaignsForm.cs");
check(/SetWorld\(WorldRecord\? world\)/.test(campaignsForm),
  "Окно кампаний обязано принимать мир.");
check(/\"Кампании и квесты — \" \+ world\.DisplayName/.test(campaignsForm),
  "Заголовок окна кампаний должен содержать имя мира: иначе непонятно, чей это каталог.");
check(/private Control CreateWorldBlock/.test(campaignsForm),
  "Мир обязан быть ОТДЕЛЬНЫМ разделом списка, а не одной из кампаний.");
check(/CreateMicroButton\("ПАПКА"\)/.test(campaignsForm),
  "В разделе мира должна быть кнопка «ПАПКА».");
// Раздел мира рисуется ДО кампаний: он корень дерева.
check(campaignsForm.indexOf("CreateWorldBlock(_world)") < campaignsForm.indexOf("CreateCampaignBlock(campaign)"),
  "Раздел мира должен идти первым: он корень дерева, а кампании — его ветви.");
// Имя мира крупнее строки кампании: иначе он выглядит ещё одной кампанией.
const worldBlock = campaignsForm.slice(
  campaignsForm.indexOf("private Control CreateWorldBlock"),
  campaignsForm.indexOf("private Control? CreateSignatureRow"));
check(/Font\("Segoe UI", 13f, FontStyle\.Bold\)/.test(worldBlock),
  "Имя мира должно быть заметно крупнее названий кампаний.");

// Строка кампании обязана остаться ПРЕЖНЕГО размера: сравнивать размеры нужно
// по конкретному объявлению шрифта, а не по взаимному расположению строк в
// файле — порядок аргументов инициализатора может меняться при правках, и
// проверка «размер идёт после имени» ломалась бы без всякой причины.
//
// Границы среза берутся от НАЧАЛА метода до начала следующего: порядок
// объявлений в файле не должен влиять на проверку.
const campaignStart = campaignsForm.indexOf("private Control CreateCampaignBlock");
const campaignEnd = campaignsForm.indexOf("\n    private ", campaignStart + 10);
const campaignBlock = campaignsForm.slice(campaignStart, campaignEnd > 0 ? campaignEnd : undefined);
const campaignFont = campaignBlock.match(/Font = new Font\("Segoe UI",\s*([\d.]+)f/);
check(campaignFont && Number(campaignFont[1]) < 13,
  "Строка кампании должна остаться меньше имени мира: " + (campaignFont?.[1] ?? "шрифт не найден"));

// И имя мира обязано быть ЖИРНЫМ и крупнее любого шрифта в блоке кампаний.
const worldFont = worldBlock.match(/Font = new Font\("Segoe UI",\s*([\d.]+)f[^)]*\)/);
check(worldFont && Number(worldFont[1]) >= 12,
  "Имя мира должно быть крупным (>=12pt): " + (worldFont?.[1] ?? "шрифт не найден"));

// --- 9. Подписи автора курсивом в списках ---

check(/FontStyle\.Italic/.test(campaignsForm),
  "Подписи created_by/modified_by должны быть курсивом: они справка, а не название.");
check(/WorldDisplayRules\.Describe\(world\.Definition\.Metadata\)/.test(campaignsForm),
  "Подпись мира в списке должна собираться правилом домена, а не склейкой в форме.");
check(/WorldDisplayRules\.Describe\(world\.Definition\.Metadata, modified: false\)/.test(campaignsForm),
  "В списке должны быть ОБЕ подписи: и создание, и изменение.");
// Подписи обязаны быть у КАМПАНИИ, а не только у мира: у каждой кампании есть
// свои created_by/modified_by, и без них автор правки не виден в списке.
check(/WorldDisplayRules\.Describe\(campaign\.Metadata\)/.test(campaignsForm),
  "Кампания в списке не подписана автором/датой правки.");
check(/WorldDisplayRules\.Describe\(campaign\.Metadata, modified: false\)/.test(campaignsForm),
  "У кампании должна быть подпись СОЗДАНИЯ, а не только изменения.");
// Вид кампании обязан нести метаданные: без них подписывать нечем.
check(/ResourceMetadata\? Metadata = null/.test(read("src/AssistQuestEditor.App/CampaignStore.cs")),
  "Вид кампании не несёт метаданные: подпись в списке собрать не из чего.");
// Подпись добавляется через null-безопасный помощник: CreateSignatureRow
// возвращает null, когда подписывать нечем (файлы без авторства), а
// Controls.Add(null) бросает исключение и уронил бы окно на старом ресурсе.
check(/private void AddSignature\(FlowLayoutPanel rows, string\? text\)/.test(campaignsForm),
  "Подпись добавляется без проверки на null: окно упадёт на ресурсе без авторства.");
check(/if \(control is not null\)\s*\n\s*rows\.Controls\.Add\(control\);/.test(campaignsForm),
  "Помощник подписи не отсекает null: Controls.Add(null) бросит исключение.");
check(!/rows\.Controls\.Add\(CreateSignatureRow\(/.test(campaignsForm),
  "Подпись добавляется в обход null-безопасного помощника.");
// Ширина/отступ строки квеста не должны применяться к подписи: подпись ниже
// строки квеста, и отступ вложенности сдвинул бы состав кампании.
check(/row is not TableLayoutPanel[\s\S]{0,260}?continue;/.test(
    campaignsForm.slice(campaignsForm.indexOf("private void ResizeBlocks"))),
  "ResizeBlocks переформатирует подписи как строки квестов: состав кампании уедет вниз.");
// Название строки берётся ПО ПОЗИЦИИ в таблице: индекс в Controls — это
// z-order, и после BringToFront имя перестало бы находиться, а высота
// молча упала бы до минимальной (длинные названия срезались бы).
check(/table\.GetControlFromPosition\(1, 0\) is not Label name/.test(campaignsForm),
  "Высота строки ищет название по индексу коллекции, а не по позиции в таблице.");

// --- 10. Оранжевое предупреждение о чужом родителе ---
//
// Папку кампании можно перенести в чужой мир файловым менеджером, и это НЕ
// запрет (перенос мог быть осознанным), поэтому работа не блокируется — но
// предупреждение обязательно: без него квесты кампании выглядят пропавшими, а
// причина не видна вовсе.
check(/ResourceParentRules\.Describe\(\s*\n?\s*campaign\.ParentWorldId, _world\?\.Definition\.Id\)/.test(campaignsForm),
  "Список кампаний не проверяет чужого родителя: причина «пропавших» квестов не видна.");
check(/ForeColor = AccentColor/.test(campaignsForm.slice(
  campaignsForm.indexOf("foreignWarning"),
  campaignsForm.indexOf("foreignWarning") + 900)),
  "Предупреждение о чужом родителе не выделено акцентным (оранжевым) цветом.");
// Предупреждение должно быть ВИДНО, то есть идти до списка квестов в блоке.
// Оба якоря ищутся ВНУТРИ блока кампании: `var rows` объявлен ещё и в блоке
// мира, и поиск по всему файлу взял бы первое вхождение — до предупреждения.
const warningIndex = campaignsForm.indexOf("foreignWarning is not null");
const rowsIndex = campaignsForm.indexOf("var rows = new FlowLayoutPanel", warningIndex);
check(warningIndex >= 0 && rowsIndex > warningIndex,
  "Предупреждение о чужом родителе должно стоять ДО списка квестов: иначе его не видно без прокрутки.");
// Id родителя обязан доезжать до окна: у записи кампании его не было.
check(/record\.Definition\.WorldId/.test(read("src/AssistQuestEditor.App/CampaignStore.cs")),
  "Каталог не передаёт id родительского мира: предупреждение нечего сравнивать.");
check(/string\? ParentWorldId = null/.test(read("src/AssistQuestEditor.App/CampaignStore.cs")),
  "Вид кампании не несёт родительский мир: окно не может показать предупреждение.");
// Правило живёт в домене: о расхождении сообщают два места, и формулировки
// разошлись бы.
check(/public static class ResourceParentRules/.test(read("src/AssistQuestEditor.Domain/WorldModels.cs")),
  "Правило чужого родителя должно жить в домене: его читают окно и селектор.");
check(/if \(string\.IsNullOrWhiteSpace\(declaredParentId\)/.test(
    read("src/AssistQuestEditor.Domain/WorldModels.cs")),
  "Пустой родитель обязан считаться нормой: иначе весь старый контент объявится чужим.");

if (failures.length) {
  console.log("Хранение мира: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Хранение мира: OK единое дерево worlds, папка в Документах, кампании ограничены миром, " +
  "раздел мира с ПАПКА и подписями, настройка до главного окна.");
