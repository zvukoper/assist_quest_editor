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
const themeCssForTree = read("src/AssistQuestEditor.App/Web/theme.css");

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

// --- 8. Окно кампаний: мир — сворачиваемый пункт дерева уровнем ВЫШЕ кампании ---

const campaignsForm = read("src/AssistQuestEditor.App/Host/CampaignsForm.cs");
check(/SetWorld\(WorldRecord\? world\)/.test(campaignsForm),
  "Окно кампаний обязано принимать мир.");
check(/\"Кампании и квесты — \" \+ world\.DisplayName/.test(campaignsForm),
  "Заголовок окна кампаний должен содержать имя мира: иначе непонятно, чей это каталог.");
check(/private Control CreateWorldBlock/.test(campaignsForm),
  "Мир обязан быть отдельным пунктом дерева, а не одной из кампаний.");
check(/CreateMicroButton\("ПАПКА"\)/.test(campaignsForm),
  "В разделе мира должна быть кнопка «ПАПКА».");
check(/CreateWorldBlock\(_world\)/.test(campaignsForm),
  "Дерево обязано начинаться с пункта мира.");

const worldBlock = campaignsForm.slice(
  campaignsForm.indexOf("private Control CreateWorldBlock"),
  campaignsForm.indexOf("private Control? CreateSignatureRow"));

