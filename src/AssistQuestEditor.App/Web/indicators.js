(function (global) {
  "use strict";
  var body = null, snapshot = null, itemCatalog = [], simulationRunning = false, simulationPaused = false;
  var highlightKey = "", highlightTimer = null, lastMarkup = "";
  var GOOD_DIRECTION = {health:1,energy:1,hydration:1,stress:-1,fatigue:-1,resilience:1,metabolism:1};
  var FLAT_RATE_EPSILON = 0.05;

  function v(){ return global.AssistVitals; }
  function num(value){ var n=Number(value); return Number.isFinite(n)?n:0; }
  function fmt(value,digits){
    var n=Number(value); if(!Number.isFinite(n)) return "0";
    var s=n.toFixed(digits==null?0:digits);
    return s.replace(/\.0+$/,"").replace(/(\.\d*?)0+$/,"$1");
  }
  function esc(value){ return v()?v().escapeHtml(value):String(value==null?"":value); }
  function conditions(){ return (snapshot&&snapshot.conditions)||{}; }
  function vitals(){ return (snapshot&&snapshot.playerVitals)||{}; }
  function character(){ return (snapshot&&snapshot.character)||{}; }
  function effects(){ return Array.isArray(conditions().effects)?conditions().effects:[]; }
  function activeEffects(){ return effects().filter(function(e){return num(e.remainingRealSeconds)>0;}); }
  function hasEffect(id){
    var wanted=String(id||"").toLowerCase();
    return activeEffects().some(function(e){return String(e.id||"").toLowerCase()===wanted;});
  }
  function totalStressPercent(){
    return Math.min(100,Math.round((num(conditions().stress)+num(conditions().cumulativeStress))/100));
  }
  function resiliencePercent(){ return Math.round(num(vitals().resilience)/100); }
  function metabolismPercent(){ return Math.round(num(vitals().metabolism)/100); }
  /**
   * Множитель расхода от метаболизма и «Быка» — ПРИСЛАН ДОМЕНОМ.
   *
   * Вторая такая же формула в Web разошлась бы с движком при первой правке
   * границ 45/75 или множителей 0,9/1,25: границы живут в калибровке, и
   * JavaScript их не повторяет. Старая версия страницы знала только «0,9/1,25»,
   * литералами, и строка расхода перестала совпадать с итогом, когда автор
   * поменял нормы в `CharacterVitalsTuning`.
   */
  function metabolismFactor(){
    if(snapshot&&snapshot.conditionRates&&snapshot.conditionRates.metabolismFactor!=null)
      return num(snapshot.conditionRates.metabolismFactor);
    return 1;
  }
  /** Порог «пониженного» и «повышенного» метаболизма — тоже у домена. */
  function reducedMetabolismPercent(){ return snapshot&&snapshot.conditionRates&&snapshot.conditionRates.reducedMetabolismPercent!=null?num(snapshot.conditionRates.reducedMetabolismPercent):45; }
  function elevatedMetabolismPercent(){ return snapshot&&snapshot.conditionRates&&snapshot.conditionRates.elevatedMetabolismPercent!=null?num(snapshot.conditionRates.elevatedMetabolismPercent):75; }
  /**
   * «В движении» = ВКЛЮЧЁННОЕ движение по маршруту, а не мгновенная скорость.
   *
   * Покой — это состояние, когда движение по маршруту ВЫКЛЮЧЕНО; только тогда
   * усталость восстанавливается. Скорость игрока может быть нулевой в кадре
   * между пакетами или на разгоне, но пока маршрут включён, игрок «едет».
   * Хост присылает признак маршрута вместе со снимком (`route.enabled`), и
   * монитор обязан читать тот же признак, что и домен: иначе подсказка
   * показывала бы покой во время движения по маршруту — ровно то, что автор
   * уже наблюдал как дефект.
   */
  function isMoving(){ return !!(snapshot&&snapshot.route&&snapshot.route.enabled); }
  /**
   * Признак «движение по маршруту включено» живёт РЯДОМ со снимком, а не внутри него.
   *
   * Хост кладёт `route` в сообщение (и `snapshot`, и `live_state`), а сам
   * `snapshot` — это `SimulatorSnapshot`, в котором поля `route` нет вовсе.
   * Поэтому признак нельзя запоминать только из live_state: каждый ПОЛНЫЙ
   * снимок его перезаписывал бы — точнее, оставлял бы прежний, полученный из
   * полного снимка ДО включения маршрута. Снаружи это выглядело ровно как
   * мерцание «покой ↔ нагрузка с ночным временем». Теперь признак берётся из
   * ЛЮБОГО сообщения, где он есть.
   */
  function applyRoute(message){
    var incoming=message.route;
    if(!incoming) return;
    if(!snapshot) snapshot={};
    var previous=snapshot.route||{};
    // Признак берём из сообщения, если он там есть; иначе оставляем прежний —
    // иначе короткий `route` вернул бы «покой» при включённом маршруте.
    var enabled=incoming.enabled===undefined?!!previous.enabled:!!incoming.enabled;
    if(previous.enabled===enabled) return;
    snapshot.route={enabled:enabled};
  }
  /**
   * Идёт ли прямо сейчас восстановление здоровья.
   *
   * Пока здоровье восстанавливается, усталость копится ВДВОЕ быстрее (задано
   * автором). Домен считает это по скорости регенерации, а та равна нулю при
   * полном здоровье или нехватке ресурсов (1 энергия и 2 жидкости за единицу
   * здоровья) — поэтому здесь проверяются ровно эти условия, и «×2» не висит на
   * игроке, у которого регенерации фактически нет.
   */
  function healthRegenerating(){
    var maxH=Math.max(1,num(vitals().maxHealth)||10000);
    return num(vitals().health)<maxH&&num(vitals().energy)>0&&num(vitals().hydration)>0;
  }
  function gameHour(){
    var d=snapshot&&snapshot.daylight, value=d&&(d.gameClockLabel||d.gameTimeLabel)||"";
    var m=String(value).match(/^(\d{1,2})[:.]/); return m?Number(m[1]):12;
  }
  function nightFactor(){
    var h=gameHour(); return h>=6&&h<18?1:h>=18&&h<22?1.5:2.5;
  }
  var EFFECT_SCALE_MAP={
    bull:["health","energy","hydration"], power_surge:["health","energy","hydration","fatigue","stress","resilience","metabolism"],
    burnout:["fatigue"], unkempt:["stress"], bum:["stress","resilience"], relaxation:["stress"], rested:["stress"],
    thirst:["hydration","stress","metabolism"], dehydration:["hydration","stress","resilience","metabolism"],
    drowsiness:["fatigue","resilience"], alcohol_aftereffect:["hydration","fatigue"], nicotine_rebound:["stress"],
    caffeine_overuse:["stress"], caffeine_excess:["stress"], caffeine_jitter:["stress"], late_caffeine:["stress","fatigue"],
    caffeine_withdrawal:["stress","resilience"], analgesia:["health"], analgesic_overuse:["health","stress"],
    strong_bones:["health"], sorbent:["health"]
  };
  var PERK_NAMES={
    "perk.dairy_habit":"Молочная привычка","perk.smoker":"Прокуренный","perk.discharged":"Уволен","perk.breadwinner":"Кормилец",
    "skill.mechanics":"Механик","skill.navigation":"Штурман","skill.medicine":"Фельдшер"
  };
  function scale(key){ return (v()&&v().SCALES||[]).find(function(s){return s.key===key;})||null; }
  /**
   * Описание шкалы для подсказки при наведении на её название.
   *
   * Берётся из AssistVitals, а не из локальной таблицы: те же строки показывает
   * сайдбар (data-game-tooltip в блоке состояния), и расхождение текстов
   * означало бы два разных объяснения одной механики.
   */
  function scaleDescription(s){
    if(!s) return "";
    return [s.desc,s.affects].filter(Boolean).join(" ");
  }
  function valuesFor(key){
    var s=scale(key); if(s&&v()&&v().read) return v().read(snapshot,s);
    var actual=num(vitals()[key]); return {actual:actual,maximum:10000,percent:actual/100,displayPercent:actual/100};
  }
  function rateFor(key){ return num(snapshot&&snapshot.conditionRates&&snapshot.conditionRates[key]); }
  /**
   * Физическое имя единицы шкалы: «ккал» для энергии, «мл» для жидкости.
   *
   * У этих двух шкал скорость показывается в тех же величинах, что и сама шкала
   * (−13,9 ккал/мин вместо −41,7 ед./мин): игроку эта цифра сопоставима с
   * этикеткой еды. У остальных шкал размерности нет — остаётся «ед.».
   */
  function rateUnit(key){
    var s=scale(key);
    return s&&s.physicalUnit?s.physicalUnit:"ед.";
  }
  /**
   * Сколько ЕДИНИЦ хранения в одной физической единице шкалы.
   *
   * Берётся из ОТНОШЕНИЯ потолков, которые прислал домен (10000 единиц = 5000
   * ккал → 2; 10000 единиц = 3000 мл → 3,33), а не из второй пары констант:
   * иначе при смене размера шкалы размерности разъехались бы.
   */
  function physicalFactor(key){
    var values=valuesFor(key);
    if(values.maxPhysical==null||!values.maximum) return 1;
    return values.maximum/values.maxPhysical;
  }
  function contribution(rate,key,semanticOverride){
    var values=valuesFor(key), unitsPerHour=rate*60, unit=rateUnit(key);
    var percentPerHour=values.maximum>0?unitsPerHour/values.maximum*100:0;
    var good=semanticOverride==null?(GOOD_DIRECTION[key]||0):semanticOverride;
    var useful=Math.abs(rate)<FLAT_RATE_EPSILON?null:good!==0&&((rate>0)===(good>0));
    var physicalPerMinute=unit==="ед."?rate:rate/physicalFactor(key);
    return {unitsPerHour:unitsPerHour,percentPerHour:percentPerHour,useful:useful,physicalPerMinute:physicalPerMinute,unit:unit};
  }
  function contributionHtml(rate,key,semanticOverride){
    var c=contribution(rate,key,semanticOverride);
    var cls=Math.abs(rate)<FLAT_RATE_EPSILON?"factorContribution factorNeutral":c.useful?"factorContribution factorGood":"factorContribution factorBad";
    // Главное число — в единицах шкалы (ккал/мл, если они есть), рядом в скобках
    // остаётся скорость в единицах хранения и в процентах за час.
    return "<strong class='"+cls+"'>"+(rate>0?"+":"")+fmt(c.physicalPerMinute,1)+" "+c.unit+"/мин ("+
      (c.unitsPerHour>0?"+":"")+fmt(c.unitsPerHour,1)+" ед./час; "+
      (c.percentPerHour>0?"+":"")+fmt(c.percentPerHour,1)+"%/ч)</strong>";
  }
  function factor(text,key,rate,kind,active,semanticOverride){
    return {text:text,key:key,rate:Number(rate)||0,kind:kind,active:!!active,semanticOverride:semanticOverride};
  }
  function activePerkRows(key){
    var result=[],seen={};
    activeEffects().forEach(function(effect){
      var id=String(effect.id||"").toLowerCase();
      if(!((EFFECT_SCALE_MAP[id]||[]).includes(key))) return;
      seen[id]=true;
      result.push({id:effect.id,name:effect.name||effect.id,kind:effect.isDebuff?"debuff":"buff"});
    });
    var ch=character(), ids=[];
    (Array.isArray(ch.buffs)?ch.buffs:[]).forEach(function(id){ids.push({id:id,kind:"buff"});});
    (Array.isArray(ch.debuffs)?ch.debuffs:[]).forEach(function(id){ids.push({id:id,kind:"debuff"});});
    ids.forEach(function(entry){
      var id=String(entry.id||"").toLowerCase();
      if(seen[id]||!((EFFECT_SCALE_MAP[id]||[]).includes(key))) return;
      seen[id]=true;
      result.push({id:entry.id,name:PERK_NAMES[id]||entry.id,kind:entry.kind});
    });
    (Array.isArray(ch.skills)?ch.skills:[]).forEach(function(skill){
      if(num(skill.level)<=0) return;
      var id=String(skill.id||"").toLowerCase();
      var affects=(id==="skill.medicine"&&key==="health")||(id==="skill.navigation"&&key==="energy")||(id==="skill.mechanics"&&key==="fatigue");
      if(affects) result.push({id:skill.id,name:skill.name||PERK_NAMES[id]||skill.id,kind:"buff"});
    });
    return result;
  }
  /**
   * Предметы, которые ПРЯМО СЕЙЧАС усваиваются в эту шкалу.
   *
   * Автор: «в пунктах шкалы нужно отображать все усваивающиеся предметы, которые
   * влияют на шкалу». Раньше строка была ОДНА и бралась из
   * `conditions.lastConsumedItemId` — то есть называла ПОСЛЕДНИЙ съеденный
   * предмет, независимо от того, даёт ли он эту шкалу и лежит ли он ещё в
   * желудке. Из-за этого выпитый кофе подписывал восстановление воды, а вода
   * «исчезала» из строки, стоив съесть после неё апельсин.
   *
   * Теперь источник — СПИСОК ПОРЦИЙ желудка: у каждой есть свой вклад по каждой
   * шкале, и предмет попадает в строку ровно тогда, когда его вклад есть.
   * `lastConsumedItemId` здесь не участвует вовсе.
   *
   * Ставка берётся У ПОРЦИИ (приходит из домена как energyPerMinute /
   * hydrationPerMinute), а не из общей суммы желудка: сумма описывает все порции
   * сразу, и «+9000 ед./час» про каждую было бы неправдой.
   */
  function digestedItemsFor(key){
    if(key!=="energy"&&key!=="hydration") return [];
    var portions=stomachPortions();
    var result=[];
    portions.forEach(function(portion){
      var rate=num(key==="energy"?portion.energyPerMinute:portion.hydrationPerMinute);
      var remaining=num(key==="energy"?portion.kilocalories:portion.waterMilliliters);
      // Ноль ставки означает «эта порция данную шкалу НЕ восполняет» — именно
      // так вода не попадает в энергию, а апельсин попадает в обе.
      if(rate<=0||remaining<=0) return;
      var id=String(portion.itemId||"");
      // Название подставляется ЗДЕСЬ, а не берётся готовым: порция приходит из
      // домена с одним Id, а имя и цвет живут в каталоге предметов. Без подстановки
      // чип рисовался бы ПУСТЫМ (кнопка без подписи) — так это и всплыло.
      result.push({id:id,name:itemName(id),rate:rate});
    });
    return result;
  }
  function consumedItemFor(key){
    // Оставлено для совместимости с внешними вызовами; строку рисует
    // digestedItemsFor, потому что предметов может быть НЕСКОЛЬКО.
    var items=digestedItemsFor(key);
    if(!items.length) return null;
    var first=items[0];
    var found=(itemCatalog||[]).find(function(e){return String(e.id||"").toLowerCase()===first.id.toLowerCase();});
    return {id:first.id,name:(found&&(found.name||found.id))||first.id,rate:first.rate};
  }
  function perkContribution(key,id){
    var rate=rateFor(key), value=0, wanted=String(id||"").toLowerCase();

    if(wanted==="relaxation"&&key==="stress") value=-Math.abs(rate)*0.15;
    else if(wanted==="rested"&&key==="stress") value=-Math.abs(rate)*0.25;
    else if(wanted==="unkempt"&&key==="stress") value=Math.abs(rate)*0.50;
    else if(wanted==="bum"&&key==="stress") value=Math.abs(rate)*0.60;
    else if(wanted==="burnout"&&key==="fatigue") value=Math.abs(rate)*0.10;
    // «Жажда» и «Обезвоживание»: вклад показан ТЕМ ЖЕ числом, что начисляет
    // домен. Жажда даёт аддитивные +0,5 %/мин к стрессу (то есть 0,5% шкалы =
    // 50 ед./мин при шкале 10000), обезвоживание множит скорость стресса на
    // 1,25 и отнимает метаболизм 2% за 10 минут (2% = 200 ед. за 10 мин =
    // 200/10/60 ед./мин).
    else if(wanted==="thirst"&&key==="stress") value=50;
    else if(wanted==="thirst"&&key==="metabolism") value=-200/60;
    else if(wanted==="dehydration"&&key==="stress") value=Math.abs(rate)*0.25;
    else if(wanted==="dehydration"&&key==="metabolism") value=-200/60;
    else if(wanted==="dehydration"&&key==="resilience") value=-Math.abs(rateFor("hydration"));
    else if(wanted==="drowsiness"&&key==="fatigue") value=Math.abs(rate)*0.10;
    else if(wanted==="alcohol_aftereffect"&&(key==="hydration"||key==="fatigue")) value=-Math.abs(rate)*0.10;
    else if(wanted==="nicotine_rebound"&&key==="stress") value=Math.abs(rate)*0.25;
    else if(wanted==="caffeine_overuse"&&key==="stress") value=Math.abs(rate)*0.20;
    else if(wanted==="caffeine_excess"&&key==="stress") value=Math.abs(rate)*0.15;
    else if(wanted==="caffeine_jitter"&&key==="stress") value=Math.abs(rate)*0.20;
    else if(wanted==="caffeine_withdrawal"&&key==="stress") value=Math.abs(rate)*0.20;
    else if(wanted==="late_caffeine"&&key==="fatigue") value=Math.abs(rate)*0.15;
    else if(wanted==="analgesic_overuse"&&(key==="health"||key==="stress")) value=-Math.abs(rate)*0.10;
    else if(wanted==="bull"&&(key==="health"||key==="energy"||key==="hydration")) value=Math.abs(rate)*0.25;
    else if(wanted==="power_surge") value=0;

    return value;
  }

  function chipHtml(entry,isItem,key){
    if(isItem)
      return "<button type='button' class='indicatorChip itemChip' data-item-link='"+esc(entry.id)+"' title='Открыть окно «Предметы»'>"+esc(entry.name)+"</button>";

    var rate=perkContribution(key,entry.id);
    return "<button type='button' class='indicatorChip "+(entry.kind==="debuff"?"debuffChip":"buffChip")+"' data-perk-link='"+esc(entry.id)+"' data-perk-kind='"+esc(entry.kind)+"' title='Открыть окно «Перки, баффы, скиллы»'>"+esc(entry.name)+"</button>" +
      " <span class='chipContribution'>"+contributionHtml(rate,key)+"</span>";
  }

  function dynamicChipRows(key){
    var html="";
    activePerkRows(key).forEach(function(entry){
      html+="<div class='indicatorFact dynamicItem'>• "+chipHtml(entry,false,key)+"</div>";
    });
    var items=digestedItemsFor(key);
    items.forEach(function(item){
      html+="<div class='indicatorFact dynamicItem'>• "+chipHtml(item,true,key)+" <span class='indicatorItemMeta'>усваивается "+contributionHtml(item.rate,key)+"</span></div>";
    });
    return html;
  }
  function dynamicFactors(key){
    var rate=rateFor(key), result=[];
    // «Пока здоровье восстанавливается, получаемая усталость удваивается».
    // Фактор ДИНАМИЧЕСКИЙ: он появляется только пока идёт регенерация здоровья.
    // Вклад показан на БАЗОВОЙ нагрузке (100/18/60), а не на итоговой скорости:
    // итог уже включает само удвоение, и доля от него читалась бы как «половина»,
    // хотя игроку обещано ровно ×2.
    if(key==="fatigue"&&isMoving()&&healthRegenerating())
      result.push(factor("Восстановление здоровья: усталость копится ×2",key,100/18/60,"dynamic",true,1));
    if(key==="fatigue"&&isMoving()&&nightFactor()!==1)
      result.push(factor("Ночное время: коэффициент ×"+fmt(nightFactor(),1),key,rate*(1-1/nightFactor()),"dynamic",true));
    if(key==="fatigue"&&hasEffect("burnout"))
      result.push(factor("Выгорание: усталость копится ×1,10",key,rate*0.1/1.1,"dynamic",true));
    if(key==="health"){
      var exhaustionLoss=-((num(conditions().cumulativeEnergy)+num(conditions().cumulativeHydration))/100)*100/6*4/60;
      if(Math.abs(exhaustionLoss)>FLAT_RATE_EPSILON) result.push(factor("Истощение энергии и жидкости повреждает здоровье",key,exhaustionLoss,"dynamic",true));
    }
    return result;
  }
  function permanentFactors(key){
    var rate=rateFor(key), result=[];
    if(key==="health"){
      // Постоянные составляющие восстановления здоровья заданы автором в
      // процентах за 10 игровых минут: база 1%, каждое свойство выше нормы +1%,
      // «Бык» +2%. Скорость шкалы в единицах/мин = процент * 10 (1% = 100
      // единиц за 10 минут → 100/10 = 10 ед./мин). Стресс умножает итог: ×0,75
      // за любой и ещё ×0,5 за кумулятивный.
      var healthMax=Math.max(1,num(vitals().maxHealth)||10000);
      var canRegen=num(vitals().health)<healthMax;
      var elevatedMetabolism=metabolismPercent()>60;
      var elevatedResilience=resiliencePercent()>60;
      var bull=hasEffect("bull");
      var perTen=1+(elevatedMetabolism?1:0)+(elevatedResilience?1:0)+(bull?2:0);
      var baseUnitsPerMinute=perTen*10;
      var afterAnyStress=baseUnitsPerMinute*(totalStressPercent()>0?0.75:1);
      var effective=afterAnyStress*(num(conditions().cumulativeStress)>0?0.5:1);

      result.push(factor("Базовое восстановление: 1% за 10 игр. мин",key,canRegen?10:0,"permanent",canRegen));
      result.push(factor("Повышенный метаболизм: восстановление +1% за 10 игр. мин",key,elevatedMetabolism?10:0,"permanent",elevatedMetabolism));
      result.push(factor("Повышенная устойчивость: восстановление +1% за 10 игр. мин",key,elevatedResilience?10:0,"permanent",elevatedResilience));
      result.push(factor("«Бык»: восстановление +2% за 10 игр. мин",key,bull?20:0,"permanent",bull));
      result.push(factor("Любой стресс: восстановление ×0,75",key,canRegen?afterAnyStress-baseUnitsPerMinute:0,"permanent",canRegen&&totalStressPercent()>0));
      result.push(factor("Кумулятивный стресс: дополнительно ×0,5",key,canRegen?effective-afterAnyStress:0,"permanent",canRegen&&num(conditions().cumulativeStress)>0));
      return result;
    }
    if(key==="energy"||key==="hydration"){
      // РАСКЛАДКА РАСХОДА. Числа берутся у ДОМЕНА, а не из таблицы констант в
      // JavaScript.
      //
      // Что было не так. Строка «Базовый расход» считалась здесь по СВОИМ числам
      // (2500/3, 300/3000…), и автор справедливо увидел расхождение: он поправил
      // нормы в `CharacterVitalsTuning`, строка динамики за ним пошла, а ЭТА
      // строка осталась со старыми 5 мл и 13,9 ккал — правку калибровки она не
      // замечала, потому что норм в ней не было вовсе, был их литерал.
      //
      // Теперь норма ВЫВОДИТСЯ из итоговой скорости делением на поправку
      // метаболизма, а сама поправка приходит полем `conditionRates.metabolismFactor`.
      // Ни одного числа баланса в странице не осталось.
      //
      // Сон здесь не участвует: в этом симуляторе сон — РАЗОВЫЙ шаг (час за
      // нажатие), а не длящееся состояние, и множителя «во сне» в скорости нет.
      var speed=num(rateFor(key)), mf=metabolismFactor();
      // Норма ЗА ИГРОВУЮ МИНУТУ — это скорость ДО поправки метаболизма, то есть
      // speed / mf. Вклад измеряется в единицах шкалы за минуту, как и все
      // вклады монитора: умножать на 60 здесь нельзя, иначе строка обещала бы
      // «−1250 ккал/мин» вместо «−20,8».
      var normalBase=speed/mf;
      result.push(factor(key==="energy"?"Базовый расход энергии в Нормальном состоянии":"Базовый расход жидкости в Нормальном состоянии",key,normalBase,"permanent",true));
      var needs=num(vitals().health)<num(vitals().maxHealth);
      // Восстановление здоровья — ПОЛЕЗНО, но его цена — РАСХОД ценного
      // ресурса, и зелёный на ней вводил в заблуждение: автор видел «зелёный
      // минус». semanticOverride = -1 означает «знак минус опасен для игрока»,
      // поэтому цена красится красным, хотя она и есть плата за пользу.
      result.push(factor(key==="energy"?"Восстановление здоровья: 1:1 по энергии":"Восстановление здоровья: 1:2 по жидкости",key,needs?rate*0.15:0,"permanent",needs,-1));
      // ШТРАФ И БОНУС МЕТАБОЛИЗМА. Пороги (45 и 75) приходят от домена, а
      // величина вклада выведена из ФАКТИЧЕСКОГО множителя: вклад равен разнице
      // между скоростью и её «нормальным» значением speed / mf. При пониженном
      // метаболизме вклад ПОЛОЖИТЕЛЕН (расход сэкономлен — игроку выгодно,
      // поэтому строка зелёная), при повышенном — отрицателен (расход вырос).
      // Оба знака — арифметические, и цвет ставится по ним, а не наоборот.
      var metabolismContribution=speed-speed/mf;
      var reduced=metabolismPercent()<reducedMetabolismPercent();
      var elevated=!reduced&&mf>1;
      result.push(factor("Метаболизм <"+fmt(reducedMetabolismPercent(),0)+"%: расход ×0,9",key,reduced?metabolismContribution:0,"permanent",reduced));
      result.push(factor("Метаболизм ≥"+fmt(elevatedMetabolismPercent(),0)+"%: расход ×1,25",key,elevated?metabolismContribution:0,"permanent",elevated));
      return result;
    }
    if(key==="fatigue"){
      if(isMoving()) result.push(factor("Нормальная нагрузка: движение в транспорте",key,100/18/60,"permanent",true));
      else {
        var rest=-100/9/60*(1-totalStressPercent()/100);
        result.push(factor("Покой: восстановление усталости",key,rest,"permanent",Math.abs(rest)>FLAT_RATE_EPSILON));
      }
      result.push(factor("Кумулятивная усталость ускоряет истощение",key,num(conditions().cumulativeFatigue)>0?rate*0.05:0,"permanent",num(conditions().cumulativeFatigue)>0));
      return result;
    }
    if(key==="stress"){
      var cf=num(conditions().cumulativeFatigue)>0;
      result.push(factor("Кумулятивная усталость формирует стресс",key,cf?rate:0,"permanent",cf));
      var res=resiliencePercent();
      result.push(factor("Устойчивость уменьшает скорость накопления",key,rate!==0?-Math.abs(rate)*Math.min(0.25,res/400):0,"permanent",true));
      // «Жажда» — АДДИТИВНАЯ прибавка +0,5 %/мин (50 ед./мин), «Обезвоживание» —
      // множитель ×1,25 к уже полученной скорости. Числа совпадают с доменом.
      result.push(factor("«Жажда»: +0,5% стресса в игр. мин",key,hasEffect("thirst")?50:0,"permanent",hasEffect("thirst"),-1));
      result.push(factor("«Обезвоживание»: стресс ×1,25",key,hasEffect("dehydration")?Math.abs(rate)*0.25:0,"permanent",hasEffect("dehydration"),-1));
      return result;
    }
    if(key==="resilience"){
      var rp=resiliencePercent(), target=hasEffect("bum")?50:60;
      var delta=target>rp?Math.min(0.8333,(target-rp)/15):-Math.min(0.8333,(rp-target)/15);
      result.push(factor("Возврат к номиналу 60%",key,hasEffect("bum")?0:delta,"permanent",!hasEffect("bum")&&Math.abs(delta)>FLAT_RATE_EPSILON));
      result.push(factor("Дебафф «Бомж» ограничивает устойчивость 50%",key,hasEffect("bum")?rate:0,"permanent",hasEffect("bum"),-1));
      if(hasEffect("dehydration"))
        result.push(factor("«Обезвоживание»: устойчивость падает вместе с расходом жидкости",key,-Math.abs(rateFor("hydration")),"permanent",true,-1));
      return result;
    }
    if(key==="metabolism"){
      var mp2=metabolismPercent();
      result.push(factor("Возврат к номиналу 60%",key,(60-mp2)*100/60/60,"permanent",Math.abs(60-mp2)>0));
      [['cumulativeStress',"Истощение стресса"],["cumulativeFatigue","Истощение усталости"],["cumulativeHydration","Истощение жидкости"],["cumulativeEnergy","Истощение энергии"]].forEach(function(entry){
        var active=num(conditions()[entry[0]])>0;
        result.push(factor(entry[1]+": −2% за 15 игр. мин",key,active?-200/60:0,"permanent",active,1));
      });
      // «Жажда» отнимает 1% за 10 игр. мин, «Обезвоживание» — вдвое быстрее, 2%.
      if(hasEffect("dehydration"))
        result.push(factor("«Обезвоживание»: метаболизм −2% за 10 игр. мин",key,-200/60,"permanent",true,1));
      else if(hasEffect("thirst"))
        result.push(factor("«Жажда»: метаболизм −1% за 10 игр. мин",key,-100/60,"permanent",true,1));
      return result;
    }
    return result;
  }
  function renderFactor(item,key){
    var cls="indicatorFact "+(item.active?"permanentActive":"permanentInactive");
    return "<div class='"+cls+"'>• "+esc(item.text)+" "+contributionHtml(item.rate,key,item.semanticOverride)+"</div>";
  }
  function renderDynamicFactor(item,key){
    return "<div class='indicatorFact dynamicFactor'>• "+esc(item.text)+" "+contributionHtml(item.rate,key,item.semanticOverride)+"</div>";
  }
  function headerDynamics(key){
    var rate=rateFor(key);
    if(!Number.isFinite(rate)||Math.abs(rate)<FLAT_RATE_EPSILON) return "<span class='trendFlat'>— 0 "+rateUnit(key)+"/мин (0 ед./час)</span>";
    var rising=rate>0, good=GOOD_DIRECTION[key]||0, positive=good!==0&&rising===(good>0);
    // Физическая скорость в шапке: −13,9 ккал/мин читается лучше, чем −41,7 ед./мин.
    var c=contribution(rate,key);
    return "<span class='"+(positive?"trendGood":"trendBad")+"'>"+(rising?"▲":"▼")+" "+(rate>0?"+":"")+fmt(c.physicalPerMinute,1)+" "+c.unit+"/мин ("+(rate*60>0?"+":"")+fmt(rate*60,1)+" ед./час)</span>";
  }
  function stateFromRates(){
    var sum=0,count=0;
    (v()&&v().SCALES||[]).forEach(function(item){
      var rate=rateFor(item.key); if(!Number.isFinite(rate)) return;
      var values=valuesFor(item.key); if(!values.maximum) return;
      var useful=rate*60/values.maximum*100*(GOOD_DIRECTION[item.key]||0);
      sum+=Math.max(-100,Math.min(100,useful)); count++;
    });
    var mean=count?sum/count:0, score=Math.max(0,Math.min(100,50+mean/2));
    var band=100/7, index=Math.min(6,Math.floor(score/band));
    var states=[
      ["Критическое","stateCritical"],["Тяжёлое","stateSevere"],["Неудовлетворительное","stateUnsat"],
      ["Удовлетворительное","stateSatisfactory"],["Нормальное","stateNormal"],["Положительное","statePositive"],["Превосходное","stateExcellent"]
    ];
    return {text:states[index][0],className:states[index][1],score:score};
  }
  function renderState(){
    var node=document.getElementById("characterStateValue"); if(!node) return;
    var state=stateFromRates(); node.className="characterStateValue "+state.className;
    node.textContent=state.text; node.title="Суммарная оценка динамики: "+fmt(state.score,1)+"/100";
  }
  function renderCard(key){
    var s=scale(key); if(!s) return "";
    var values=valuesFor(key), current=Math.round(values.actual), maximum=Math.round(values.maximum);
    var display=Math.round(values.displayPercent==null?values.percent:values.displayPercent);
    // «Реальное значение»: у энергии и жидкости показывается ФИЗИЧЕСКАЯ величина
    // (2750 / 5000 ккал, 2100 / 3000 мл) — ровно как в сайдбаре. Внутренние
    // единицы 0..10000 в интерфейс не попадают: автор видел в них ошибку
    // («потолок 10000 вместо 3000»). У остальных шкал размерности нет, поэтому
    // они остаются в единицах.
    var real=values.maxPhysical!=null
      ? (Math.round(values.physical)+" / "+Math.round(values.maxPhysical)+" "+values.physicalUnit)
      : (current+" / "+maximum);
    var permanent=permanentFactors(key), active=permanent.filter(function(x){return x.active;}), inactive=permanent.filter(function(x){return !x.active;});
    var dynamic=dynamicFactors(key), highlighted=key===highlightKey?" indicatorHighlight":"";
    // Шкала рисуется ДОМЕННОЙ геометрией сайдбара (AssistVitals.barTrack): цвет,
    // двойная (кумулятивная) часть и форсаж совпадают с блоком состояния в
    // сайдбаре, поэтому «одна шкала» не выглядит двумя разными приборами.
    var bar=(v()&&v().barTrack)?v().barTrack(s,values):"";
    return "<section class='indicatorCard"+highlighted+"' data-scale-key='"+esc(key)+"'>"+
      "<div class='indicatorTopRow'><div class='indicatorName' data-game-tooltip=\""+esc(scaleDescription(s))+"\">"+esc(s.label)+"</div>"+
      "<div class='indicatorDynamics'>"+headerDynamics(key)+"</div>"+
      "<div class='indicatorValue'><div class='indicatorScale'>"+bar+"</div>"+
      "<div class='indicatorValueText'><span class='valueReal'>("+real+")</span> <span class='valuePercent'>"+display+"%</span></div></div></div>"+
      "<div class='indicatorFactors'>"+dynamicChipRows(key)+
      dynamic.map(function(x){return renderDynamicFactor(x,key);}).join("")+
      active.map(function(x){return renderFactor(x,key);}).join("")+
      inactive.map(function(x){return renderFactor(x,key);}).join("")+
      "</div></section>";
  }
  /**
   * Ключ кнопки-чипа.
   *
   * Чип — это ссылка на пункт (перк, бафф, предмет), и её идентификатор и есть
   * ключ. Шкала входит в ключ обязательно: один и тот же пункт влияет на
   * НЕСКОЛЬКО шкал (например «Бык» — на здоровье, энергию и жидкость), и без
   * шкалы три разных чипа считались бы одним — узел достался бы только одному
   * месту, а остальные пропали бы из разметки.
   */
  function chipKey(node){
    var card=node.closest?node.closest("[data-scale-key]"):null;
    var scale=card?String(card.getAttribute("data-scale-key")||""):"";
    return scale+"\u0001"+
      String(node.getAttribute("data-perk-link")||"")+"\u0001"+
      String(node.getAttribute("data-item-link")||"");
  }
  /**
   * Переносит кнопки-чипы из ПРЕЖНЕЙ карточки в новую КАК ЕСТЬ.
   *
   * Разметка монитора меняется на каждом тике: значения шкал и вклады пунктов
   * живые. Из-за этого `innerHTML` подменял ВСЕ узлы, включая кнопки активных
   * перков и предметов. Новый узел начинал переход оформления (у `button` из
   * общего правила задано `transition:background-color 50ms ease`) с нуля, и под
   * курсором цвет ходил между обычным и наведённым — снаружи ровно «кнопки
   * постоянно мерцают то выделением, то без него».
   *
   * Ключевым здесь является не столько подмена узла, сколько ПЕРЕНОС: элемент
   * переезжает из живого узла в живой и документа не покидает, поэтому :hover и
   * начатый переход сохраняются. Числа внутри чипа обновляются как обычно — при
   * переносе узел берётся уже с новым содержимым вклада.
   *
   * С появлением `AssistDom.reconcile` (dom_reconcile.js) этой функции уже мало:
   * она спасает ТОЛЬКО кнопки-чипы, а плитки желудка, кнопка «Содержимое» и
   * подписи шкал оставались подменяемыми — и мерцали ровно так же. Сейчас вся
   * карточка собирается сверкой узлов, а перенос чипов оставлен как ВТОРОЙ рубеж
   * для кнопок, у которых ключ разметки когда-нибудь изменится.
   */
  function reuseChips(from,to){
    var previous={};
    from.querySelectorAll("button.indicatorChip").forEach(function(node){previous[chipKey(node)]=node;});
    to.querySelectorAll("button.indicatorChip").forEach(function(fresh){
      var keep=previous[chipKey(fresh)];
      if(keep) fresh.replaceWith(keep);
    });
  }
  /**
   * Обновляет тело монитора, СОХРАНЯЯ живые узлы.
   *
   * Прежде разметка вставлялась через `innerHTML` (с переносом чипов поверх).
   * Каждая замена узла начинала переход оформления заново, и под курсором это
   * выглядело как мерцание плашек — автор: «в окне монитора любая плашка с
   * хувером мерцает, фликер». Сверка узлов (`AssistDom.reconcile`) переносит в
   * документ только настоящие изменения, поэтому элемент под курсором остаётся
   * ТЕМ ЖЕ узлом и переход не перезапускается.
   *
   * Запасной путь на случай, если общий модуль не подключён (старая страница из
   * кеша WebView2): прежнее поведение — вставить разметку целиком и перенести
   * чипы. Лучше редкое мерцание, чем пустое окно.
   */
  function applyBodyMarkup(markup){
    if(global.AssistDom&&global.AssistDom.reconcile){
      global.AssistDom.reconcile(body,markup);
      return;
    }
    var shell=document.createElement("div");
    shell.innerHTML=markup;
    swapKeepingChips(shell);
  }
  /**
   * Ставит свежую разметку, СОХРАНЯЯ узлы чипов.
   *
   * Новая карточка вставляется РЯДОМ со старой, чипы переносятся, и только потом
   * старая карточка убирается. Так узлы чипов ни на мгновение не оказываются вне
   * документа: `innerHTML` этого не позволяет — он отвязывает всё поддерево
   * целиком, и начатый переход оформления пришлось бы начинать заново.
   */
  function swapKeepingChips(shell){
    var fresh=Array.prototype.slice.call(shell.childNodes);
    var old=Array.prototype.slice.call(body.childNodes);
    var index=0;
    for(;index<fresh.length;index++){
      if(index<old.length){
        body.insertBefore(fresh[index],old[index]);
        reuseChips(old[index],fresh[index]);
        body.removeChild(old[index]);
      } else {
        body.appendChild(fresh[index]);
      }
    }
    for(;index<old.length;index++) body.removeChild(old[index]);
  }
  // ── Блок «Желудок» ───────────────────────────────────────────────────────
  //
  // Порции раскладываются ПЛИТКОЙ из горизонтальных прямоугольников: высота у
  // всех одинаковая (две строки текста), а ширина — по занимаемому объёму,
  // поэтому «площадь = объём» сохраняется. Место порции в желудке значения не
  // имеет — важен только размер, и это то, что задал автор: «объекты должны
  // просто располагаться плиткой».
  //
  // Числа приходят ИЗ ДОМЕНА (digestion в снимке): литры, ккал, миллилитры и
  // остаток времени. Считать их здесь значило бы завести вторую версию формул
  // пищеварения, которая разошлась бы с начислением.
  function digestion(){ return (snapshot&&snapshot.digestion)||null; }
  function stomachPortions(){ var d=digestion(); return d&&Array.isArray(d.portions)?d.portions:[]; }
  function itemDef(itemId){
    var wanted=String(itemId||"").toLowerCase();
    return (itemCatalog||[]).find(function(e){return String(e.id||"").toLowerCase()===wanted;})||null;
  }
  function itemName(itemId){
    var def=itemDef(itemId);
    return (def&&(def.name||def.id))||String(itemId||"");
  }
  function itemColor(itemId){
    var def=itemDef(itemId);
    return (def&&def.color)||"#3a4149";
  }
  /**
   * Читаемый цвет подписи на заливке предмета — БЕЛЫЙ или ЧЁРНЫЙ.
   *
   * Автор: «проверить контраст текста в плашках содержимого желудка. Плохо
   * читается, плашки светлые, текст белый». Так и было: заливка берётся из
   * каталога (молоко #e8e0ca, витамин C #f0c94b, записка #e7d58a), а подпись
   * всегда белая — на светлом фоне это почти нечитаемо.
   *
   * Выбор делается по ОТНОСИТЕЛЬНОЙ ЯРКОСТИ (формула WCAG): берётся вариант с
   * большим отношением контраста. Одного порога яркости мало — у средних тонов
   * (например оранжевый апельсина) выигрыш белого и чёрного почти равен, и
   * сравнение отношений даёт устойчивый выбор вместо скачка на границе.
   *
   * Цвет считается ЗДЕСЬ, а не задан в оформлении: заливка у каждого предмета
   * своя и приходит из каталога, а CSS не умеет выбирать цвет текста по фону.
   */
  var DARK_INK="#10161c", LIGHT_INK="#ffffff";
  function channelLuminance(value){
    var c=value/255;
    return c<=0.03928?c/12.92:Math.pow((c+0.055)/1.055,2.4);
  }
  function relativeLuminance(rgb){
    return 0.2126*channelLuminance(rgb[0])+0.7152*channelLuminance(rgb[1])+0.0722*channelLuminance(rgb[2]);
  }
  function contrastRatio(first,second){
    var a=relativeLuminance(first), b=relativeLuminance(second);
    var light=Math.max(a,b), dark=Math.min(a,b);
    return (light+0.05)/(dark+0.05);
  }
  /** Разбор заливки: `#rgb`, `#rrggbb`; иначе `null` (тогда подпись белая). */
  function parseColour(value){
    var text=String(value||"").trim();
    if(text.charAt(0)!=="#") return null;
    var hex=text.slice(1);
    if(hex.length===3) hex=hex.charAt(0)+hex.charAt(0)+hex.charAt(1)+hex.charAt(1)+hex.charAt(2)+hex.charAt(2);
    if(hex.length!==6||!/^[0-9a-f]{6}$/i.test(hex)) return null;
    return [parseInt(hex.slice(0,2),16),parseInt(hex.slice(2,4),16),parseInt(hex.slice(4,6),16)];
  }
  function readableInk(background){
    var rgb=parseColour(background);
    if(!rgb) return LIGHT_INK;
    var onDark=contrastRatio(rgb,[255,255,255]);
    var onLight=contrastRatio(rgb,[16,22,28]);
    return onDark>=onLight?LIGHT_INK:DARK_INK;
  }
  /**
   * Тень подписи — ТОЛЬКО у белого варианта.
   *
   * На тёмной плитке белый текст иногда идёт по среднему тону (оранжевый
   * апельсина), и тонкая тёмная тень возвращает чёткость края. У чёрной подписи
   * тень была бы светлой и только размывала бы буквы.
   */
  function inkShadow(ink, background){
    return ink===LIGHT_INK?("text-shadow:0 1px 1px "+esc(shadowInk(background))):"text-shadow:none";
  }
  /** Затемнённая заливка для тени: тот же цвет, вдвое темнее. */
  function shadowInk(background){
    var rgb=parseColour(background);
    if(!rgb) return "rgba(0,0,0,.65)";
    return "rgba("+Math.round(rgb[0]*0.4)+","+Math.round(rgb[1]*0.4)+","+Math.round(rgb[2]*0.4)+",.85)";
  }
  /**
   * Таймер порции словами: «2 ч 15 мин», «48 мин», «< 1 мин».
   *
   * Считается от ИГРОВЫХ секунд, которые прислал домен: игровое время идёт 1:1 с
   * реальным, но при ускорении FF срок укорачивается вместе с миром, и подпись
   * обязана это показывать.
   */
  function durationLabel(seconds){
    var total=Math.max(0,Math.round(num(seconds)));
    if(total<60) return "< 1 мин";
    var minutes=Math.round(total/60);
    if(minutes<60) return minutes+" мин";
    var hours=Math.floor(minutes/60); minutes=minutes%60;
    return minutes>0?hours+" ч "+minutes+" мин":hours+" ч";
  }
  /**
   * Область желудка — ЧЕТЫРЕ СТРОКИ ПО 35px, и её площадь строго отвечает
   * литру: 1 литр распределён на 470×140px (автор: «объём желудка визуально
   * будет состоять из 4 строк высотой по 35px»).
   *
   * Раньше ширина плитки считалась как «литры × 240px» в одну строку: полный
   * литр давал 240px — то есть область визуально не имела ничего общего с
   * литром, и «занято 1 из 1 л» показывалось при видимой пустоте (ровно это и
   * увидел автор). Теперь объём — это ПЛОЩАДЬ.
   */
  var STOMACH_MILLILITRES_PER_ROW = 250;
  var STOMACH_ROW_COUNT = 4;
  var STOMACH_TILE_HEIGHT = 35;
  var STOMACH_TILE_GAP = 3;
  function millilitres(value){
    var ml=num(value);
    return ml>0?ml:0;
  }
  /**
   * Объём порции в миллилитрах. Домен присылает миллилитры готовыми
   * (`volumeMilliliters`); литры — только запасной путь для старой разметки,
   * чтобы страница не пересчитывала объём двумя разными формулами.
   */
  function volumeMillilitres(portion){
    if(!portion) return 0;
    return millilitres(portion.volumeMilliliters!=null
      ? portion.volumeMilliliters
      : num(portion.volumeLiters)*1000);
  }
  /**
   * Раскладывает порции по области желудка — плиткой, БЕЗ наложения и БЕЗ
   * потерь, с площадью, строго пропорциональной миллилитрам.
   *
   * Автор: «1 литр желудка нужно распределить по области размером 470×140px.
   * Предметы должны в этой области располагаться плиткой без пересечения или
   * наложения. Занятое и свободное пространство в этой области должно строго
   * соответствовать занятым и свободным миллилитрам желудка».
   *
   * Как это устроено. Область — ЭТАЖЕРКА из четырёх рядов по 35px, и в каждом
   * ряду укладывается ровно 250 мл (250 × 4 = 1000). Ряд заполняется слева
   * направо, а когда в ряду не остаётся места, порция ПРОДОЛЖАЕТСЯ в следующем
   * ряду с его начала. Отсюда три свойства сразу:
   *
   *   • НАЛОЖЕНИЯ НЕВОЗМОЖНЫ по построению: в ряду ведётся занятость в
   *     миллилитрах, и следующий участок начинается ровно там, где кончился
   *     предыдущий;
   *   • ПОТЕРЯТЬ ПОРЦИЮ НЕЛЬЗЯ: место ищется по одному миллилитру, поэтому
   *     участок находится, пока в этажерке есть хоть один свободный миллилитр;
   *   • ПЛОЩАДЬ РАВНА ОБЪЁМУ: ширина участка — его миллилитры, высота — ряд,
   *     поэтому сумма площадей всех участков и есть занятые миллилитры.
   *
   * Крупная порция показывается НЕСКОЛЬКИМИ прямоугольниками той же заливки:
   * вода 500 мл — двумя по 250 мл, обед 400 мл — отрезками 250 + 150. Иначе
   * блоки разной ширины пришлось бы сводить в один прямоугольник по ширине
   * самого широкого ряда, и «обед 400 мл» занял бы площадь как 500 мл — то
   * самое расхождение площади и объёма, на которое автор и жаловался.
   *
   * Прямоугольники одной порции остаются ОДНИМ предметом: у них один
   * `data-stomach-item`, поэтому и наведение, и ПКМ, и подписи работают как с
   * целым предметом.
   */
  function tileRectangles(portions){
    var rows=[];
    for(var r=0;r<STOMACH_ROW_COUNT;r++) rows.push(0);
    var rects=[];
    portions.forEach(function(portion){
      var ml=volumeMillilitres(portion);
      var parts=[];
      if(ml<=0){
        // Нулевая порция (мыло, каталожная мелочь) всё равно должна быть видна:
        // предмет в желудке есть, и пропадать из области ему нельзя. Ширину ей
        // даёт минимальные 2px в layoutStomachTiles.
        var zero=freestRow(rows);
        parts.push({row:zero,startMl:rows[zero],widthMl:0});
      } else {
        var remaining=ml;
        while(remaining>1e-9){
          var row=-1;
          for(var i=0;i<rows.length;i++){
            if(rows[i]<STOMACH_MILLILITRES_PER_ROW-1e-9){ row=i; break; }
          }
          if(row<0) break; // свободного места нет вовсе — домен такого не допустит
          var free=STOMACH_MILLILITRES_PER_ROW-rows[row];
          var slice=Math.min(remaining,free);
          parts.push({row:row,startMl:rows[row],widthMl:slice});
          rows[row]+=slice;
          remaining-=slice;
        }
      }
      // Подпись несёт ТОЛЬКО первая часть: иначе «Вода» была бы написана столько
      // раз, сколько рядов занимает бутылка, и предмет читался бы как несколько.
      parts.forEach(function(part,index){
        rects.push({
          portion:portion,
          row:part.row,
          startMl:part.startMl,
          widthMl:part.widthMl,
          volumeMl:ml,
          partIndex:index,
          partCount:parts.length
        });
      });
    });
    return rects;
  }
  /** Номер самого свободного ряда — для порций без объёма. */
  function freestRow(rows){
    var best=0;
    for(var i=1;i<rows.length;i++) if(rows[i]<rows[best]) best=i;
    return best;
  }
  /**
   * Разметка участка плитки. Ширина — его миллилитры (ставит layoutStomachTiles),
   * высота — ровно один ряд (35px), поэтому «четыре строки по 35px» выполняются
   * буквально, а литр занимает всю область без остатка.
   *
   * Объём подписан в миллилитрах (автор: «объём желудка отображать в
   * миллилитрах»): 5 мл мёда читаются как «5 мл», а не как «0,005 л».
   */
  function portionTileHtml(rect){
    var portion=rect.portion, ml=rect.volumeMl;
    var background=itemColor(portion.itemId);
    // Цвет подписи выбирается по ЯРКОСТИ заливки: у молока, витамина и записки
    // заливка светлая, и белый текст на них не читался (замечание автора).
    var ink=readableInk(background);
    // Продолжение крупной порции (вода 500 мл = два ряда) рисуется тем же
    // цветом и с тем же `data-stomach-item`, но БЕЗ текста: подпись дублировалась
    // бы в каждом ряду, и один предмет выглядел бы как несколько.
    var inner=rect.partIndex>0
      ? ""
      : "<div class='stomachPortionName'>"+esc(itemName(portion.itemId))+"</div>"+
        "<div class='stomachPortionTimer'>"+durationLabel(portion.remainingGameSeconds)+
        " · "+fmt(ml,0)+" мл</div>";
    // Цвет подписи ставится здесь ПОСТОЯННО (а не меняется только когда заливка
    // светлая): иначе узел переключал бы цвет подписи при смене предмета, а это
    // ровно то, ради чего заведена сверка узлов.
    return "<div class='stomachPortion"+(rect.partIndex>0?" stomachPortionPart":"")+
      "' data-stomach-item='"+esc(portion.itemId)+
      "' data-stomach-ml='"+fmt(rect.widthMl,3)+"' data-stomach-volume-ml='"+fmt(ml,3)+"'"+
      " data-stomach-row='"+rect.row+"'"+
      " data-stomach-start='"+fmt(rect.startMl,3)+"'"+
      " data-stomach-part='"+rect.partIndex+"'"+
      " style='background:"+esc(background)+";color:"+ink+";"+inkShadow(ink,background)+"'>"+inner+"</div>";
  }
  /**
   * Ставит участкам координаты ПОСЛЕ вставки разметки: ширина области известна
   * только в документе, поэтому укладка не может жить в stomachMarkup().
   *
   * Миллилитры → пиксели: ширина внутренней части области делится на 250 мл в
   * ряду, высота — на четыре ряда по 35px. Отсюда «занято / всего» и площадь
   * считаются одной формулой, и разойтись они не могут.
   */
  function layoutStomachTiles(root){
    if(!root) return;
    var area=root.querySelector("#stomachArea");
    if(!area) return;
    var inner=area.clientWidth-2*7;
    var innerHeight=area.clientHeight-2*7;
    if(inner<=0||innerHeight<=0) return;
    var pxPerMl=inner/STOMACH_MILLILITRES_PER_ROW;
    var nodes=area.querySelectorAll(".stomachPortion");
    for(var i=0;i<nodes.length;i++){
      var node=nodes[i];
      var row=num(node.getAttribute("data-stomach-row"));
      var start=num(node.getAttribute("data-stomach-start"));
      var widthMl=num(node.getAttribute("data-stomach-ml"));
      node.style.left=Math.round(start*pxPerMl)+"px";
      // Меньше двух пикселей плитка не бывает даже при объёме в 3 мл: тонкая
      // линия читается как «почти ничего», а исчезнувший предмет — как ошибка.
      node.style.width=Math.max(2,Math.round(widthMl*pxPerMl)-STOMACH_TILE_GAP)+"px";
      node.style.top=(row*STOMACH_TILE_HEIGHT)+"px";
      node.style.height=(STOMACH_TILE_HEIGHT-STOMACH_TILE_GAP)+"px";
    }
  }
  /**
   * Подбирает шрифт строки плитки так, чтобы текст ПОМЕЩАЛСЯ в ширину.
   *
   * Автор: «если текст внутри предмета не помещается, уменьшаем шрифт».
   * Плитка занимает ряд целиком (250 мл), поэтому шрифт уменьшается РЕДКО —
   * только для самых длинных названий каталога («Соли для восстановления
   * организма» ≈ 230px при базовых 10px).
   *
   * Предел ужатия — 6.5px: ниже текст уже не читается, и такое название честнее
   * показать целиком в списке содержимого. Высота строки при этом НЕ меняется
   * (12px в CSS): иначе ужатая плитка стала бы ниже соседей, и ряд перестал бы
   * быть ровным.
   *
   * Уменьшать шрифт можно только ПРОГРАММНО: CSS этого не умеет, а `text-fit`
   * браузеры не поддерживают. Замер идёт после вставки в документ, поэтому
   * вызывается это из render(), а не из stomachMarkup().
   */
  var STOMACH_MIN_FONT_PX = 6.5;
  function fitPortionText(root){
    if(!root) return;
    var nodes=root.querySelectorAll(".stomachPortion");
    for(var i=0;i<nodes.length;i++){
      var lines=nodes[i].querySelectorAll(".stomachPortionName,.stomachPortionTimer");
      for(var j=0;j<lines.length;j++){
        var node=lines[j];
        // Сбрасываем прошлый подбор: замер ведётся от исходного размера, иначе
        // шрифт только уменьшался бы и никогда не возвращался назад.
        node.style.fontSize="";
        if(node.scrollWidth<=node.clientWidth) continue;
        for(var size=9.5;size>=STOMACH_MIN_FONT_PX;size-=0.5){
          node.style.fontSize=size+"px";
          if(node.scrollWidth<=node.clientWidth) break;
        }
      }
    }
  }
  /**
   * Тултип порции: СПИСОК ВЕЩЕСТВ, которые организм получит, и ОБЪЁМ порции.
   *
   * Перечисляются только те, что реально есть в порции: обещать «0 ккал» у
   * воды значило бы показывать пустую строку как полезную.
   *
   * «Жидкость» и «Объём» — РАЗНЫЕ величины, и подписаны они по-разному: у банана
   * жидкость 110 мл (вода внутри), а объём порции 150 мл. Путать их нельзя — на
   * этой путанице и выросло сообщение автора о «несоответствии добавленного».
   */
  function portionTooltipHtml(portion){
    var items=[];
    if(num(portion.kilocalories)>0) items.push("<li>Энергия: "+fmt(portion.kilocalories,0)+" ккал</li>");
    if(num(portion.waterMilliliters)>0) items.push("<li>Жидкость: "+fmt(portion.waterMilliliters,0)+" мл</li>");
    // Объём берём из ТОЙ ЖЕ величины, что печатают меню и список содержимого
    // (`volumeMilliliters`), а не из литров: иначе три места показывали бы три
    // числа на одну порцию.
    items.push("<li>Объём: "+fmt(volumeMillilitres(portion),0)+" мл из литра</li>");
    items.push("<li>Осталось: "+durationLabel(portion.remainingGameSeconds)+"</li>");
    return "<strong>"+esc(itemName(portion.itemId))+"</strong><ul>"+items.join("")+"</ul>";
  }
  function stomachFactorsHtml(){
    var d=digestion(), factors=d&&Array.isArray(d.factors)?d.factors:[];
    if(!factors.length) return "";
    return ", " + factors.map(function(f){
      return "<span class='"+(f.useful?"stomachFactorGood":"stomachFactorBad")+"'>"+esc(f.text)+"</span>";
    }).join(", ");
  }
  function stomachMarkup(){
    var d=digestion();
    if(!d) return "";
    var portions=stomachPortions(), inner="";
    // Координаты плиткам ставит layoutStomachTiles ПОСЛЕ вставки: ширина области
    // известна только в документе.
    tileRectangles(portions).forEach(function(rect){
      inner+=portionTileHtml(rect);
    });
    if(!inner) inner="<div class='stomachAreaEmpty'>Желудок пуст. ПКМ — употребить предмет.</div>";

    // Общая динамика усвоения — в ФИЗИЧЕСКИХ величинах, как и шкалы в карточках:
    // «+10 ккал/мин» игрок сверяет с этикеткой еды, а «+21 ед./мин» — ни с чем.
    var energyPerMinute=num(d.energyPerMinute), hydrationPerMinute=num(d.hydrationPerMinute);
    var dynamics=[];
    if(energyPerMinute>0) dynamics.push("+"+fmt(energyPerMinute/2,1)+" ккал/мин");
    if(hydrationPerMinute>0) dynamics.push("+"+fmt(hydrationPerMinute*0.3,0)+" мл/мин");
    var dynamicsHtml=dynamics.length
      ? "<span class='stomachDynamics'>Усвоение: "+dynamics.join(", ")+"</span>"
      : "<span class='stomachDynamics'>Усвоение: —</span>";

    // Занятый объём — в МИЛЛИЛИТРАХ (автор: «объём желудка отображать в
    // миллилитрах»). Миллилитры приходят из домена готовыми: страница их не
    // пересчитывает из литров, иначе подпись и вёрстка области могли бы
    // разойтись. Объём — единственный предел желудка: он не даёт съесть больше
    // литра, и без числа игрок не понимает, почему предмет не помещается.
    var occupied=millilitres(d.occupiedMilliliters), total=millilitres(d.totalMilliliters);
    var volumeHtml=total>0
      ? "<span class='stomachVolume' title='Занятый объём желудка'>"+
        fmt(occupied,0)+" / "+fmt(total,0)+" мл</span>"
      : "";

    return "<section class='indicatorCard stomachCard' data-stomach='1'>"+
      "<div class='stomachHead'><span class='stomachTitle'>Желудок</span>"+
      dynamicsHtml+
      "<span class='stomachFactors'>"+stomachFactorsHtml()+"</span>"+
      volumeHtml+
      // Кнопка стоит ПОСЛЕ динамики и её влияний (требование автора), а
      // margin-left:auto уводит её к правому краю той же строки.
      "<button type='button' class='stomachContentsButton' id='stomachContentsButton' title='Список содержимого желудка'>Содержимое</button>"+
      "</div>"+
      "<div class='stomachArea' id='stomachArea'>"+inner+"</div></section>";
  }
  function tooltipNode(){ return document.getElementById("stomachTooltip"); }
  function hideTooltip(){ var node=tooltipNode(); if(node) node.hidden=true; }
  function showTooltip(portionNode){
    var node=tooltipNode(); if(!node) return;
    var itemId=portionNode.getAttribute("data-stomach-item");
    var portion=stomachPortions().find(function(p){return String(p.itemId)===String(itemId);});
    if(!portion){ hideTooltip(); return; }
    node.innerHTML=portionTooltipHtml(portion);
    node.hidden=false;
    var rect=portionNode.getBoundingClientRect();
    var width=node.offsetWidth, height=node.offsetHeight;
    var left=Math.min(Math.max(4,rect.left),global.innerWidth-width-6);
    var top=rect.bottom+6;
    if(top+height>global.innerHeight-6) top=Math.max(4,rect.top-height-6);
    node.style.left=left+"px"; node.style.top=top+"px";
  }
  /**
   * Меню КАТАЛОГА употребимого (ПКМ по ПУСТОМУ месту желудка).
   *
   * Автор: «открывает список не инвентаря, а каталог существующих объектов,
   * которые съедобны. Это действие заменяет поиск предмета, выдачу в инвентарь и
   * нажатие "использовать"; действия сокращаются до "выбрал — попало в желудок"».
   * Поэтому список берётся из каталога (поле `consumables`), а НЕ из инвентаря:
   * инвентарь тут не нужен вовсе, ни количество, ни выдача.
   */
  function menuNode(){ return document.getElementById("stomachMenu"); }
  function hideMenu(){ var node=menuNode(); if(node) node.hidden=true; }
  function consumables(){ return (snapshot&&Array.isArray(snapshot.consumables))?snapshot.consumables:[]; }
  /**
   * Объём порции предмета в миллилитрах — ЕДИНСТВЕННАЯ величина, по которой
   * считается и показывается место в желудке.
   *
   * Автор: «в желудок должно добавляться строго то количество миллилитров
   * предмета, которое указано в списке ПКМ-добавления». Значит список и желудок
   * обязаны печатать ОДНО число. Домен отдаёт его в `volumeMilliliters`; прежний
   * `grams` оставлен запасным путём для старой версии Хоста.
   */
  function portionMillilitres(item){
    if(!item) return 0;
    return millilitres(item.volumeMilliliters!=null?item.volumeMilliliters:item.grams);
  }
  /**
   * Подпись пункта меню: ОБЪЁМ ПОРЦИИ, затем пищевая ценность.
   *
   * Объём идёт ПЕРВЫМ и всегда: он единственное число, которое можно сверить с
   * желудком, и именно его автор сверял, когда сообщил о расхождении. Прежняя
   * подпись начиналась с ккал, а объём показывала только у неедовых предметов —
   * то есть у еды его не было вовсе, и сверить было нечего.
   *
   * Содержимое ВОДЫ (сколько жидкости предмет восполняет) сюда НЕ входит: у
   * банана это 110 мл, и в списке добавления оно читалось как «объём порции»,
   * хотя порция — 150 мл, а «110» — вода внутри неё. Именно эта подмена и
   * выглядела как ошибка добавления.
   */
  function consumableMeta(item){
    var parts=[fmt(portionMillilitres(item),0)+" мл"];
    if(num(item.kilocalories)>0) parts.push(fmt(item.kilocalories,0)+" ккал");
    return parts.join(" · ");
  }
  function showMenuAt(x,y){
    var node=menuNode(); if(!node) return;
    var items=consumables();
    // Свободное место в желудке — ОДНО число на всё меню: по нему видно, почему
    // часть пунктов погашена. Считается из тех же миллилитров, что и подпись
    // «занято / всего», поэтому подсказка и шапка всегда сходятся.
    var d=digestion();
    var totalMl=d?millilitres(d.totalMilliliters):0;
    var freeMl=totalMl>0?Math.max(0,totalMl-millilitres(d.occupiedMilliliters)):0;
    node.innerHTML=items.length
      ? "<div class='stomachMenuHead'>Каталог: можно употребить</div>"+
        items.map(function(item){
          var meta=consumableMeta(item);
          // Непомещающиеся предметы — СЕРЫЕ и неактивные (требование автора),
          // а не пропавшие из списка: пропажа читалась бы как «предмет
          // исчез из каталога», а серый пункт с подписью «не помещается»
          // объясняет, ЧТО именно мешает и почему. Признак `fits` считает домен
          // по текущему желудку; при отсутствии поля пункт считается годным,
          // чтобы старая версия Хоста не делала меню целиком мёртвым.
          var fits=item.fits!==false;
          return "<button type='button' class='stomachMenuItem"+(fits?"":" stomachMenuItemDisabled")+
            "' data-use-item='"+esc(item.id)+"'"+(fits?"":" disabled aria-disabled='true'")+
            " title='"+(fits?
              "Употребить "+esc(item.name||item.id):
              // Подсказка называет СВОБОДНЫЙ объём, а не объём предмета: автор
              // спрашивает «почему нельзя», и ответ — «свободно меньше N мл».
              "Не помещается: свободно "+fmt(freeMl,0)+" мл, а порция "+
              fmt(portionMillilitres(item),0)+" мл")+"'>"+
            "<span class='stomachMenuSwatch' style='background:"+esc(item.color||"#3a4149")+"'></span>"+
            "<span class='stomachMenuName'>"+esc(item.name||item.id)+"</span>"+
            (meta?"<span class='stomachMenuMeta'>"+esc(meta)+"</span>":"")+
            "</button>";
        }).join("")
      : "<div class='stomachMenuEmpty'>Каталог употребимого пуст.</div>";
    node.hidden=false;
    var width=node.offsetWidth, height=node.offsetHeight;
    node.style.left=Math.min(Math.max(4,x),global.innerWidth-width-6)+"px";
    node.style.top=Math.min(Math.max(4,y),global.innerHeight-height-6)+"px";
  }

  /**
   * Окно «Содержимое желудка»: ПРОСТОЙ список порций — название и таймер.
   *
   * Плитка отвечает на вопрос «сколько места занято», а этот список — «что именно
   * и когда усвоится». Плитки с этой задачей не справляются: у мелкой порции
   * ширина 44px, и название в неё влезает только ужатым шрифтом или вовсе под
   * многоточием. Здесь ширины хватает, поэтому список читается.
   *
   * ПКМ по строке убирает порцию — то же действие, что ПКМ по плитке.
   */
  function windowNode(){ return document.getElementById("stomachWindow"); }
  function windowBodyNode(){ return document.getElementById("stomachWindowBody"); }
  function hideContentsWindow(){ var node=windowNode(); if(node) node.hidden=true; }
  var lastContentsMarkup="";
  function contentsRowsHtml(){
    var portions=stomachPortions();
    if(!portions.length) return "<div class='stomachWindowEmpty'>Желудок пуст.</div>";
    return portions.map(function(portion){
      var ml=millilitres(portion.volumeMilliliters!=null?portion.volumeMilliliters:num(portion.volumeLiters)*1000);
      return "<div class='stomachRow' data-stomach-item='"+esc(portion.itemId)+"'>"+
        "<span class='stomachRowSwatch' style='background:"+esc(itemColor(portion.itemId))+"'></span>"+
        "<span class='stomachRowName'>"+esc(itemName(portion.itemId))+"</span>"+
        // Объём — в МИЛЛИЛИТРАХ, как и занятое место в шапке блока: автор
        // заметил, что подпись расходилась с добавленным («5 мл мёда добавилось
        // как 30»), и расхождение было в пересчёте. Здесь печатается ТО ЖЕ число,
        // что занимает место в желудке.
        "<span class='stomachRowVolume'>"+fmt(ml,0)+" мл</span>"+
        "<span class='stomachRowTimer'>"+durationLabel(portion.remainingGameSeconds)+"</span></div>";
    }).join("");
  }
  function toggleContentsWindow(button){
    var node=windowNode(); if(!node) return;
    if(!node.hidden){ hideContentsWindow(); return; }
    refreshContentsWindow(true);
    node.hidden=false;
    // Позиция по умолчанию — под кнопкой. Уже открытое окно НЕ переезжает на
    // кнопку: его могли утащить в сторону, и возврат на прежнее место на каждом
    // открытии был бы борьбой с пользователем.
    if(!node.dataset.placed&&button&&button.getBoundingClientRect){
      var rect=button.getBoundingClientRect();
      node.style.left=Math.min(Math.max(4,rect.left),Math.max(4,global.innerWidth-node.offsetWidth-6))+"px";
      node.style.top=Math.min(Math.max(4,rect.bottom+6),Math.max(4,global.innerHeight-node.offsetHeight-6))+"px";
      node.dataset.placed="1";
    }
  }
  /**
   * Обновляет список, если окно ОТКРЫТО.
   *
   * Таймеры обязаны идти: без этого список показывал бы остаток на момент
   * открытия и через минуту разошёлся бы с плиткой.
   *
   * Сравнение идёт с СОБСТВЕННОЙ строкой, а не с `innerHTML`: браузер
   * переписывает атрибуты по-своему, и сравнение с ним перерисовывало бы список
   * на каждом шаге. Перерисовка же сбрасывает выделение и мигает на экране.
   */
  function refreshContentsWindow(force){
    var node=windowNode(); if(!node) return;
    if(!force&&node.hidden) return;
    var body=windowBodyNode(); if(!body) return;
    var markup=contentsRowsHtml();
    if(force||markup!==lastContentsMarkup){ lastContentsMarkup=markup; body.innerHTML=markup; }
  }
  /**
   * Перетаскивание всплывающего окна за шапку.
   *
   * Окно плавающее и перекрывает плитки, поэтому его должно быть можно убрать с
   * дороги, не закрывая: иначе «Содержимое» мешало бы смотреть сам желудок.
   */
  function dragWindowBy(node,handle){
    if(!node||!handle) return;
    var startX=0,startY=0,baseX=0,baseY=0,dragging=false;
    handle.addEventListener("mousedown",function(event){
      if(event.button!==0) return;
      // Клик по крестику не должен начинать перетаскивание.
      if(event.target&&event.target.closest&&event.target.closest("button")) return;
      var rect=node.getBoundingClientRect();
      baseX=rect.left; baseY=rect.top;
      startX=event.clientX; startY=event.clientY; dragging=true;
      event.preventDefault();
    });
    document.addEventListener("mousemove",function(event){
      if(!dragging) return;
      node.style.left=Math.min(Math.max(0,baseX+(event.clientX-startX)),Math.max(0,global.innerWidth-40))+"px";
      node.style.top=Math.min(Math.max(0,baseY+(event.clientY-startY)),Math.max(0,global.innerHeight-24))+"px";
    });
    document.addEventListener("mouseup",function(){dragging=false;});
  }

  function render(){
    if(!body||!snapshot||!v()) return;
    var markup=(v().SCALES||[]).map(function(s){return renderCard(s.key);}).join("");
    // Блок желудка — ПЕРВЫМ: он во всю ширину, и шкалы выстраиваются под ним.
    markup=stomachMarkup()+markup;
    if(markup!==lastMarkup){
      lastMarkup=markup;
      // Подсказка (#assistGameTooltip) висит на body, поэтому внутренние замены
      // её не касаются.
      //
      // Разметка вставляется СВЕРКОЙ УЗЛОВ, а не `innerHTML`: замена узлов
      // начинала переход оформления заново, и плашка под курсором мерцала
      // (см. applyBodyMarkup и dom_reconcile.js).
      applyBodyMarkup(markup);
      if(highlightKey) scrollToHighlight();
    }
    // Подбор шрифта, укладка плиток и список содержимого — ПОСЛЕ вставки:
    // ширина области и строк известна только в документе, а до вставки замер
    // вернул бы нули. Укладка идёт ПЕРВОЙ: подбор шрифта мерит ширину плитки,
    // которую задаёт именно она.
    layoutStomachTiles(body);
    fitPortionText(body);
    refreshContentsWindow();
    renderState();
  }
  function scrollToHighlight(){
    var node=body.querySelector("[data-scale-key='"+highlightKey+"']");
    if(node&&node.scrollIntoView) node.scrollIntoView({block:"center"});
  }
  function setHighlight(key){
    highlightKey=String(key||"");
    if(highlightTimer) global.clearTimeout(highlightTimer);
    highlightTimer=null; render();
    if(!highlightKey) return;
    highlightTimer=global.setTimeout(function(){highlightKey="";highlightTimer=null;render();},4000);
  }
  function applyMessage(message){
    if(!message) return;
    if(message.type==="snapshot"){
      snapshot=message.snapshot||snapshot;
      if(message.playerVitals) snapshot.playerVitals=message.playerVitals;
      if(Array.isArray(message.itemCatalog)) itemCatalog=message.itemCatalog;
      if(snapshot&&message.conditionRates) snapshot.conditionRates=message.conditionRates;
      if(snapshot&&message.digestion) snapshot.digestion=message.digestion;
      if(Array.isArray(message.consumables)) snapshot.consumables=message.consumables;
      if(message.daylight) snapshot.daylight=message.daylight;
      // Признак маршрута приходит РЯДОМ со снимком и должен пережить полное
      // обновление: без этого «покой» возвращался на каждом снимке.
      applyRoute(message);
      simulationRunning=!!message.simulationRunning; simulationPaused=!!message.simulationPaused;
    } else if(message.type==="live_state"){
      if(!snapshot) snapshot={};
      if(message.player) snapshot.player=message.player;
      if(message.playerVitals) snapshot.playerVitals=message.playerVitals;
      if(message.conditions) snapshot.conditions=message.conditions;
      if(message.character) snapshot.character=message.character;
      if(message.conditionRates) snapshot.conditionRates=message.conditionRates;
      if(message.digestion) snapshot.digestion=message.digestion;
      if(Array.isArray(message.consumables)) snapshot.consumables=message.consumables;
      if(Array.isArray(message.itemCatalog)) itemCatalog=message.itemCatalog;
      if(message.daylight) snapshot.daylight=message.daylight;
      applyRoute(message);
      if(typeof message.simulationRunning==="boolean") simulationRunning=message.simulationRunning;
      if(typeof message.simulationPaused==="boolean") simulationPaused=message.simulationPaused;
    } else if(message.type==="highlight"){
      setHighlight(message.key); return;
    }
    render();
  }
  function send(payload){ if(global.chrome&&global.chrome.webview) global.chrome.webview.postMessage(payload); }
  function init(){
    body=document.getElementById("indicatorsBody"); if(!body||!v()) return;
    body.addEventListener("click",function(event){
      var perk=event.target&&event.target.closest?event.target.closest("[data-perk-link]"):null;
      if(perk){send({action:"open_perks",perkId:perk.getAttribute("data-perk-link")||"",perkKind:perk.getAttribute("data-perk-kind")||""});return;}
      var item=event.target&&event.target.closest?event.target.closest("[data-item-link]"):null;
      if(item){send({action:"open_items",itemId:item.getAttribute("data-item-link")||""});return;}
      // Кнопка «Содержимое» тоже живёт в теле и перерисовывается вместе с блоком,
      // поэтому обработчик висит здесь же — на узле он пропадал бы после каждого
      // обновления усвоения.
      var contents=event.target&&event.target.closest?event.target.closest("#stomachContentsButton"):null;
      if(contents){toggleContentsWindow(contents);return;}
    });

    // ── Желудок: тултип, ПКМ по порции, ПКМ по пустому месту ────────────────
    //
    // Обработчики висят на ТЕЛЕ (а не на самих порциях): разметка блока
    // перерисовывается на каждом шаге усвоения, и обработчик на узле пропадал бы
    // вместе с ним — ровно то, из-за чего кнопки в этом окне однажды уже
    // перестали реагировать.
    body.addEventListener("mouseover",function(event){
      var node=event.target&&event.target.closest?event.target.closest("[data-stomach-item]"):null;
      if(node) showTooltip(node); else hideTooltip();
    });
    body.addEventListener("mouseout",function(event){
      var node=event.target&&event.target.closest?event.target.closest("[data-stomach-item]"):null;
      if(node) hideTooltip();
    });
    body.addEventListener("contextmenu",function(event){
      var area=event.target&&event.target.closest?event.target.closest("#stomachArea"):null;
      if(!area) return;
      event.preventDefault();
      hideTooltip();
      var node=event.target.closest("[data-stomach-item]");
      if(node){
        // ПКМ по ПОРЦИИ — убрать её из желудка (задано автором).
        send({action:"remove_stomach_portion",itemId:node.getAttribute("data-stomach-item")||""});
        return;
      }
      // ПКМ по ПУСТОМУ месту — список предметов, которые можно употребить.
      showMenuAt(event.clientX,event.clientY);
    });
    body.addEventListener("scroll",function(){hideMenu();hideTooltip();});

    var menu=menuNode();
    if(menu) menu.addEventListener("click",function(event){
      var node=event.target&&event.target.closest?event.target.closest("[data-use-item]"):null;
      if(!node) return;
      send({action:"use_stomach_item",itemId:node.getAttribute("data-use-item")||""});
      hideMenu();
    });

    // ── Окно «Содержимое желудка» ───────────────────────────────────────────
    //
    // Окно статичное (в разметке страницы), поэтому его обработчики можно вешать
    // прямо на него: render() перерисовывает только ТЕЛО монитора, окно лежит
    // вне его. Список внутри обновляется через innerHTML, и ПКМ обрабатывается
    // на самом окне — то есть переживает обновление списка, как и обработчики
    // блока желудка на body.
    var contentsWindow=windowNode();
    if(contentsWindow){
      // Клик по крестику ЗАКРЫВАЕТ окно, а не «переключает»: кнопка закрытия
      // обязана закрывать, иначе она выглядит сломанной.
      contentsWindow.addEventListener("click",function(event){
        var close=event.target&&event.target.closest?event.target.closest("#stomachWindowClose"):null;
        if(close){hideContentsWindow();return;}
      });
      // ПКМ по строке списка — убрать порцию, как ПКМ по плитке (задано автором).
      contentsWindow.addEventListener("contextmenu",function(event){
        var row=event.target&&event.target.closest?event.target.closest("[data-stomach-item]"):null;
        if(!row) return;
        event.preventDefault();
        hideTooltip();
        send({action:"remove_stomach_portion",itemId:row.getAttribute("data-stomach-item")||""});
      });
      dragWindowBy(contentsWindow,document.getElementById("stomachWindowHead"));
    }

    document.addEventListener("click",function(event){
      var menu=menuNode(); if(!menu||menu.hidden) return;
      if(menu.contains(event.target)) return;
      hideMenu();
    });
    global.addEventListener("blur",function(){hideMenu();hideTooltip();});

    if(global.chrome&&global.chrome.webview) global.chrome.webview.addEventListener("message",function(event){try{applyMessage(typeof event.data==="string"?JSON.parse(event.data):event.data);}catch(_){ }});
    global.setInterval(function(){if(simulationRunning&&!simulationPaused) render();},1000);
    send({action:"indicators_ready"});
  }
  /**
   * Escape закрывает СЛОИ по одному: сначала всплывающие окна, и только потом
   * само окно монитора.
   *
   * Раньше Escape сразу просил закрыть монитор. Как только у блока появились свои
   * всплывающие окна, это стало бы ловушкой: чтобы убрать список содержимого,
   * приходилось бы закрыть весь монитор и открыть его заново. Поэтому первый
   * Escape убирает то, что находится ПОВЕРХ, и лишь когда поверх ничего нет —
   * закрывает окно.
   */
  document.addEventListener("keydown",function(event){
    if(event.key!=="Escape") return;
    var contentsWindow=windowNode();
    if(contentsWindow&&!contentsWindow.hidden){ hideContentsWindow(); return; }
    var menu=menuNode();
    if(menu&&!menu.hidden){ hideMenu(); return; }
    hideTooltip();
    send({action:"close_indicators"});
  });
  global.AssistIndicators={render:render,setHighlight:setHighlight,factorsFor:function(key){return dynamicFactors(key).concat(permanentFactors(key));},stateFromRates:stateFromRates};
  if(document.readyState==="loading") document.addEventListener("DOMContentLoaded",init); else init();
})(typeof window!=="undefined"?window:this);
