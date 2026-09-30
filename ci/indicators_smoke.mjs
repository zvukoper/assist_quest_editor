import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root=process.cwd();
const tmp=fs.mkdtempSync(path.join(os.tmpdir(),"aq-indicators-"));
const web=path.join(tmp,"Web");
fs.mkdirSync(web,{recursive:true});

for(const name of ["theme.css","vitals.js","indicators.js","web_log.js","dom_reconcile.js"]){
  const source=path.join(root,"src","AssistQuestEditor.App","Web",name);
  if(fs.existsSync(source)) fs.copyFileSync(source,path.join(web,name));
}

const html=fs.readFileSync(path.join(root,"src","AssistQuestEditor.App","Web","indicators.html"),"utf8")
  .replace(/\?v=[0-9a-f]+/g,"")
  .replace("</head>","<script>window.chrome={webview:{listeners:new Map(),addEventListener(t,h){const a=this.listeners.get(t)||[];a.push(h);this.listeners.set(t,a)},postMessage(m){(window.__sent=window.__sent||[]).push(m)}}}</script></head>");
fs.writeFileSync(path.join(web,"index.html"),html);

const snapshot={
  player:{position:{x:0,y:0,z:0},speedKmh:72},
  // Физические поля ОБЯЗАТЕЛЬНЫ: энергию и жидкость карточка показывает в
  // килокалориях и миллилитрах (2750/5000 ккал, 2100/3000 мл), а не во
  // внутренних единицах 0..10000. Без них вернулся бы дефект «потолок 10000».
  playerVitals:{health:8000,maxHealth:10000,energy:5500,maxEnergy:10000,hydration:7000,maxHydration:10000,fatigue:4000,maxFatigue:10000,resilience:6000,metabolism:6000,
    energyKilocalories:2750,maxEnergyKilocalories:5000,hydrationMilliliters:2100,maxHydrationMilliliters:3000},
  conditions:{stress:300,cumulativeStress:0,cumulativeFatigue:10000,cumulativeEnergy:500,cumulativeHydration:0,
    stomach:{energyRemaining:1500,hydrationRemaining:500,energyPerGameSecond:0.5,hydrationPerGameSecond:0.25},
    lastConsumedItemId:"food.meal",
    effects:[{id:"bull",name:"Бык",remainingRealSeconds:3600,isDebuff:false}]},
  character:{buffs:[],debuffs:[],skills:[{id:"skill.medicine",name:"Фельдшер",level:2}]},
  conditionRates:{health:0,energy:-41.67,hydration:-10.5,fatigue:92.6,stress:0.9,resilience:0,metabolism:-1.3,
    // НОМИНАЛЫ И ГРАНИЦЫ ПРИХОДЯТ ОТ ДОМЕНА. Раньше монитор зашивал «60»
    // литералами, и правка калибровки до него не доезжала.
    reducedMetabolismPercent:45,elevatedMetabolismPercent:75,
    defaultMetabolismPercent:60,defaultResiliencePercent:60,elevatedBonusFullPercent:75,
    exhaustionResilienceDivisor:2,bumResilienceCapPercent:50,
    resilienceReturnPerQuarterHour:0.5,resilienceBumReturnPerQuarterHour:1},
  // Галочка «%/время» — из НАСТРОЕК Хоста: страница её не хранит, а рисует
  // присланное. Фикстура подаёт включённое значение: оно же значение по умолчанию.
  percentUnits:true
};