// МИР ВЫГЛЯДИТ КАК КАМПАНИЯ, просто уровнем выше. Требование изменилось
// намеренно: прежде мир был отдельным разделом с крупным (13pt) заголовком, и
// выдать это за «тот же вид» нельзя. Единственный признак уровня — ОРАНЖЕВЫЙ
// цвет (сохранён) и вложенность кампаний внутрь пункта мира.
check(!/Font\("Segoe UI", 13f/.test(worldBlock),
  "Имя мира не должно быть крупнее названий кампаний: мир — такой же пункт дерева.");
check(/Font\("Segoe UI", 9f, FontStyle\.Bold\)/.test(worldBlock),
  "Имя мира должно иметь тот же размер шрифта, что и название кампании.");
check(/250, 176, 3/.test(worldBlock),
  "Оранжевый цвет пункта мира обязан сохраниться: это единственный признак уровня.");
// Мир сворачивается, как кампания.
check(/ToggleCollapsed\(WorldCollapseKey\)/.test(worldBlock),
  "Пункт мира обязан сворачиваться, как кампания: это тот же вид.");
check(/collapsed \? "▸" : "▾"/.test(worldBlock),
  "У пункта мира должна быть стрелка свёрнутости, как у кампании.");

// Кампании ВЛОЖЕНЫ в пункт мира, а не лежат плоским списком рядом с ним:
// только так принадлежность кампании миру видна без подписи.
check(/foreach \(var campaign in _catalog\)\s*\r?\n?\s*rows\.Controls\.Add\(CreateCampaignBlock\(campaign\)\)/.test(worldBlock),
  "Кампании должны вкладываться ВНУТРЬ пункта мира: иначе это два несвязанных списка.");
// И плоского добавления кампаний в корень при ВЫБРАННОМ мире быть не должно.
//
// Проверка различает ветки, а не запрещает слово `CreateCampaignBlock`: при
// отсутствии мира кампании остаются ПЛОСКИМ списком — это осознанная вырожденная
// ветка (показывать их без родителя нельзя, но и прятать нечего). Запрещать её
// значило бы требовать, чтобы окно перестало показывать хоть что-нибудь.
const rebuildBody = campaignsForm.slice(
  campaignsForm.indexOf("private void Rebuild()"),
  campaignsForm.indexOf("private Control CreateWorldBlock"));
const worldAddIndex = rebuildBody.indexOf("_list.Controls.Add(CreateWorldBlock(_world))");
const elseIndex = rebuildBody.indexOf("else", worldAddIndex);
const rootCampaignAddIndex = rebuildBody.indexOf("_list.Controls.Add(CreateCampaignBlock(campaign))");

check(worldAddIndex >= 0, "Rebuild не добавляет пункт мира.");
check(rootCampaignAddIndex < 0 || (elseIndex > worldAddIndex && rootCampaignAddIndex > elseIndex),
  "Кампании добавляются в корень списка при выбранном мире, а не внутрь пункта мира.");

// Строка кампании обязана остаться ПРЕЖНЕГО размера: сравнивать размеры нужно
// по конкретному объявлению шрифта, а не по взаимному расположению строк в
// файле — порядок аргументов инициализатора может меняться при правках.
const campaignStart = campaignsForm.indexOf("private Control CreateCampaignBlock");
const campaignEnd = campaignsForm.indexOf("\n    private ", campaignStart + 10);
const campaignBlock = campaignsForm.slice(campaignStart, campaignEnd > 0 ? campaignEnd : undefined);
const campaignFont = campaignBlock.match(/Font = new Font\("Segoe UI",\s*([\d.]+)f/);
check(campaignFont && Number(campaignFont[1]) < 13,
  "Строка кампании должна остаться меньше 13pt: " + (campaignFont?.[1] ?? "шрифт не найден"));
check(campaignFont && campaignFont[1] === "9", // 9f — как у пункта мира
  "Размер шрифта кампании и мира должен совпадать: " + (campaignFont?.[1] ?? "?") + " против 9");

// --- 9. Под названием — ТОЛЬКО даты создания и изменения ---

check(/FontStyle\.Italic/.test(campaignsForm),
  "Подписи дат должны быть курсивом: они справка, а не название.");
// Автора в подписи НЕТ: в строке рядом с названием он читался бы как часть
// названия. Для этого и появилось отдельное правило домена — DescribeStamp.
check(/WorldDisplayRules\.DescribeStamp\(world\.Definition\.Metadata, modified: false\)/.test(campaignsForm),
  "У мира должна быть подпись СОЗДАНИЯ без автора.");
check(/WorldDisplayRules\.DescribeStamp\(world\.Definition\.Metadata\)/.test(campaignsForm),
  "У мира должна быть подпись ИЗМЕНЕНИЯ без автора.");
check(/WorldDisplayRules\.DescribeStamp\(campaign\.Metadata, modified: false\)/.test(campaignsForm),
  "У кампании должна быть подпись СОЗДАНИЯ без автора.");
check(/WorldDisplayRules\.DescribeStamp\(campaign\.Metadata\)/.test(campaignsForm),
  "У кампании должна быть подпись ИЗМЕНЕНИЯ без автора.");
// Прежняя подпись с автором в дереве быть НЕ должна: она противоречит требованию.
check(!/WorldDisplayRules\.Describe\((world\.Definition|campaign)\.Metadata/.test(campaignsForm),
  "В дереве осталась подпись с автором: под названием должны быть только даты.");
// Правило живёт в домене: две реализации дали бы две подписи одного ресурса.
const worldModelsForStamps = read("src/AssistQuestEditor.Domain/WorldModels.cs");
check(/public static string\? DescribeStamp\(ResourceMetadata\? metadata, bool modified = true\)/.test(worldModelsForStamps),
  "Правило подписи датами должно жить в домене, а не собираться в форме.");
check(/label = modified \? "Modified" : "Created"/.test(worldModelsForStamps),
  "Даты должны подписываться словами Modified/Created.");
// Вид кампании обязан нести метаданные: без них подписывать нечем.
check(/ResourceMetadata\? Metadata = null/.test(read("src/AssistQuestEditor.App/CampaignStore.cs")),
  "Вид кампании не несёт метаданные: подпись в списке собрать не из чего.");

// --- 9б. Иконка ℹ️ у названия мира и кампании ---

check(/CreateInfoIcon\("Свойства мира"\)/.test(worldBlock),
  "У названия мира должна быть иконка информации для открытия свойств.");
check(/CreateInfoIcon\("Свойства кампании"\)/.test(campaignBlock),
  "У названия кампании должна быть иконка информации для открытия свойств.");
// Иконка — МАЛЕНЬКАЯ СИНЯЯ иконка, а не кнопка во всю высоту строки: кнопка
// спорила с названием за место и выглядела органом управления, а не справкой.
const infoIconBody = campaignsForm.slice(
  campaignsForm.indexOf("private static Label CreateInfoIcon"),
  campaignsForm.indexOf("private const int InfoIconSize")
);
check(/InfoIconSize/.test(infoIconBody),
  "Иконка информации должна ограничивать свой размер, иначе она растянет строку.");
check(/var\(--blue\)/.test(themeCssForTree) || /18, 171, 229/.test(infoIconBody),
  "Иконка информации должна быть синей: это цвет справки в интерфейсе.");
check(!/CreateMicroButton\("ℹ️"\)/.test(campaignsForm),
  "Осталась прежняя кнопка ℹ️ во всю высоту строки.");
// Иконка у кампании обязана нести ЕЁ id: иконок столько же, сколько кампаний, и
// «открыть свойства текущей» открывало бы не ту.
check(/CampaignPropertiesRequested\?\.Invoke\(this,\s*new CampaignPropertiesRequestedEventArgs\(campaign\.Id\)\)/.test(campaignBlock),
  "Иконка ℹ️ кампании не передаёт её id: откроются свойства не той кампании.");
check(/WorldPropertiesRequested\?\.Invoke/.test(worldBlock),
  "Иконка ℹ️ мира не запрашивает свойства мира.");
// Сворачивание по иконке информации было бы неожиданным: у неё своё действие.
check(/foreach \(Control element in new Control\[\] \{ header, text, marker \}\)\s*\r?\n?\s*\{/.test(campaignBlock),
  "Сворачивание повешено на весь заголовок, включая кнопки: клик по иконке свернёт кампанию.");
check(/new Control\[\] \{ header, text, marker \}/.test(campaignBlock),
  "Сворачивание должно быть только на заголовке, названии и стрелке.");

// Уровневость дерева задаётся ОТСТУПОМ СЛЕВА: без него кампании выглядят как
// плоский список, выровненный по левому краю, — именно на это жаловался автор.
// Проверяется КОНСТРУКЦИЯ отступа, а не вхождение имени константы: имя
// встречается и в пояснении к правке, и такая проверка прошла бы впустую.
check(/Margin = new Padding\(TreeLevelIndent, 0, 0, 8\)/.test(campaignBlock),
  "Пункт кампании не имеет отступа вложенности: уровни дерева не читаются.");
check(/var total = LayoutPanelChildren\(rows, innerWidth, QuestRowIndent\)/.test(campaignsForm),
  "Строки квестов не получают отступ вложенности при раскладке.");
check(/private static int LayoutPanelChildren\(FlowLayoutPanel rows, int availableWidth, int indent\)/.test(campaignsForm),
  "Раскладка строк не принимает отступ уровня: каждый уровень пришлось бы править отдельно.");

// Обводка выделенной строки квеста обязана быть СКРУГЛЁННОЙ и того же радиуса,
// что кнопки: прямоугольная спорила со скруглениями рядом.
const questRowBody = campaignsForm.slice(
  campaignsForm.indexOf("private Control CreateQuestRow"),
  campaignsForm.indexOf("private static Label CreateNotice")
);
check(/RoundedPath\(bounds, ButtonRadius\)/.test(questRowBody),
  "Обводка выделенной строки квеста должна быть скруглённой.");
check(/DrawPath\(pen, path\)/.test(questRowBody),
  "Обводка строки квеста должна рисоваться скруглённым путём, а не прямоугольником.");
// Выравнивание однотипных элементов одной строки — ЯКОРЕМ, а не подобранным
// отступом: отступы разъезжаются при любой правке высоты строки.
check(!/Margin = new Padding\(0, 1[14], /.test(questRowBody),
  "В строке квеста остались ПОДОБРАННЫЕ вертикальные отступы: элементы разъезжаются.");
check(/Anchor = AnchorStyles\.(Left|Right)/.test(questRowBody),
  "Элементы строки квеста не выровнены якорем: вертикаль задана отступами.");

// События обязаны доходить до окна свойств, а не теряться на середине пути.
const simulatorFormForInfo = read("src/AssistQuestEditor.App/Host/SimulatorForm.cs");
const mainFormForInfo = read("src/AssistQuestEditor.App/Host/MainForm.cs");
check(/CampaignPropertiesRequested \+= \(_, e\) =>/.test(simulatorFormForInfo),
  "Симулятор не пробрасывает просьбу о свойствах кампании наружу.");
check(/campaignId: e\.CampaignId/.test(mainFormForInfo),
  "Главное окно не открывает свойства той кампании, у которой нажата иконка.");
check(/public event EventHandler\? WorldPropertiesRequested/.test(simulatorFormForInfo),
  "Симулятор не объявляет событие свойств мира.");

// --- 9в. Одна вертикальная прокрутка на всё дерево, без горизонтальной ---

// Прокрутка только у корневого списка: вложенные спорили бы за колесо мыши, и
// до кампаний было бы не добраться.
check(/FlowLayoutPanel \{ Dock = DockStyle\.Fill, AutoScroll = true,[\s\S]{0,200}?WrapContents = false/.test(campaignsForm),
  "Корневой список дерева обязан иметь вертикальную прокрутку.");
const nestedScrollCount = (campaignsForm.match(/AutoScroll = false/g) || []).length;
check(nestedScrollCount >= 2,
  "У вложенных контейнеров прокрутка должна быть выключена (найдено " +
  nestedScrollCount + "): две вложенные полосы спорят за колесо мыши.");
// Горизонтальная прокрутка исключена: длинный текст ПЕРЕНОСИТСЯ по словам.
check(!/AutoEllipsis = true/.test(worldBlock),
  "Название мира обрезается многоточием вместо переноса.");
check(!/AutoEllipsis = true/.test(campaignBlock),
  "Название кампании обрезается многоточием вместо переноса.");
check(/AutoSize = false/.test(campaignBlock),
  "Название кампании должно быть с AutoSize = false: иначе Dock игнорируется и перенос не работает.");
// WrapContents = false обязателен: при true FlowLayoutPanel переносил бы
// вложенные плашки вбок и создавал горизонтальную прокрутку.
check(!/WrapContents = true/.test(campaignsForm),
  "Перенос содержимого включён: появится горизонтальная прокрутка.");
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