const {browser}=await openBrowser();
try{
  const page=await browser.newPage({viewport:{width:1000,height:900}});
  const errors=[]; page.on("pageerror",e=>errors.push(String(e)));
  await page.goto("file:///"+path.join(web,"index.html").replace(/\\/g,"/"));
  // Первый снимок подаёт ПИЩЕВАРЕНИЕ С ПОРЦИЯМИ, а не одну «последнюю съеденную»
  // строку. Раньше строка предмета в шкале бралась из `lastConsumedItemId`, и
  // автор справедливо указал, что предметов может быть НЕСКОЛЬКО: пока вода
  // переваривается, съеденный апельсин подписывал восстановление, а сама вода из
  // строки исчезала. Теперь источник — порции, у каждой свой вклад по каждой
  // шкале. `lastConsumedItemId` оставлен в фикстуре нарочно: если он снова
  // начнёт влиять на строку, проверки покраснеют.
  const mealDigestion={
    // Вместимость — 2000 мл (задано автором: «увеличиваем с 1000 до 2000»).
    occupiedLiters:0.4,totalLiters:2,occupiedMilliliters:400,totalMilliliters:2000,
    // Пропускная способность — первое влияние в списке: игрок обязан видеть
    // лимит, из которого считаются сроки порций.
    dryThroughputMillilitersPerHour:1000,
    energyPerMinute:60,hydrationPerMinute:75,
    portions:[
      {itemId:"water.bottle",volumeLiters:0.5,volumeMilliliters:500,kilocalories:0,waterMilliliters:500,
        remainingGameSeconds:1200,energyPerMinute:0,hydrationPerMinute:45},
      {itemId:"food.meal",volumeLiters:0.4,volumeMilliliters:400,kilocalories:600,waterMilliliters:250,
        remainingGameSeconds:1800,energyPerMinute:30,hydrationPerMinute:30}
    ],
    factors:[]
  };
  await page.evaluate(data=>{(window.chrome.webview.listeners.get("message")||[]).forEach(fn=>fn({data:JSON.stringify(data)}));},{
    type:"snapshot",snapshot,conditionRates:snapshot.conditionRates,daylight:{gameClockLabel:"12:00"},
    digestion:mealDigestion,
    itemCatalog:[
      {id:"food.meal",name:"Паёк",description:"Нормальная еда"},
      {id:"water.bottle",name:"Вода",description:"Питьё"}
    ],
    simulationRunning:true,simulationPaused:false
  });
  await page.waitForTimeout(80);

  // Карточки ШКАЛ, а не все `.indicatorCard`. Блок пищеварения тоже несёт класс
  // `indicatorCard` и стоит в разметке ПЕРВЫМ, поэтому выборка без фильтра по
  // `data-scale-key` брала бы его как «первую шкалу»
  // и падала на отсутствии `.indicatorName` — так это и всплыло после правок.
  const cardData=await page.$$eval(".indicatorCard[data-scale-key]",nodes=>nodes.map(node=>({
    key:node.getAttribute("data-scale-key"),
    name:node.querySelector(".indicatorName")?.textContent.trim()||"",
    value:node.querySelector(".indicatorValue")?.textContent.trim()||"",
    dynamic:node.querySelector(".indicatorDynamics")?.textContent.trim()||"",
    factors:[...node.querySelectorAll(".indicatorFact")].map(x=>({text:x.textContent.trim(),cls:x.className}))
  })));

  // Геометрия верхней строки и карточки: жирность, ширина блоков, шкала в третьей
  // ячейке и отсутствие обрезки динамики. Проверяем ВЫЧИСЛЕННЫЕ стили, а не текст
  // разметки: автор просил конкретную жирность и ширину, и они измеряются в CSS,
  // а не в разметке — совпадение строки в HTML ничего бы не доказывало.
  const layout=await page.evaluate(()=>{
    const card=document.querySelector(".indicatorCard[data-scale-key]");
    const name=card.querySelector(".indicatorName");
    const dyn=card.querySelector(".indicatorDynamics");
    const value=card.querySelector(".indicatorValue");
    const bar=value.querySelector(".dualBar");
    const valueText=value.querySelector(".indicatorValueText");
    const body=document.querySelector(".indicatorsBody");
    const cards=[...document.querySelectorAll(".indicatorCard[data-scale-key]")];
    const topRows=new Set(cards.map(c=>Math.round(c.getBoundingClientRect().top)));
    return {
      cardWidth:Math.round(card.getBoundingClientRect().width),
      nameWeight:getComputedStyle(name).fontWeight,
      nameSize:getComputedStyle(name).fontSize,
      nameTooltip:name.getAttribute("data-game-tooltip")||"",
      dynWeight:getComputedStyle(dyn).fontWeight,
      dynWrap:getComputedStyle(dyn).whiteSpace,
      valueWeight:getComputedStyle(value).fontWeight,
      cardOverflow:getComputedStyle(card).overflow,
      cardOverflowY:card.scrollHeight-card.clientHeight,
      bodyWrap:getComputedStyle(body).flexWrap,
      bodyOverflowY:getComputedStyle(body).overflowY,
      hasBar:!!bar,
      barHeight:bar?Math.round(bar.getBoundingClientRect().height):0,
      // Шкала стоит СВЕРХУ значения: её верх меньше верхней границы текстового блока.
      barAboveValue:!!bar&&!!valueText&&bar.getBoundingClientRect().top<=valueText.getBoundingClientRect().top,
      rows:topRows.size
    };
  });

  const failures=[];
  const check=(ok,msg)=>{if(!ok)failures.push(msg)};
  const all=cardData.map(c=>[c.name,c.value,c.dynamic,...c.factors.map(x=>x.text)].join(" ")).join(" ");
  check(errors.length===0,"ошибки страницы: "+errors.join(" | "));
  check(cardData.length===7,"ожидалось 7 шкал, получено "+cardData.length);
  check(!all.includes("Активные эффекты"),"осталась отдельная карточка эффектов");
  check(!/раст[ёе]т|падает/.test(all),"остались старые слова направления");
  const energy=cardData.find(c=>c.key==="energy");
  check(energy&&energy.dynamic.includes("▼"),"нет треугольника падения энергии");
  // ВКЛЮЧЁННАЯ галочка (состояние по умолчанию) печатает скорость ПРОЦЕНТОМ за
  // минуту и час: «−0,417%/мин (−25%/ч)». Проверку формата ведём НИЖЕ, где снимок
  // подан с явно выключенной галочкой, а здесь держим главное следствие:
  // физических единиц в шапке при включённой галочке быть не должно.
  check(energy&&energy.dynamic.includes("/мин")&&energy.dynamic.includes("%/ч"),
    "нет скорости энергии в %/мин и %/ч: "+(energy?energy.dynamic:"нет карточки"));
  check(!/ккал\/мин|мл\/мин/.test(energy?energy.dynamic:""),
    "с включённой галочкой скорость энергии осталась в ккал/мин: "+(energy?energy.dynamic:"нет карточки"));
  const hydrationCard=cardData.find(c=>c.key==="hydration");
  check(energy&&energy.value.includes("(")&&energy.value.endsWith("%"),"неверный формат значения энергии");
  // Потолок энергии — 5000 ккал, жидкости — 3000 мл: физические величины,
  // которые присылает домен. Внутренние 10000 в карточке видны быть не должны.
  check(energy&&energy.value.includes("2750 / 5000 ккал"),"энергия не показана в килокалориях: "+(energy?energy.value:"нет карточки"));
  const hydration=cardData.find(c=>c.key==="hydration");
  check(hydration&&hydration.value.includes("2100 / 3000 мл"),"жидкость не показана в миллилитрах: "+(hydration?hydration.value:"нет карточки"));
  check(!cardData.some(c=>(c.key==="energy"||c.key==="hydration")&&c.value.includes("10000")),"физическая шкала показывает внутренний потолок 10000");
  check(energy&&energy.factors.some(x=>x.text.includes("Базовый расход энергии")),"нет базового фактора энергии");
  check(energy&&energy.factors.some(x=>x.cls.includes("permanentInactive")),"нет неактивных постоянных факторов");

  // ── Строка «Базовый расход» берёт числа У ДОМЕНА, а не из литералов ──────
  //
  // Дефект автора: он поправил нормы расхода в `CharacterVitalsTuning`, строка
  // динамики за ним пошла, а «Базовый расход энергии/жидкости» осталась с
  // прежними 13,9 ккал и 5 мл — страница считала её по СВОЕЙ таблице чисел и
  // правку калибровки не замечала вовсе.
  //
  // Сторож берёт нормы, которые страница знать не может (они «пришли» из
  // conditionRates): −41,67 ед./мин — это ровно 20,8 ккал/мин при потолке 5000,
  // а −10,5 ед./мин — 3,1 мл/мин при потолке 3000. Оба числа выведены из ДАННЫХ
  // фикстуры, а не из калибровки, поэтому проверка ловит именно дублирование
  // норм в JavaScript: пока они там есть, строка показывает старые 13,9 ккал и
  // 5 мл и краснеет.
  const baseRow=card=>card.factors.find(x=>x.text.includes("Базовый расход"));
  const energyBase=baseRow(energy), hydrationBase=baseRow(hydration);
  // С включённой галочкой вклад тоже в процентах: −41,67 ед./мин при потолке
  // 10000 — это −0,417%/мин. Число выведено из ДАННЫХ фикстуры, поэтому проверка
  // ловит дублирование норм в JavaScript.
  check(!!energyBase&&energyBase.text.includes("-0.417%/мин"),
    "базовый расход энергии не совпал с доменной нормой: "+(energyBase?energyBase.text.trim():"строка не найдена"));
  check(!!hydrationBase&&hydrationBase.text.includes("-0.105%/мин"),
    "базовый расход жидкости не совпал с доменной нормой: "+(hydrationBase?hydrationBase.text.trim():"строка не найдена"));

  // ── Галочка «%/время» ───────────────────────────────────────────────────
  //
  // Задано автором: «Нужна галочка: "%/время". По умолчанию включена. Если
  // галочка включена, то все единицы измерения в окне визуально заменяются на
  // процент в минуту и процент в час соответственно: например 2%/мин (120%/ч).
  // Если выключена, то как сейчас показываются полные единицы, миллилитры, кДж».
  //
  // С галочкой скорость шкалы печатается процентом ЗА МИНУТУ и за час; без неё —
  // физической величиной (ккал/мин, мл/мин, ед./мин).
  check(!cardData.some(c=>/ккал\/мин|мл\/мин/.test(c.dynamic)),
    "с включённой галочкой скорость показана физической величиной: "+
    cardData.map(c=>c.dynamic).filter(Boolean).join(" | "));
  check(cardData.some(c=>/%\/мин/.test(c.dynamic)&&/%\/ч/.test(c.dynamic)),
    "нет скорости ни в %/мин, ни в %/ч: "+cardData.map(c=>c.dynamic).join(" | "));

  // Переключение галочки: узел СТАТИЧНЫЙ и живёт в шапке, а не в теле монитора,
  // поэтому его состояние обязано переживать перерисовку тела (раз в секунду).
  const toggleBefore=await page.$eval("#percentUnitsToggle",n=>({checked:n.checked,exists:true}));
  check(toggleBefore.checked,"галочка «%/время» по умолчанию не включена");

  await page.click("#percentUnitsToggle");
  await page.waitForTimeout(150);
  const offData=await page.$$eval(".indicatorCard[data-scale-key]",nodes=>nodes.map(node=>({
    key:node.getAttribute("data-scale-key"),
    dynamic:node.querySelector(".indicatorDynamics")?.textContent.trim()||"",
    factors:[...node.querySelectorAll(".indicatorFact")].map(x=>x.textContent.trim())
  })));
  check(!offData.some(c=>/%\/мин/.test(c.dynamic)),
    "после снятия галочки скорости остались в процентах: "+
    offData.map(c=>c.dynamic).join(" | "));
  check(offData.some(c=>/ккал\/мин|мл\/мин|ед\.\/мин/.test(c.dynamic)),
    "после снятия галочки нет ни одной физической скорости: "+
    offData.map(c=>c.dynamic).join(" | "));

  // ТЕ ЖЕ вклады, что выше проверялись процентами, без галочки обязаны читаться
  // в ФИЗИЧЕСКИХ единицах: −41,67 ед./мин при потолке 5000 ккал — это −20,8
  // ккал/мин (10000 единиц = 5000 ккал, значит одна килокалория = 2 единицы),
  // а −10,5 ед./мин — 3,1 мл/мин при потолке 3000. Оба числа выведены из данных
  // фикстуры: пока нормы продублированы в JavaScript, строка показывает старые
  // 13,9 ккал и 5 мл и краснеет.
  const offEnergy=offData.find(c=>c.key==="energy");
  const offHydration=offData.find(c=>c.key==="hydration");
  check(offEnergy&&offEnergy.dynamic.includes("20.8 ккал/мин"),
    "без галочки нет скорости энергии в ккал/мин: "+(offEnergy?offEnergy.dynamic:"нет карточки"));
  const offBaseRow=(card,label)=>card?(card.factors.find(t=>t.includes("Базовый расход"))||""):"";
  check(offBaseRow(offEnergy).includes("20.8 ккал/мин"),
    "базовый расход энергии не совпал с доменной нормой: "+offBaseRow(offEnergy));
  check(offBaseRow(offHydration).includes("3.1 мл/мин"),
    "базовый расход жидкости не совпал с доменной нормой: "+offBaseRow(offHydration));

  // Строка усвоения пищеварения без галочки — в ккал/мин, как и была.
  const offStomach=await page.$eval(".stomachHead",n=>n.textContent);
  check(/ккал\/мин/.test(offStomach),
    "без галочки динамика усвоения не в ккал/мин: "+offStomach);

  // Обратно: страница обязана вернуться к процентам БЕЗ перезагрузки.
  await page.click("#percentUnitsToggle");
  await page.waitForTimeout(150);
  const backOn=await page.$$eval(".indicatorCard[data-scale-key]",nodes=>nodes.map(node=>node.querySelector(".indicatorDynamics")?.textContent.trim()||""));
  check(backOn.some(t=>/%\/мин/.test(t)),"галочка не включила проценты обратно: "+backOn.join(" | "));

  // Значения шкал галочке НЕ подчиняются (решение автора): «2750 / 5000 ккал»
  // остаётся и при включённой галочке — это значение, а не скорость.
  check(energy&&energy.value.includes("2750 / 5000 ккал"),
    "значение энергии перестало показывать килокалории: "+(energy?energy.value:"нет карточки"));
  const chips=await page.$$eval(".indicatorChip",nodes=>nodes.map(n=>({text:n.textContent.trim(),perk:n.getAttribute("data-perk-link"),item:n.getAttribute("data-item-link")})));
  check(chips.some(x=>x.text==="Бык"&&x.perk),"бафф «Бык» не кликабелен");
  check(chips.some(x=>x.text==="Паёк"&&x.item),"предмет не кликабелен");

  // ── В шкале показаны ВСЕ усваивающиеся предметы ─────────────────────────
  // Автор: «в пунктах шкалы нужно отображать все усваивающиеся предметы, которые
  // влияют на шкалу». Из фикстуры в желудке лежат вода (только жидкость) и паёк
  // (обе шкалы), поэтому: в жидкости обязаны быть ОБА, а в энергии — только
  // паёк с ЕГО вкладом, а не с общей скоростью желудка.
  check(chips.filter(x=>x.text==="Вода"&&x.item).length===1,
    "в шкале жидкости не показана вода: "+JSON.stringify(chips.map(x=>x.text)));
  const energyItemRow=(cardData.find(c=>c.key==="energy")?.factors||[])
    .map(f=>f.text).join(" | ");
  const hydrationItemRow=(cardData.find(c=>c.key==="hydration")?.factors||[])
    .map(f=>f.text).join(" | ");
  // Вода даёт ТОЛЬКО жидкость: в энергии её быть не должно.
  check(!energyItemRow.includes("Вода"),
    "вода показана в шкале энергии, хотя жидкости не даёт энергии: "+energyItemRow);
  check(hydrationItemRow.includes("Вода"),
    "в шкале жидкости нет воды, хотя она лежит в желудке: "+hydrationItemRow);
  check(energyItemRow.includes("Паёк"),
    "в шкале энергии нет пайка, хотя он лежит в пищеварении: "+energyItemRow);
  // Вклад берётся У ПОРЦИИ, а не из общей суммы пищеварения. Паёк даёт 30 ед./мин
  // энергии из 60 общих, то есть 0,3%/мин, а не 0,6%/мин общей скорости желудка.
  check(/усваивается.*0[.,]3%\/мин/.test(energyItemRow),
    "в строке предмета показана не его скорость, а общая по желудку: "+energyItemRow);
  // Вода даёт 45 ед./мин жидкости из 75 общих, то есть 0,45%/мин.
  check(/усваивается.*0[.,]45%\/мин/.test(hydrationItemRow),
    "в строке воды показана не её скорость: "+hydrationItemRow);

  // ── Занятый и полный объём пищеварения ──────────────────────────────────
  // Автор: «нужно справа от желудка также показывать занятый объём и через слеш
  // весь объём» + «объём отображать в миллилитрах». Миллилитры,
  // а не литры: 5 мл мёда игрок обязан видеть как «5 мл», иначе дробные объёмы
  // округляются до «0 л» и разница между предметами исчезает.
  const volume=await page.$eval(".stomachVolume",n=>n.textContent.trim());
  check(/^400\s*\/\s*2000\s*мл$/.test(volume),
    "объём пищеварения не показан в миллилитрах как «занято / всего»: "+volume);
  const state=await page.$eval("#characterStateValue",n=>({text:n.textContent.trim(),cls:n.className,title:n.title}));
  check(state.text&&/state[A-Z]/.test(state.cls)&&state.title.includes("/100"),"не рассчитано состояние персонажа");
  const grid=await page.$eval(".indicatorTopRow",n=>getComputedStyle(n).gridTemplateColumns);
  check(String(grid).split(/\s+/).length===3,"верхняя строка не из трёх колонок: "+grid);
  const sent=await page.evaluate(()=>window.__sent||[]);
  check(sent.some(x=>x.action==="indicators_ready"),"нет indicators_ready");

  // Жирность: имя шкалы — 700, остальные ячейки верхней строки — 600.
  check(layout.nameWeight==="700","имя шкалы не 700: "+layout.nameWeight);
  check(layout.dynWeight==="600","динамика не 600: "+layout.dynWeight);
  check(layout.valueWeight==="600","значение не 600: "+layout.valueWeight);
  check(layout.nameSize==="12px","имя шкалы не 12px: "+layout.nameSize);
  // Ширина блока — 500px, высота — по контенту, без обрезки и внутренней прокрутки.
  check(Math.abs(layout.cardWidth-500)<=1,"ширина карточки не 500px: "+layout.cardWidth);
  check(layout.cardOverflowY<=0,"карточка обрезает контент по высоте: "+layout.cardOverflowY);
  // Динамику не обрезаем многоточием.
  check(layout.dynWrap!=="nowrap","динамика обрезается: "+layout.dynWrap);
  // Шкала в третьей ячейке, НАД значением.
  check(layout.hasBar,"в третьей ячейке нет шкалы");
  check(layout.barHeight>=5&&layout.barHeight<=10,"высота шкалы вне разумных границ: "+layout.barHeight);
  check(layout.barAboveValue,"шкала не над значением");
  // Блоки раскладываются в несколько колонок при широком окне (1000px → две
  // колонки по 500px) и заполняют свободное место.
  check(layout.bodyWrap==="wrap","тело монитора не переносит блоки: "+layout.bodyWrap);
  check(layout.bodyOverflowY==="auto","тело монитора не прокручивается по вертикали: "+layout.bodyOverflowY);
  check(layout.rows>=2,"блоки не разложены в несколько рядов: рядов "+layout.rows);
  // Подсказка на имени шкалы: описание и влияние.
  check(layout.nameTooltip.length>20&&layout.nameTooltip.includes("На что влияет"),"нет подсказки шкалы: "+layout.nameTooltip);

  // Подсказка обязана РЕАЛЬНО показываться по наведению, а не только лежать в
  // атрибуте. Прежде клик по чипу «Вода» и наведение на имя шкалы ничего не
  // делали — атрибут был, а обработчик отсутствовал. Проверяем делегированный
  // слушатель AssistVitals.attachTooltips: он один на документ и обязан
  // пережить пересборку разметки (innerHTML заменяет узлы).
  await page.hover(".indicatorName");
  await page.waitForTimeout(60);
  const tip=await page.evaluate(()=>{
    const node=document.getElementById("assistGameTooltip");
    return {exists:!!node,shown:!!node&&node.style.display==="block",text:node&&node.textContent||""};
  });
  check(tip.exists,"нет элемента подсказки #assistGameTooltip в мониторе");
  check(tip.shown,"подсказка не показывается по наведению на имя шкалы");
  check(tip.text.includes("На что влияет"),"подсказка пуста или без влияния: "+tip.text.slice(0,80));

  const push=(data)=>page.evaluate(payload=>{
    (window.chrome.webview.listeners.get("message")||[]).forEach(fn=>fn({data:JSON.stringify(payload)}));
  },data);

  // ── Блок «Пищеварение» ───────────────────────────────────────────────────
  //
  // Прямой тест дефекта автора: «использовал воду, затем Апельсин — вода
  // исчезла». Блок обязан показать СРАЗУ ОБЕ порции, каждую со своим таймером,
  // и занимать всю ширину монитора — его площадь и есть объём.
  //
  // Вместимость — 2000 мл (задано автором), поэтому и «занято / всего»,
  // и сумма миллилитров участков считаются против нового числа.
  const stomach={...snapshot,digestion:{
    volumeFraction:0.35,occupiedLiters:0.7,totalLiters:2,
    occupiedMilliliters:700,totalMilliliters:2000,
    dryThroughputMillilitersPerHour:1000,
    energyPerMinute:60,hydrationPerMinute:100,
    portions:[
      {itemId:"water.bottle",volumeFraction:0.25,volumeLiters:0.5,volumeMilliliters:500,kilocalories:0,waterMilliliters:500,remainingGameSeconds:1200,remainingGameMinutes:20,waterContent:1},
      {itemId:"food.orange",volumeFraction:0.1,volumeLiters:0.2,volumeMilliliters:200,kilocalories:90,waterMilliliters:170,remainingGameSeconds:2400,remainingGameMinutes:40,waterContent:0.85}
    ],
    factors:[{text:"Пропускная способность: 1000 мл/ч сухого, 1350 мл/ч воды",useful:true},
      {text:"Метаболизм 75%: усвоение ×2,5 и +0,5% объёма",useful:true}]
  }};
  await push({
    type:"snapshot",snapshot:stomach,conditionRates:snapshot.conditionRates,digestion:stomach.digestion,
    daylight:{gameClockLabel:"12:00"},simulationRunning:true,simulationPaused:false,
    // Меню желудка берёт список из КАТАЛОГА употребимого (consumables), а не из
    // инвентаря: автор задал, что это «каталог существующих объектов, которые
    // съедобны», и что инвентарь в этом действии не участвует вовсе.
    //
    // `fits` приходит из домена: желудок занят на 700 мл, поэтому вода (500 мл)
    // помещается, а паёк (400 мл) — НЕТ. Оба пункта обязаны быть в списке, но
    // паёк — серым и неактивным (требование автора).
    //
    // У апельсина ОБЪЁМ ПОРЦИИ (200) НАРОЧНО ОТЛИЧАЕТСЯ от содержимого ВОДЫ (170)
    // — это прямой сторож дефекта автора: «Банан 110 мл, а добавилось 150».
    // Меню печатало `waterMilliliters`, и в списке добавления стоял объём воды,
    // а не объём порции. Показ 170 вместо 200 обязан валить проверку.
    consumables:[
      {id:"water.bottle",name:"Вода",color:"#3a6ea5",grams:500,volumeMilliliters:500,kilocalories:0,waterMilliliters:500,fits:true},
      {id:"food.orange",name:"Апельсин",color:"#d78a36",grams:200,volumeMilliliters:200,kilocalories:90,waterMilliliters:170,fits:false},
      {id:"food.meal",name:"Паёк",color:"#b27b50",grams:400,volumeMilliliters:400,kilocalories:600,waterMilliliters:250,fits:false},
      {id:"soap",name:"Мыло",color:"#5f7ea1",grams:0,volumeMilliliters:0,kilocalories:0,waterMilliliters:0,fits:true}
    ],
    itemCatalog:[
      {id:"water.bottle",name:"Вода",color:"#3a6ea5"},
      {id:"food.orange",name:"Апельсин",color:"#d78a36"},
      {id:"food.meal",name:"Паёк",color:"#b27b50"},
      {id:"soap",name:"Мыло",color:"#5f7ea1"}
    ]
  });
  await page.waitForTimeout(80);

  const stomachBlock=await page.evaluate(()=>{
    const card=document.querySelector("[data-stomach]");
    if(!card) return null;
    const area=card.querySelector("#stomachArea");
    const nodes=[...card.querySelectorAll(".stomachPortion")];
    const portions=nodes.map(n=>({
      item:n.getAttribute("data-stomach-item"),
      ml:Number(n.getAttribute("data-stomach-volume-ml")||0),
      widthMl:Number(n.getAttribute("data-stomach-ml")||0),
      row:Number(n.getAttribute("data-stomach-row")||0),
      start:Number(n.getAttribute("data-stomach-start")||0),
      part:Number(n.getAttribute("data-stomach-part")||0),
      name:n.querySelector(".stomachPortionName")?.textContent.trim()||"",
      timer:n.querySelector(".stomachPortionTimer")?.textContent.trim()||"",
      width:n.getBoundingClientRect().width,
      height:n.getBoundingClientRect().height,
      top:Math.round(n.getBoundingClientRect().top),
      left:Math.round(n.getBoundingClientRect().left)
    }));
    return {
      title:card.querySelector(".stomachTitle")?.textContent.trim()||"",
      dynamics:card.querySelector(".stomachDynamics")?.textContent.trim()||"",
      factors:card.querySelector(".stomachFactors")?.textContent.trim()||"",
      volumeText:card.querySelector(".stomachVolume")?.textContent.trim()||"",
      cardWidth:Math.round(card.getBoundingClientRect().width),
      bodyWidth:Math.round(document.querySelector(".indicatorsBody").getBoundingClientRect().width),
      areaDisplay:area?getComputedStyle(area).display:"",
      areaPosition:area?getComputedStyle(area).position:"",
      areaWidth:area?Math.round(area.getBoundingClientRect().width):0,
      areaHeight:area?Math.round(area.getBoundingClientRect().height):0,
      areaInnerWidth:area?area.clientWidth:0,
      areaInnerHeight:area?area.clientHeight:0,
      portionPosition:nodes.length?getComputedStyle(nodes[0]).position:"",
      portions
    };
  });

  check(stomachBlock!==null,"блок «Пищеварение» не отрисован");
  if(stomachBlock){
    // ПРЕДМЕТОВ должно быть РОВНО два: подмена цикла на «только последняя» —
    // ровно тот дефект, который видел автор. Участков (прямоугольников) может
    // быть больше: крупная порция продолжается в следующем ряду, и это тот же
    // предмет — «Апельсин стёр Воду» от этого не возвращается.
    const stomachItems=[...new Set(stomachBlock.portions.map(p=>p.item))];
    check(stomachItems.length===2,
      "в пищеварении не 2 предмета, а "+stomachItems.length+": "+stomachItems.join(","));
    check(stomachItems.includes("water.bottle")&&stomachItems.includes("food.orange"),
      "в пищеварении не вода и апельсин: "+stomachItems.join(","));
    check(stomachBlock.title==="Пищеварение",
      "нет заголовка «Пищеварение»: "+stomachBlock.title);
    check(stomachBlock.dynamics.includes("Усвоение"),"нет общей динамики усвоения: "+stomachBlock.dynamics);
    check(stomachBlock.dynamics.includes("%/мин"),"динамика усвоения не в %/мин: "+stomachBlock.dynamics);
    check(stomachBlock.factors.includes("усвоение"),"влияние на усвоение не показано: "+stomachBlock.factors);

    // Подписанные участки (первые части) — по одному на предмет: именно они несут
    // название и таймер, и именно их проверяют последующие сценарии.
    const labelled=stomachBlock.portions.filter(p=>p.name);
    check(labelled.length===2,"подписанных участков не 2, а "+labelled.length);
    const first=labelled[0], second=labelled[1];
    check(first&&first.item==="water.bottle","первая подпись не вода: "+(first||{}).item);
    check(second&&second.item==="food.orange","вторая подпись не апельсин: "+(second||{}).item);
    check(first&&first.name==="Вода","вода подписана не названием: "+(first||{}).name);
    // У каждого предмета СВОЙ таймер.
    check(first&&second&&first.timer&&second.timer,"у порции нет таймера усвоения");
    check(first&&second&&first.timer!==second.timer,"таймеры порций совпали — таймер не по порции");

    // ── ОБЛАСТЬ ПИЩЕВАРЕНИЯ: 4 строки по 35px, площадь = объём ─────────────
    // Автор: «объём нужно распределить по области размером 470x140px. Предметы
    // должны в этой области располагаться плиткой без пересечения или наложения.
    // Занятое и свободное пространство в этой области должно строго
    // соответствовать занятым и свободным миллилитрам».
    //
    // Строк по-прежнему ЧЕТЫРЕ (высота 140px задана автором), а вместимость
    // выросла вдвое — поэтому на строку теперь приходится 500 мл, а не 250.
    check(stomachBlock.portionPosition==="absolute",
      "порция не позиционирована укладчиком: "+stomachBlock.portionPosition);
    check(stomachBlock.areaPosition==="relative",
      "область пищеварения не является системой координат для плиток: "+stomachBlock.areaPosition);
    // Высота области: 4 строки по 35px + отступы. Именно ЖЁСТКАЯ, а не по
    // содержимому: при высоте «по контенту» область не отвечала бы объёму, и
    // «занято 1 из 1» показывалось бы при видимой пустоте — дефект автора.
    check(Math.abs(stomachBlock.areaHeight-140)<=2,
      "область пищеварения не 140px (4 строки по 35px): "+stomachBlock.areaHeight);
    check(Math.abs(stomachBlock.cardWidth-500)<=1,
      "блок пищеварения не шириной как карточка шкалы: "+stomachBlock.cardWidth);

    // Пропорция пикселей: 500 мл = внутренняя ширина ряда. Внутренняя — потому
    // что укладчик вычитает отступ области (7px с каждой стороны), и брать
    // clientWidth напрямую значило бы проверять не ту формулу, которой
    // пользуется страница.
    const pxPerMl=(stomachBlock.areaInnerWidth-14)/500;
    // Вода (500 мл) занимает РОВНО один ряд; апельсин (200 мл) — один участок.
    // При прежней вместимости вода делилась на два участка по 250 мл; теперь
    // 500 мл — это целый ряд, и деление начинается только с 501 мл.
    const waterParts=stomachBlock.portions.filter(p=>p.item==="water.bottle");
    const orangeParts=stomachBlock.portions.filter(p=>p.item==="food.orange");
    check(waterParts.length===1,
      "вода 500 мл не занимает целый ряд: "+waterParts.length);
    check(orangeParts.length===1,
      "апельсин 200 мл разложен не одним участком: "+orangeParts.length);
    // Подпись несёт первый (и единственный) участок.
    check(waterParts[0]&&waterParts[0].name==="Вода",
      "вода не подписана: "+JSON.stringify(waterParts.map(p=>p.name)));
    // Объём, записанный в участке, — объём всего предмета, а не ряда.
    check(waterParts.every(p=>Math.abs(p.ml-500)<1),
      "объём воды в участках не 500 мл: "+waterParts.map(p=>p.ml).join(","));

    // Сумма ШИРИН участков (в миллилитрах ряда) отвечает занятым миллилитрам:
    // это и есть «площадь = объём».
    const rowMlSum=stomachBlock.portions.reduce((s,p)=>s+p.widthMl,0);
    check(Math.abs(rowMlSum-700)<1,
      "сумма миллилитров участков не равна занятым 700 мл: "+rowMlSum);
    // А в ПИКСЕЛЯХ ширина каждого участка = его миллилитры минус зазор: проверяем
    // поэлементно, потому что сумма «съедает» округления.
    stomachBlock.portions.forEach(p=>{
      const expected=Math.max(2,Math.round(p.widthMl*pxPerMl)-3);
      check(Math.abs(p.width-expected)<=2,
        "ширина участка не отвечает объёму: "+p.item+" "+p.widthMl+" мл → "+
        p.width+"px, ожидалось "+expected);
    });

    // Ни один участок не шире ряда: иначе он вылез бы за область.
    check(stomachBlock.portions.every(p=>p.width<=pxPerMl*500+2),
      "участок шире ряда: "+stomachBlock.portions.map(p=>p.width).join(","));

    // НАЛОЖЕНИЙ НЕТ: внутри одного ряда участки не пересекаются.
    const byRow={};
    stomachBlock.portions.forEach(p=>{(byRow[p.row]=byRow[p.row]||[]).push(p);});
    Object.keys(byRow).forEach(row=>{
      const sorted=byRow[row].slice().sort((a,b)=>a.left-b.left);
      for(let i=1;i<sorted.length;i++){
        const prev=sorted[i-1], cur=sorted[i];
        check(prev.left+prev.width<=cur.left+1,
          "участки наложились в ряду "+row+": "+
          (prev.item+" кончается на "+(prev.left+prev.width)+", а "+cur.item+" начинается на "+cur.left));
      }
    });
    // Ряды укладываются в четыре: значит область отвечает ровно вместимости.
    check(stomachBlock.portions.every(p=>p.row>=0&&p.row<4),
      "участок вне четырёх рядов области: "+stomachBlock.portions.map(p=>p.row).join(","));
    // Высота участка — 35px минус зазор: одна из четырёх строк.
    check(stomachBlock.portions.every(p=>Math.abs(p.height-32)<=2),
      "высота участка не равна строке: "+stomachBlock.portions.map(p=>p.height).join(","));

    // Занятое место в шапке — В МИЛЛИЛИТРАХ (требование автора) и совпадает с
    // суммой объёмов порций: площадь области и число обязаны сходиться.
    check(/^700\s*\/\s*2000\s*мл$/.test(stomachBlock.volumeText),
      "объём пищеварения не показан в миллилитрах: «"+stomachBlock.volumeText+"»");

    // Тултип по наведению: список веществ.
    const orangePresent=await page.$("[data-stomach-item='food.orange']");
    check(!!orangePresent,"порции апельсина нет в разметке пищеварения");
    if(orangePresent){
      await page.hover("[data-stomach-item='food.orange']");
      await page.waitForTimeout(60);
      const portionTip=await page.evaluate(()=>{
        const n=document.getElementById("stomachTooltip");
        return {hidden:n.hidden,text:n.textContent||""};
      });
      check(!portionTip.hidden,"тултип порции не показан по наведению");
      check(portionTip.text.includes("90 ккал"),"в тултипе нет веществ порции: "+portionTip.text);
      check(portionTip.text.includes("170 мл"),"в тултипе нет жидкости порции: "+portionTip.text);
      check(portionTip.text.includes("Осталось"),"в тултипе нет остатка времени: "+portionTip.text);
    }

    // ПКМ по порции — просьба убрать её из желудка.
    const waterPresent=await page.$("[data-stomach-item='water.bottle']");
    if(waterPresent){
      await page.click("[data-stomach-item='water.bottle']",{button:"right"});
      await page.waitForTimeout(60);
      const afterRemove=await page.evaluate(()=>window.__sent||[]);
      check(afterRemove.some(x=>x.action==="remove_stomach_portion"&&x.itemId==="water.bottle"),
        "ПКМ по порции не просит удалить её из желудка");
    }

    // ПКМ по ПУСТОМУ месту — меню КАТАЛОГА употребимого; выбор просит употребить.
    //
    // Координаты берём у САМОЙ области и смещаемся ВНИЗ: порции занимают первую
    // строку плитки, поэтому клик по её середине попал бы по порции и открыл бы
    // удаление, а не меню.
    const areaBox=await page.$eval("#stomachArea",n=>{const r=n.getBoundingClientRect();return {x:r.left,y:r.top,width:r.width,height:r.height};});
    await page.mouse.click(areaBox.x+areaBox.width*0.5,areaBox.y+areaBox.height-6,{button:"right"});
    await page.waitForTimeout(60);
    const menuVisible=await page.evaluate(()=>{
      const n=document.getElementById("stomachMenu");
      return {
        hidden:n.hidden,
        head:n.querySelector(".stomachMenuHead")?.textContent.trim()||"",
        items:[...n.querySelectorAll("[data-use-item]")].map(b=>b.getAttribute("data-use-item")),
        names:[...n.querySelectorAll(".stomachMenuName")].map(b=>b.textContent.trim()),
        // Подписи пунктов: в них стоит ОБЪЁМ ПОРЦИИ — то самое число, которое
        // предмет займёт в желудке. Проверяем его отдельно от названий.
        metas:[...n.querySelectorAll(".stomachMenuItem")].map(b=>
          (b.querySelector(".stomachMenuMeta")||{}).textContent||""),
        // Требование автора: «в ПКМ-списке в желудке неактивны (серые) все
        // предметы, которые не поместятся». Собираем состояние КАЖДОГО пункта
        // вместе с подсказкой — иначе проверять «серость» было бы нечем.
        states:[...n.querySelectorAll("[data-use-item]")].map(b=>({
          id:b.getAttribute("data-use-item"),
          disabled:b.disabled,
          aria:String(b.getAttribute("aria-disabled")),
          cls:b.className,
          title:b.title
        }))
      };
    });
    check(!menuVisible.hidden,"ПКМ по пустому месту желудка не открывает меню");
    // Меню — КАТАЛОГ, а не инвентарь: в нём обязан быть предмет, которого в
    // инвентаре нет вовсе (мыло). Это и есть «список существующих объектов».
    check(menuVisible.items.includes("soap"),
      "меню берёт предметы не из каталога (мыла нет): "+menuVisible.items.join(","));
    check(menuVisible.items.includes("food.meal"),
      "в меню каталога нет еды: "+menuVisible.items.join(","));
    check(menuVisible.head.includes("Каталог"),
      "у меню нет подписи «каталог»: "+menuVisible.head);
    check(menuVisible.names.length===menuVisible.items.length,
      "пункт меню без названия: имён "+menuVisible.names.length+", пунктов "+menuVisible.items.length);

    // ── Объём в меню = объём порции (дефект «Банан 110 мл, добавилось 150») ──
    // Автор: «в желудок должно добавляться строго то количество миллилитров
    // предмета, которое указано в списке ПКМ-добавления». Значит подпись пункта
    // обязана нести ОБЪЁМ ПОРЦИИ: у апельсина — 200, а не 170 (содержимое воды).
    const metaOf=id=>menuVisible.metas[menuVisible.items.indexOf(id)]||"";
    check(/200 мл/.test(metaOf("food.orange")),
      "в меню у апельсина не показан объём порции 200 мл: «"+metaOf("food.orange")+"»");
    check(!/170 мл/.test(metaOf("food.orange")),
      "в меню у апельсина объём показан как содержимое воды (170 мл): «"+
      metaOf("food.orange")+"»");
    check(/500 мл/.test(metaOf("water.bottle")),
      "в меню у воды не показан объём порции: «"+metaOf("water.bottle")+"»");
    check(/400 мл/.test(metaOf("food.meal")),
      "в меню у пайка не показан объём порции 400 мл: «"+metaOf("food.meal")+"»");
    check(!/250 мл/.test(metaOf("food.meal")),
      "в меню у пайка объём показан как содержимое воды (250 мл): «"+
      metaOf("food.meal")+"»");
    // Объём обязан быть ПЕРВЫМ полем подписи: именно его автор сверял с желудком,
    // и он же единственное число, которое с желудком сходится.
    check(/^400 мл/.test(metaOf("food.meal")),
      "объём не стоит первым в подписи пункта: «"+metaOf("food.meal")+"»");

    // ── Что НЕ помещается — серое и неактивное ──────────────────────────────
    // Пищеварение занято на 700 мл (вода 500 + апельсин 200) из 2000, то есть
    // свободно 1300 мл. Паёк (400 мл) в такое свободное место КАК РАЗ помещается,
    // поэтому «непомещающийся» пункт задаётся фикстурой `fits:false`, а не
    // размером: проверяем, что страница ЧИТАЕТ признак домена, а не считает его
    // сама. Свободный объём в подсказке при этом берётся из снимка — 1300 мл.
    const mealState=menuVisible.states.find(s=>s.id==="food.meal");
    check(mealState&&mealState.disabled,
      "пункт пайка не отключён, хотя домен прислал fits:false: "+(mealState?JSON.stringify(mealState):"нет"));
    check(mealState&&String(mealState.aria)==="true",
      "у непомещающегося пункта нет aria-disabled: "+(mealState?JSON.stringify(mealState):"нет"));
    check(mealState&&mealState.cls.includes("stomachMenuItemDisabled"),
      "у непомещающегося пункта нет класса «серый»: "+(mealState?mealState.cls:"нет"));
    // Подсказка объясняет ПРИЧИНУ и называет СВОБОДНЫЙ объём: 1300 мл свободно,
    // порция 400 мл.
    check(mealState&&/свободно\s+1300\s+мл/.test(mealState.title),
      "в подсказке непомещающегося пункта нет свободного объёма: "+(mealState?mealState.title:"нет"));
    check(mealState&&/400\s+мл/.test(mealState.title),
      "в подсказке нет объёма порции: "+(mealState?mealState.title:"нет"));
    // Контроль в обратную сторону: помещающееся обязано быть ЖИВЫМ, иначе
    // «серое» получилось бы у всего списка и проверка выше ничего не доказывала.
    const waterState=menuVisible.states.find(s=>s.id==="water.bottle");
    check(waterState&&!waterState.disabled,
      "помещающийся пункт тоже отключён — серым стало всё: "+(waterState?JSON.stringify(waterState):"нет"));
    check(waterState&&!waterState.cls.includes("stomachMenuItemDisabled"),
      "у помещающегося пункта класс «серый»: "+(waterState?waterState.cls:"нет"));

    if(menuVisible.items.length){
      // Кликаем по ПОМЕЩАЮЩЕМУСЯ предмету: погашенный пункт браузер не нажмёт —
      // и это ровно то поведение, которого просил автор.
      await page.click("[data-use-item='water.bottle']");
      await page.waitForTimeout(60);
      const afterUse=await page.evaluate(()=>window.__sent||[]);
      // «Выбрал — попало в желудок»: одного сообщения достаточно, количество и
      // выдача в инвентарь в этом действии не участвуют.
      check(afterUse.some(x=>x.action==="use_stomach_item"&&x.itemId==="water.bottle"),
        "выбор предмета в меню не просит употребить его");
      const menuClosed=await page.evaluate(()=>document.getElementById("stomachMenu").hidden);
      check(menuClosed,"меню не закрылось после выбора предмета");
    }

    // Самая МЕЛКАЯ порция тоже обязана остаться видимой плиткой и не исчезнуть.
    //
    // У воды (500 мл) и апельсина (200 мл) ширина заведомо больше высоты, поэтому
    // на них проверка формы ничего не доказывает. Таблетка же даёт 3 мл: без
    // минимума в 2px её участок схлопнулся бы в невидимую линию и «съеденный
    // предмет исчез из желудка» — ровно тот дефект, который автор уже ловил.
    const tiny={...stomach,digestion:{...stomach.digestion,portions:[
      {itemId:"vitamin.c",volumeFraction:0.003,volumeLiters:0.003,volumeMilliliters:3,kilocalories:0,waterMilliliters:0,remainingGameSeconds:300,remainingGameMinutes:5}
    ]}};
    await push({type:"snapshot",snapshot:tiny,conditionRates:snapshot.conditionRates,digestion:tiny.digestion,
      daylight:{gameClockLabel:"12:00"},simulationRunning:true,simulationPaused:false,consumables:[]});
    await page.waitForTimeout(80);
    const tinyTile=await page.evaluate(()=>{
      const n=document.querySelector(".stomachPortion");
      if(!n) return null;
      const r=n.getBoundingClientRect();
      return {item:n.getAttribute("data-stomach-item"),w:Math.round(r.width),h:Math.round(r.height)};
    });
    check(tinyTile!==null,"мелкая порция не отрисована плиткой");
    check(tinyTile&&tinyTile.w>=2,
      "мелкая порция схлопнулась в невидимую линию: "+(tinyTile?tinyTile.w+"px":"нет"));
    check(tinyTile&&tinyTile.h>0,
      "у мелкой порции нет высоты строки: "+(tinyTile?tinyTile.h+"px":"нет"));

    // ── Шрифт подбирается под ширину плитки ─────────────────────────────────
    // Автор: «если текст внутри предмета не помещается, уменьшаем шрифт». Плитка
    // занимает до целого ряда (250 мл — это ≈463px), поэтому мелкие названия
    // ужимать НЕЛЬЗЯ — иначе они стали бы мельче соседей без причины. А вот у
    // МЕЛКОЙ порции участок узкий (60 мл — это ≈109px), и длинное название
    // («Соли для восстановления организма») обязано ужаться и ВЛЕЗТЬ, а не
    // обрезаться многоточием.
    const sorbentName="Сорбент";
    const longName="Соли для восстановления организма после тяжёлого дня";
    const fitFixture={...stomach,digestion:{...stomach.digestion,portions:[
      {itemId:"water.bottle",volumeFraction:0.5,volumeLiters:0.5,volumeMilliliters:500,kilocalories:0,waterMilliliters:500,remainingGameSeconds:1200},
      {itemId:"medicine.sorbent",volumeFraction:0.1,volumeLiters:0.1,volumeMilliliters:100,kilocalories:0,waterMilliliters:0,remainingGameSeconds:600},
      {itemId:"medicine.recovery_salts",volumeFraction:0.25,volumeLiters:0.25,volumeMilliliters:250,kilocalories:0,waterMilliliters:0,remainingGameSeconds:600}
    ]}};
    await push({type:"snapshot",snapshot:fitFixture,conditionRates:snapshot.conditionRates,digestion:fitFixture.digestion,
      daylight:{gameClockLabel:"12:00"},simulationRunning:true,simulationPaused:false,consumables:[],
      itemCatalog:[
        {id:"water.bottle",name:"Вода",color:"#3a6ea5"},
        {id:"medicine.sorbent",name:sorbentName,color:"#8f9aa5"},
        {id:"medicine.recovery_salts",name:longName,color:"#9aa7b3"}
      ]});
    await page.waitForTimeout(80);
    const fitted=await page.evaluate(()=>[...document.querySelectorAll(".stomachPortion")]
      .filter(n=>n.querySelector(".stomachPortionName"))
      .map(n=>{
        const name=n.querySelector(".stomachPortionName");
        return {
          item:n.getAttribute("data-stomach-item"),
          width:Math.round(n.getBoundingClientRect().width),
          font:parseFloat(getComputedStyle(name).fontSize),
          overflow:name.scrollWidth-name.clientWidth
        };
      }));
    // Подписанных участков — ровно три: продолжения крупных порций подписи не несут.
    check(fitted.length===3,"подбор шрифта: ожидалось 3 подписи, получено "+fitted.length);
    const wide=fitted.find(x=>x.item==="water.bottle");
    const mid=fitted.find(x=>x.item==="medicine.sorbent");
    const long=fitted.find(x=>x.item==="medicine.recovery_salts");
    // Короткое название: шрифт уменьшать НЕЛЬЗЯ — иначе «Вода» стала бы мельче
    // соседей без всякой причины.
    check(wide&&wide.font>=10,"шрифт уменьшен там, где текст и так помещается: "+(wide?wide.font:0));
    check(wide&&wide.overflow<=0,"короткое название обрезано: "+(wide?wide.overflow:0));
    check(mid&&mid.overflow<=0,"среднее название обрезано: "+(mid?mid.overflow:0));
    // Самое длинное название: шрифт ужимается — и текст ВЛЕЗАЕТ (а не обрезается
    // многоточием), причём не ниже предела читаемости.
    check(long&&long.font<10,
      "шрифт не уменьшен, хотя длинное название не помещается (ширина "+(long?long.width:0)+"): "+
      (long?long.font:0));
    check(long&&long.font>=6.5,
      "длинное название ужато ниже предела читаемости: "+(long?long.font:0));
    check(long&&long.overflow<=0,
      "уменьшенный шрифт всё равно обрезан: "+(long?long.overflow:0));
    // ПОДПИСЬ НЕ ЛОМАЕТ ПЛИТКУ: даже ужатый шрифт не выводит название за границы
    // участка — иначе текст наезжал бы на соседей, и «без наложения» перестало бы
    // выполняться на уровне текста.
    const labelOverflow=await page.evaluate(()=>[...document.querySelectorAll(".stomachPortion")]
      .filter(n=>n.querySelector(".stomachPortionName"))
      .map(n=>({
        item:n.getAttribute("data-stomach-item"),
        overflow:Math.round(n.querySelector(".stomachPortionName").getBoundingClientRect().right-
          n.getBoundingClientRect().right)
      })));
    labelOverflow.forEach(l=>{
      check(l.overflow<=1,
        "подпись вылезает за пределы участка: "+l.item+" на "+l.overflow+"px");
    });
    // Высота участков НЕ изменилась: строки имеют жёсткую высоту, поэтому ужатый
    // шрифт не делает плитку ниже соседей.
    const fitHeights=await page.evaluate(()=>[...document.querySelectorAll(".stomachPortion")]
      .map(n=>Math.round(n.getBoundingClientRect().height)));
    check(new Set(fitHeights).size===1,
      "участки разной высоты после подбора шрифта: "+fitHeights.join(","));

    // ── Кнопка «Содержимое» и окно со списком ───────────────────────────────
    // Автор: «после динамики желудка делаем кнопку "Содержимое" — открывает ещё
    // одно окно: простой список содержимого желудка с названиями и таймером. По
    // ПКМ можно также удалять объекты из списка».
    await push({type:"snapshot",snapshot:stomach,conditionRates:snapshot.conditionRates,digestion:stomach.digestion,
      daylight:{gameClockLabel:"12:00"},simulationRunning:true,simulationPaused:false,consumables:[],
      itemCatalog:[
        {id:"water.bottle",name:"Вода",color:"#3a6ea5"},
        {id:"food.orange",name:"Апельсин",color:"#d78a36"}
      ]});
    await page.waitForTimeout(80);

    // Кнопка обязана стоять ПОСЛЕ динамики усвоения и её влияний.
    const headOrder=await page.evaluate(()=>{
      const head=document.querySelector(".stomachHead");
      return head?[...head.children].map(c=>c.id||c.getAttribute("class")||""):[];
    });
    const dynamicsIndex=headOrder.findIndex(x=>String(x).includes("stomachDynamics"));
    const buttonIndex=headOrder.findIndex(x=>x==="stomachContentsButton");
    check(buttonIndex>=0,"в шапке блока нет кнопки «Содержимое»: "+JSON.stringify(headOrder));
    check(dynamicsIndex>=0&&buttonIndex>dynamicsIndex,
      "кнопка «Содержимое» не после динамики усвоения: "+JSON.stringify(headOrder));

    await page.click("#stomachContentsButton");
    await page.waitForTimeout(80);
    const contents=await page.evaluate(()=>{
      const n=document.getElementById("stomachWindow");
      return {
        hidden:n.hidden,
        display:getComputedStyle(n).display,
        title:n.querySelector(".stomachWindowTitle")?.textContent.trim()||"",
        rows:[...n.querySelectorAll(".stomachRow")].map(r=>({
          item:r.getAttribute("data-stomach-item"),
          name:r.querySelector(".stomachRowName")?.textContent.trim()||"",
          timer:r.querySelector(".stomachRowTimer")?.textContent.trim()||""
        }))
      };
    });
    // `display` проверяем отдельно от `hidden`: у окна стоит display:flex, который
    // ПЕРЕБИВАЕТ действие атрибута hidden, и без явного правила окно было бы видно
    // всегда. Если правило потеряется, hidden останется false, а окно — на экране.
    check(!contents.hidden&&contents.display!=="none",
      "кнопка «Содержимое» не открыла окно (hidden="+contents.hidden+", display="+contents.display+")");
    check(contents.title.includes("Содержимое"),"у окна нет заголовка: "+contents.title);
    check(contents.rows.length===2,"в списке не 2 порции, а "+contents.rows.length);
    check(contents.rows[0]&&contents.rows[0].name==="Вода","первая строка списка не вода: "+(contents.rows[0]||{}).name);
    check(contents.rows[1]&&contents.rows[1].name==="Апельсин","вторая строка списка не апельсин: "+(contents.rows[1]||{}).name);
    check(contents.rows[0]&&contents.rows[1]&&contents.rows[0].timer&&contents.rows[1].timer,
      "у строки списка нет таймера");
    check(contents.rows[0]&&contents.rows[1]&&contents.rows[0].timer!==contents.rows[1].timer,
      "таймеры строк совпали — таймер не по строке");

    // ПКМ по строке списка — удалить порцию (то же действие, что ПКМ по плитке).
    await page.click(".stomachRow[data-stomach-item='water.bottle']",{button:"right"});
    await page.waitForTimeout(60);
    const afterRowRemove=await page.evaluate(()=>window.__sent||[]);
    check(afterRowRemove.some(x=>x.action==="remove_stomach_portion"&&x.itemId==="water.bottle"),
      "ПКМ по строке списка не просит удалить порцию из желудка");

    // Escape убирает ВЕРХНИЙ слой, а не закрывает монитор: иначе, чтобы убрать
    // список, пришлось бы закрыть окно монитора и открыть его заново.
    const beforeEsc=await page.evaluate(()=>(window.__sent||[]).filter(x=>x.action==="close_indicators").length);
    await page.keyboard.press("Escape");
    await page.waitForTimeout(60);
    const afterEsc=await page.evaluate(()=>({
      hidden:document.getElementById("stomachWindow").hidden,
      display:getComputedStyle(document.getElementById("stomachWindow")).display,
      closes:(window.__sent||[]).filter(x=>x.action==="close_indicators").length
    }));
    check(afterEsc.hidden,"Escape не убрал всплывающее окно содержимого");
    // Скрытие проверяем и по ВЫЧИСЛЕННОМУ стилю: у окна display:flex, который
    // перебивает действие атрибута hidden, и без явного правила окно осталось бы
    // на экране при hidden=true.
    check(afterEsc.display==="none",
      "окно содержимого скрыто атрибутом, но осталось видимым (display="+afterEsc.display+")");
    check(afterEsc.closes===beforeEsc,
      "Escape закрыл весь монитор вместо всплывающего окна");

    // Крестик закрывает окно (а не «переключает» его).
    await page.click("#stomachContentsButton");
    await page.waitForTimeout(60);
    await page.click("#stomachWindowClose");
    await page.waitForTimeout(60);
    const afterCross=await page.evaluate(()=>document.getElementById("stomachWindow").hidden);
    check(afterCross,"крестик не закрыл окно содержимого");

    // ── Мерцание под курсором ───────────────────────────────────────────────
    //
    // Автор: «в окне монитора любая плашка с хувером мерцает, фликер».
    //
    // Причина не в оформлении: монитор пересобирал разметку на каждом живом
    // обновлении (четыре раза в секунду), и `innerHTML` заменял ВСЕ узлы. Новый
    // узел начинает переход оформления (`transition:background-color 50ms`) с
    // нуля, поэтому плашка под курсором «ходила» между наведённым и обычным
    // состоянием.
    //
    // Проверяем САМ УЗЕЛ, а не текст: подмена узла и есть мерцание, тогда как
    // сверка содержимого его бы пропустила. Таймеры порций меняются каждую
    // секунду, поэтому разметка на этих обновлениях МЕНЯЕТСЯ — то есть проверка
    // ловит именно то, что прежний кэш подписи не лечил.
    await push({type:"snapshot",snapshot:stomach,conditionRates:snapshot.conditionRates,
      digestion:stomach.digestion,daylight:{gameClockLabel:"12:00"},
      consumables:[{id:"water.bottle",name:"Вода",color:"#3a6ea5",volumeMilliliters:500,kilocalories:0,fits:true}],
      itemCatalog:[{id:"water.bottle",name:"Вода",color:"#3a6ea5"}],
      simulationRunning:true,simulationPaused:false});
    await page.waitForTimeout(80);

    await page.evaluate(()=>{
      window.__flickerPortion=document.querySelector(".stomachPortion");
      window.__flickerButton=document.getElementById("stomachContentsButton");
      window.__flickerCard=document.querySelector("[data-stomach]");
      window.__flickerChip=document.querySelector("button.indicatorChip");
    });

    for(let tick=0;tick<8;tick++){
      await push({type:"live_state",player:snapshot.player,playerVitals:snapshot.playerVitals,
        conditions:snapshot.conditions,conditionRates:snapshot.conditionRates,
        digestion:{...stomach.digestion,portions:stomach.digestion.portions.map((p,i)=>({
          ...p,remainingGameSeconds:p.remainingGameSeconds-60*(tick+1)}))},
        simulationRunning:true,simulationPaused:false});
      await page.waitForTimeout(60);
    }

    const flicker=await page.evaluate(()=>({
      portionKept:window.__flickerPortion===document.querySelector(".stomachPortion"),
      buttonKept:window.__flickerButton===document.getElementById("stomachContentsButton"),
      cardKept:window.__flickerCard===document.querySelector("[data-stomach]"),
      chipKept:window.__flickerChip===document.querySelector("button.indicatorChip")
    }));
    check(flicker.portionKept,
      "плитка порции подменена при живом обновлении: узел под курсором теряет "+
      ":hover и начинает переход оформления заново — это и есть мерцание плашек");
    check(flicker.buttonKept,
      "кнопка «Содержимое» подменена при живом обновлении — она мерцает под курсором");
    check(flicker.cardKept,"блок желудка пересоздан при живом обновлении");
    check(flicker.chipKept,"чип перка пересоздан при живом обновлении");

    // ── Под курсором не двигается НИ ОДИН узел ──────────────────────────────
    //
    // Прежние четыре проверки выше стерегут ПОДМЕНУ узла. Но узел можно и не
    // подменять, а ПЕРЕСТАВИТЬ — вставкой в другое место: элемент на мгновение
    // покидает документ, `:hover` с него слетает, и плашка мигает. Ровно это
    // автор и увидел повторно: «запущенная симуляция всё равно вызывает фликер
    // плашек предметов в блоках шкал».
    //
    // Считаются ОПЕРАЦИИ НАД ДОКУМЕНТОМ, а не содержимое: монитор живёт на
    // живых обновлениях, и в устоявшемся состоянии ни вставок, ни удалений быть
    // не должно вовсе — ни одного.
    const churn = await page.evaluate(async payload => {
      const counters = { inserts: 0, removes: 0, styleWrites: 0 };
      const insertBefore = Node.prototype.insertBefore;
      const appendChild = Node.prototype.appendChild;
      const removeChild = Node.prototype.removeChild;
      const setAttribute = Element.prototype.setAttribute;

      Node.prototype.insertBefore = function (node, reference) {
        if (node.parentNode) counters.inserts++;
        return insertBefore.call(this, node, reference);
      };
      Node.prototype.appendChild = function (node) {
        if (node.parentNode) counters.inserts++;
        return appendChild.call(this, node);
      };
      Node.prototype.removeChild = function (node) {
        counters.removes++;
        return removeChild.call(this, node);
      };
      // Замена объявления `style` ЦЕЛИКОМ — отдельный сторож: координаты и размер
      // плитки ставит укладчик УЖЕ ПОСЛЕ вставки разметки, и такая замена их
      // выбрасывает. Плитка на мгновение теряет место, а укладчик следом читает
      // `clientWidth` области — то есть браузер пересчитывает вёрстку синхронно,
      // четыре раза в секунду. Именно это делало прокрутку подтормаживающей.
      Element.prototype.setAttribute = function (name, value) {
        if (String(name).toLowerCase() === "style") counters.styleWrites++;
        return setAttribute.call(this, name, value);
      };

      const tile = document.querySelector(".stomachPortion");
      counters.widthBefore = tile ? Math.round(tile.getBoundingClientRect().width) : 0;
      counters.inlineBefore = tile ? tile.style.width : "";

      const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
      for (let tick = 0; tick < 6; tick++) {
        window.chrome.webview.listeners.get("message").forEach(fn => fn({ data: JSON.stringify({
          type: "live_state", player: payload.player, playerVitals: payload.playerVitals,
          conditions: payload.conditions, conditionRates: payload.conditionRates,
          digestion: { ...payload.digestion, portions: payload.digestion.portions.map(portion => ({
            ...portion, remainingGameSeconds: portion.remainingGameSeconds - 60 * (tick + 1)
          })) },
          simulationRunning: true, simulationPaused: false
        }) }));
        await wait(25);
      }

      Node.prototype.insertBefore = insertBefore;
      Node.prototype.appendChild = appendChild;
      Node.prototype.removeChild = removeChild;
      Element.prototype.setAttribute = setAttribute;

      const after = document.querySelector(".stomachPortion");
      counters.widthAfter = after ? Math.round(after.getBoundingClientRect().width) : 0;
      counters.inlineAfter = after ? after.style.width : "";
      return counters;
    }, {
      player: snapshot.player, playerVitals: snapshot.playerVitals,
      conditions: snapshot.conditions, conditionRates: snapshot.conditionRates,
      digestion: stomach.digestion
    });
    check(churn.inserts === 0 && churn.removes === 0,
      "живое обновление монитора ДВИГАЕТ узлы (вставок " + churn.inserts +
      ", удалений " + churn.removes + "): узел под курсором на мгновение покидает " +
      "документ, теряет :hover и начинает переход оформления заново — плашка мерцает");
    check(churn.styleWrites === 0,
      "живое обновление переписывает объявление style ЦЕЛИКОМ (" + churn.styleWrites +
      " раз): координаты и размер плитки, которые ставит укладчик, выбрасываются, " +
      "плитка теряет место, а синхронный пересчёт вёрстки подторамаживает прокрутку");
    // Прямая проверка того же самого по последствию: место плитки обязано
    // пережить обновления, а не быть выставленным заново.
    check(churn.inlineBefore.length > 0 && churn.inlineAfter === churn.inlineBefore,
      "плитка потеряла свои координаты при живом обновлении: до «" +
      churn.inlineBefore + "», после «" + churn.inlineAfter + "»");
    check(churn.widthAfter === churn.widthBefore && churn.widthBefore > 2,
      "ширина плитки изменилась от живого обновления: до " + churn.widthBefore +
      ", после " + churn.widthAfter);
  }

  // ── Контраст подписи на плитке желудка ────────────────────────────────────
  //
  // Автор: «проверить контраст текста в плашках содержимого желудка. Плохо
  // читается, плашки светлые, текст белый».
  //
  // Заливка плитки берётся из каталога предметов, а подпись прежде была всегда
  // белой: на светлых заливках (молоко #e8e0ca, витамин C #f0c94b, записка
  // #e7d58a) белый текст почти не читается. Цвет подписи обязан выбираться ПО
  // ЯРКОСТИ заливки, и проверяется это ЗАМЕРОМ контраста по WCAG, а не
  // сравнением строки: порог 4,5:1 — общепринятый минимум для обычного текста.
  {
    const lightItems={...snapshot,digestion:{...stomach.digestion,portions:[
      {itemId:"food.milk",volumeMilliliters:200,kilocalories:120,waterMilliliters:180,remainingGameSeconds:900},
      {itemId:"vitamin.c",volumeMilliliters:200,kilocalories:0,waterMilliliters:0,remainingGameSeconds:900},
      {itemId:"water.bottle",volumeMilliliters:200,kilocalories:0,waterMilliliters:200,remainingGameSeconds:900},
      {itemId:"drink.coffee",volumeMilliliters:200,kilocalories:50,waterMilliliters:190,remainingGameSeconds:900}
    ]}};
    await push({type:"snapshot",snapshot:lightItems,conditionRates:snapshot.conditionRates,
      digestion:lightItems.digestion,daylight:{gameClockLabel:"12:00"},
      // Молоко и витамин — СВЕТЛЫЕ заливки каталога (#e8e0ca, #f0c94b): именно на
      // них белая подпись и была нечитаемой. Кофе — тёмная заливка, на ней
      // обязан остаться белый текст.
      itemCatalog:[
        {id:"food.milk",name:"Молоко",color:"#e8e0ca"},
        {id:"vitamin.c",name:"Витамин C",color:"#f0c94b"},
        {id:"water.bottle",name:"Вода",color:"#4b8fe8"},
        {id:"drink.coffee",name:"Кофе",color:"#76533d"}
      ],
      consumables:[],simulationRunning:true,simulationPaused:false});
    await page.waitForTimeout(80);

    const inks=await page.evaluate(()=>{
      const luminance=(r,g,b)=>{
        const channel=v=>{const c=v/255;return c<=0.03928?c/12.92:Math.pow((c+0.055)/1.055,2.4)};
        return 0.2126*channel(r)+0.7152*channel(g)+0.0722*channel(b);
      };
      const ratio=(first,second)=>{
        const a=luminance(first[0],first[1],first[2]), b=luminance(second[0],second[1],second[2]);
        return (Math.max(a,b)+0.05)/(Math.min(a,b)+0.05);
      };
      const parse=text=>{
        const match=/rgb\((\d+),\s*(\d+),\s*(\d+)\)/.exec(text||"");
        return match?[Number(match[1]),Number(match[2]),Number(match[3])]:null;
      };
      const name=node=>{
        const card=node.closest("[data-stomach]");
        return node.getAttribute("data-stomach-item");
      };
      return [...document.querySelectorAll(".stomachPortion")].map(node=>{
        const style=getComputedStyle(node);
        const background=parse(style.backgroundColor);
        const ink=parse(style.color);
        const label=node.querySelector(".stomachPortionName");
        return {
          item:name(node),
          background:style.backgroundColor,
          ink:style.color,
          contrast:background&&ink?ratio(background,ink):0,
          text:label?label.textContent.trim():""
        };
      });
    });

    check(inks.length>=4,"плитки желудка не отрисованы: "+inks.length);
    for(const tile of inks){
      check(tile.contrast>=4.5,
        "подпись «"+tile.text+"» не читается на заливке: контраст "+
        tile.contrast.toFixed(2)+":1 (нужно не меньше 4,5:1), заливка "+
        tile.background+", текст "+tile.ink);
    }
    // Светлая заливка обязана получить ТЁМНУЮ подпись, тёмная — светлую. Это
    // сторож выбора по яркости: белый цвет на всех плитках тоже прошёл бы
    // проверку контраста на тёмной заливке воды и кофе.
    const inkLuminance=text=>{
      const match=/rgb\((\d+),\s*(\d+),\s*(\d+)\)/.exec(text||"");
      if(!match) return -1;
      const channel=value=>{const c=value/255;return c<=0.03928?c/12.92:Math.pow((c+0.055)/1.055,2.4)};
      return 0.2126*channel(Number(match[1]))+0.7152*channel(Number(match[2]))+0.0722*channel(Number(match[3]));
    };
    const milk=inks.find(tile=>tile.item==="food.milk");
    check(milk&&inkLuminance(milk.ink)<0.2,
      "на светлой заливке молока подпись осталась светлой: "+(milk?milk.ink:"нет плитки"));
    const coffee=inks.find(tile=>tile.item==="drink.coffee");
    check(coffee&&coffee.ink==="rgb(255, 255, 255)",
      "на тёмной заливке кофе подпись не белая: "+(coffee?coffee.ink:"нет плитки"));

    // ── Сокращения в мониторе ───────────────────────────────────────────────
    //
    // Автор: «в окне монитора не пишем нигде "игровые"/"игровых" — сокращаем до
    // "игр.", никогда не пишем слово "минуты" целиком, только "мин"».
    //
    // Проверяется ВЕСЬ видимый текст монитора — и содержимое, и КАЖДАЯ подсказка
    // (`title`, `data-game-tooltip`): подсказка показывается по наведению, то есть
    // это такой же текст окна, и «нигде» относится к ней ровно так же.
    const wording = await page.evaluate(() => {
      const body = document.querySelector(".indicatorsBody");
      const parts = [body.textContent];
      body.querySelectorAll("[title],[data-game-tooltip]").forEach(node => {
        parts.push(node.getAttribute("title") || "");
        parts.push(node.getAttribute("data-game-tooltip") || "");
      });
      const card = document.querySelector("[data-stomach]");
      if (card) {
        card.querySelectorAll("[title],[data-game-tooltip]").forEach(node => {
          parts.push(node.getAttribute("title") || "");
          parts.push(node.getAttribute("data-game-tooltip") || "");
        });
      }
      return parts.join(" \u0001 ");
    });

    // Проверка слов идёт по тексту БЕЗ сокращений «игр. мин»: иначе «игр.» само
    // попалось бы под шаблон «игров» на букву «о».
    const plain = wording.replace(/игр\.\s*мин/g, " ");
    const fullGame = plain.match(/игров\S*/i);
    check(!fullGame,
      "в мониторе осталось полное слово «игровые»/«игровых»: " +
      (fullGame ? fullGame[0] : ""));
    const fullMinutes = plain.match(/минут[аыеу]?(?!\s*\.)/i);
    check(!fullMinutes,
      "в мониторе осталось полное слово «минуты»/«минуту»: " +
      (fullMinutes ? fullMinutes[0] : ""));
    check(/игр\.\s*мин/.test(wording),
      "в мониторе нет сокращения «игр. мин» — проверка сокращений ничего не значит");
  }

  // --- Признак маршрута переживает ПОЛНЫЙ снимок ---
  //
  // Хост кладёт признак маршрута РЯДОМ со снимком (message.route), а сам
  // snapshot — это SimulatorSnapshot, где поля route нет вовсе. Монитор
  // запоминал признак только из живого обновления, поэтому каждый полный снимок
  // возвращал «Покой»: усталость мигала «покой ↔ нагрузка с ночным временем»
  // четыре раза в секунду. Здесь полный снимок идёт с ВКЛЮЧЁННЫМ маршрутом, и
  // строка усталости обязана остаться на нагрузке.

  await push({
    type:"snapshot",snapshot,conditionRates:snapshot.conditionRates,
    route:{enabled:true},daylight:{gameClockLabel:"22:00"},
    simulationRunning:true,simulationPaused:false
  });
  await page.waitForTimeout(60);

  const fatigueRow=await page.$eval("[data-scale-key='fatigue']",n=>n.textContent.replace(/\s+/g," "));
  check(fatigueRow.includes("Нормальная нагрузка"),
    "после ПОЛНОГО снимка с включённым маршрутом усталость показывает «Покой» — "+
    "признак маршрута потерян, строка мигает: "+fatigueRow.slice(0,160));
  check(fatigueRow.includes("Ночное время"),
    "ночной коэффициент усталости не показан при включённом маршруте: "+
    fatigueRow.slice(0,160));

  // --- Каталог желудка обязан работать БЕЗ инвентаря ---
  //
  // Автор: «при нажатии на предмет в меню-списке (ПКМ в желудке) предмет не
  // добавляется в желудок». Причина была в проводке Хоста, а не в разметке:
  // выбор пункта шёл через инвентарный путь (`UseInventoryItemCore`), а тот первым
  // делом требует предмет В ИНВЕНТАРЕ. У предмета из каталога его там нет — и
  // обработчик МОЛЧА выходил. Снаружи это и выглядело как «клик ничего не делает»:
  // меню закрывалось, желудок оставался прежним.
  //
  // Проверка статичная, по исходникам Хоста. Смоук страницы её не поймал бы: там
  // фикстура подаёт `consumables` напрямую, а решение «что делать» принимает
  // C#. Автор задал это действие как замену трёх шагов («поиск предмета, выдача в
  // инвентарь и нажатие "использовать"»), поэтому инвентарь здесь не участвует
  // вовсе, и связь «меню → не инвентарный путь» — часть требования, а не деталь.
  {
    const simulatorForm=fs.readFileSync(
      path.join(root,"src","AssistQuestEditor.App","Host","SimulatorForm.cs"),"utf8");
    const indicatorsForm=fs.readFileSync(
      path.join(root,"src","AssistQuestEditor.App","Host","IndicatorsForm.cs"),"utf8");

    check(/case "use_stomach_item":/.test(indicatorsForm) &&
          /StomachActionRequested\?\.Invoke/.test(indicatorsForm),
      "окно монитора не передаёт use_stomach_item наружу: выбор пункта меню желудка " +
      "останется без обработчика");

    // Ветка «use» в обработчике желудка и метод, который она зовёт.
    //
    // Ищем именно ВЫЗОВ с `e.ItemId!`: рядом стоит проверка
    // `string.IsNullOrWhiteSpace(e.ItemId)`, и нестрогий шаблон поймал бы её
    // (проверено — поймал), отчего guard проверял бы не тот метод.
    const handler=/private void IndicatorsForm_StomachActionRequested[\s\S]*?\n    }/
      .exec(simulatorForm);
    check(!!handler,"в Симуляторе не найден обработчик StomachActionRequested");
    const useCall=handler?/case "use":[\s\S]*?(\w+)\(e\.ItemId!\)/.exec(handler[0]):null;
    check(!!useCall,"в обработчике StomachActionRequested нет ветки «use»");
    const useMethod=useCall?useCall[1]:"";

    check(useMethod.length>0&&!/Inventor/i.test(useMethod),
      "употребление из каталога желудка идёт через инвентарный путь («"+useMethod+"»): " +
      "у предмета, которого нет в инвентаре, обработчик молча выйдет, и клик по " +
      "пункту меню ничего не сделает");

    const body=useMethod
      ?new RegExp("private (?:static )?[\\w<>?]+ "+useMethod+"\\(string itemId\\)[\\s\\S]*?\\n    }")
        .exec(simulatorForm)
      :null;
    check(!!body,"метод «"+useMethod+"» не найден в Симуляторе");
    check(body&&!/Get<InventoryState>/.test(body[0]),
      "метод «"+useMethod+"» читает инвентарь: каталог желудка обязан работать " +
      "БЕЗ инвентаря — ни наличия, ни количества, ни списания");
    // Отклик обязателен: молчание на выборе пункта и было дефектом.
    check(body&&/RequestSnapshot\(/.test(body[0]),
      "метод «"+useMethod+"» не обновляет снимок: желудок остался бы прежним на " +
      "глазах у автора, как при молчаливом выходе");
  }

  // --- Объём приходит из домена миллилитрами, признак «влезет» — тоже ---
  //
  // Автор: «объём желудка отображать в миллилитрах. Почему-то указанный объём в
  // списке не соответствует добавленному» и «если съедобный предмет по объёму
  // больше, чем свободное место желудка, предмет употребить нельзя».
  //
  // Обе величины обязаны считаться в ДОМЕНЕ и приходить готовыми: страница не
  // знает ни пищевых профилей, ни массы порций, и её собственная формула
  // разошлась бы с начислением при первой правке баланса.
  {
    const simulatorForm=fs.readFileSync(
      path.join(root,"src","AssistQuestEditor.App","Host","SimulatorForm.cs"),"utf8");
    const digestion=fs.readFileSync(
      path.join(root,"src","AssistQuestEditor.Domain","CharacterDigestion.cs"),"utf8");
    const engine=fs.readFileSync(
      path.join(root,"src","AssistQuestEditor.Domain","CharacterVitalsEngine.cs"),"utf8");
    const models=fs.readFileSync(
      path.join(root,"src","AssistQuestEditor.Domain","DomainModels.cs"),"utf8");
    const indicators=fs.readFileSync(
      path.join(root,"src","AssistQuestEditor.App","Web","indicators.js"),"utf8");
    // Файл НАСТРОЕК читается статически: галочка «%/время» обязана быть в нём
    // полем, а не в localStorage страницы (см. проверку в конце блока).
    const preferences=fs.readFileSync(
      path.join(root,"src","AssistQuestEditor.App","Core","AppUiPreferencesStore.cs"),"utf8");

    check(/occupiedMilliliters/.test(simulatorForm)&&/totalMilliliters/.test(simulatorForm),
      "Хост не присылает занятый и полный объём пищеварения в миллилитрах: подпись " +
      "«занято / всего» осталась бы в литрах, а дробные объёмы округлялись бы до нуля");
    check(/volumeMilliliters\s*=/.test(simulatorForm),
      "Хост не присылает объём порции в миллилитрах: плитка не смогла бы отвечать " +
      "объёму, и площадь области разошлась бы с числом «занято»");
    check(/fits\s*=\s*CharacterDigestionReport\.Fits\(/.test(simulatorForm),
      "Хост не считает признак «влезет» доменом: меню серым гасило бы не то, " +
      "что фактически примет начисление");
    check(/StomachCapacityMilliliters/.test(simulatorForm),
      "Хост не берёт вместимость пищеварения из домена: вторая такая же константа " +
      "в Хосте разошлась бы с пищеварением");
    // Пропускная способность обязана приходить из ДОМЕНА: страница не знает ни
    // литров в час, ни коэффициента воды, и её собственная формула разошлась бы
    // с начислением при первой правке физиологии.
    check(/dryThroughputMillilitersPerHour/.test(simulatorForm),
      "Хост не присылает пропускную способность пищеварения: подпись о лимите " +
      "не сходилась бы с фактическими сроками порций");
    // Правило «целиком или никак» живёт в Begin: без него в порцию записывалась бы
    // урезанная доля, и «110 мл банана» снова стало бы «20 мл».
    check(/if \(mass > free \+ 1e-9d\)/.test(digestion),
      "пищеварение снова принимает порцию ЧАСТЬЮ: тогда в порции окажется не объём " +
      "предмета, а остаток вместимости — «110 мл банана» снова станет «20 мл»");
    // Ставка порции — ОБЩАЯ пропускная способность, поделённая по «весу» порций.
    // Это ядро новой модели: без него вернулся бы «свой таймер у каждой порции»,
    // и смесь продуктов перестала бы замедлять каждую из них.
    check(/throughput\s*\*\s*mass\s*\*\s*lightness\s*\/\s*massTotal/.test(digestion),
      "ставка порции не считается из ОБЩЕЙ пропускной способности: смесь продуктов " +
      "перестала бы замедлять каждую порцию, а вода — обгонять сухую еду");
    check(/WaterContentSpeedFactor/.test(digestion),
      "исчез коэффициент «лёгкости» воды: скорость усвоения перестала бы зависеть " +
      "от содержания жидкости в порции");
    // МЕТАБОЛИЗМ ПИЩЕВАРЕНИЯ — ЧИСЛО, А НЕ ФЛАГ. С флагами игрок мог поесть при
    // высоком метаболизме и получить ставку ×2,5 навсегда — эксплоит, который
    // автор назвал прямо: «метаболизм будет на нуле, а пища отработает по норме».
    check(/double metabolismPercent\)/.test(digestion) &&
      /MetabolismPercent/.test(digestion),
      "пищеварение снова принимает метаболизм ПРИЗНАКОМ: тогда поесть заранее при " +
      "высоком метаболизме снова даст ставку ×2,5 на всё переваривание");
    check(/double\.IsFinite\(ExhaustionRollPending\)/.test(models),
      "в состоянии нет накопителя дробных порций броска устойчивости: шанс «не " +
      "получить порцию истощения» стал бы зависеть от размера шага симуляции");
    // СКОРОСТИ УСТОЙЧИВОСТИ И МЕТАБОЛИЗМА — В «% В ЧАС». Смесь размерностей
    // («за четверть часа» плюс «за минуту») показывала рост там, где шкала падает.
    check(/QuarterHoursPerHour/.test(engine) && /TenMinuteSpansPerHour/.test(engine),
      "движок снова смешивает размерности скоростей устойчивости и метаболизма: " +
      "подсказка покажет рост шкалы там, где она на самом деле падает");
    check(/ElevatedBonusShare/.test(engine),
      "надбавка за свойство выше номинала снова включается ФЛАГОМ: 61% и 100% " +
      "давали бы одно и то же, хотя автор просил пропорциональность");
    // ПРОПОРЦИОНАЛЬНОСТЬ НАДБАВКИ В МОНИТОРЕ: если страница снова зашьёт «>60»,
    // строка обещала бы полный +1% уже с 61%.
    check(/elevatedBonusShare/.test(indicators),
      "монитор не считает ДОЛЮ надбавки: строка обещала бы полный размер уже при " +
      "61%, тогда как движок начисляет пропорционально");
    // Проверяем именно КОД, а не комментарий: в пояснении к функции нарочно
    // оставлена запись «было `metabolismPercent()>60`».
    const indicatorsCode=indicators.replace(/\/\*[\s\S]*?\*\//g,"")
      .replace(/^\s*\/\/.*$/gm,"");
    check(!/metabolismPercent\(\)>60/.test(indicatorsCode) &&
      !/resiliencePercent\(\)>60/.test(indicatorsCode),
      "монитор снова зашивает номинал 60 ДВУМЯ литералами: правка калибровки до " +
      "этих строк не доедет, хотя 45 и 75 уже приходят полями");
    check(/exhaustionResilienceDivisor/.test(indicators) &&
      /exhaustionResilienceDivisor/.test(simulatorForm),
      "шанс устойчивости не показывается игроку: это её сильнейший эффект, и он " +
      "обязан быть виден строкой монитора");
    // ГАЛОЧКА «%/время» — настройка ХОСТА, а не страницы. Если её состояние
    // начнёт жить в localStorage, оно снова сломается при смене профиля WebView2:
    // профиль собирается по отпечатку сборки, а настройки переживают обновление
    // версии именно потому, что лежат в файле.
    check(/percentUnits/.test(simulatorForm) && /AppUiPreferencesStore/.test(simulatorForm),
      "галочка «%/время» больше не сохраняется в настройках: она обязана переживать " +
      "перезапуск и смену профиля WebView2, как раскрытые разделы сайдбара");
    check(/PercentUnits/.test(preferences),
      "в пользовательских настройках нет поля PercentUnits: галочка не переживёт " +
      "перезапуск");
    check(!/localStorage[^\n]*percentUnits/.test(indicators),
      "состояние галочки хранится в localStorage страницы: он живёт в профиле " +
      "WebView2 и теряется при обновлении версии");
    // Узел галочки СТАТИЧНЫЙ: тело монитора перерисовывается раз в секунду, и
    // галочка внутри него теряла бы нажатие вместе со своей разметкой.
    check(/id="percentUnitsToggle"/.test(html),
      "в шапке монитора нет узла галочки: обработчик включения не к чему привязать");
    check(/percentUnitsToggle/.test(indicators) && !/percentUnitsToggle/.test(
      indicators.split("function init()")[1]?.split("function render()")[0] || ""),
      "галочка перерисовывается вместе с телом монитора: она теряла бы нажатие");
  }

  if(failures.length){console.error("Indicators smoke: FAIL");failures.forEach(x=>console.error("  ✗ "+x));process.exitCode=1;}
  else console.log("Indicators smoke: OK");
}finally{await closeBrowser(browser);}
