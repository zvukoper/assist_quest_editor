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
  function isMoving(){ return num(snapshot&&snapshot.player&&snapshot.player.speedKmh)>0.001; }
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
  function valuesFor(key){
    var s=scale(key); if(s&&v()&&v().read) return v().read(snapshot,s);
    var actual=num(vitals()[key]); return {actual:actual,maximum:10000,percent:actual/100,displayPercent:actual/100};
  }
  function rateFor(key){ return num(snapshot&&snapshot.conditionRates&&snapshot.conditionRates[key]); }
  function contribution(rate,key,semanticOverride){
    var values=valuesFor(key), unitsPerHour=rate*60;
    var percentPerHour=values.maximum>0?unitsPerHour/values.maximum*100:0;
    var good=semanticOverride==null?(GOOD_DIRECTION[key]||0):semanticOverride;
    var useful=Math.abs(rate)<FLAT_RATE_EPSILON?null:good!==0&&((rate>0)===(good>0));
    return {unitsPerHour:unitsPerHour,percentPerHour:percentPerHour,useful:useful};
  }
  function contributionHtml(rate,key,semanticOverride){
    var c=contribution(rate,key,semanticOverride);
    var cls=Math.abs(rate)<FLAT_RATE_EPSILON?"factorContribution factorNeutral":c.useful?"factorContribution factorGood":"factorContribution factorBad";
    return "<strong class='"+cls+"'>"+(rate>0?"+":"")+fmt(rate,1)+" ед./мин ("+
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
  function consumedItemFor(key){
    if(key!=="energy"&&key!=="hydration") return null;
    var stomach=conditions().stomach||{};
    var remaining=key==="energy"?num(stomach.energyRemaining):num(stomach.hydrationRemaining);
    if(remaining<=0) return null;
    var id=String(conditions().lastConsumedItemId||""); if(!id) return null;
    var found=(itemCatalog||[]).find(function(e){return String(e.id||"").toLowerCase()===id.toLowerCase();});
    return {id:id,name:(found&&(found.name||found.id))||id,rate:key==="energy"?num(stomach.energyPerGameSecond)*60:num(stomach.hydrationPerGameSecond)*60};
  }
  function chipHtml(entry,isItem){
    if(isItem) return "<button type='button' class='indicatorChip itemChip' data-item-link='"+esc(entry.id)+"' title='Открыть окно «Предметы»'>"+esc(entry.name)+"</button>";
    return "<button type='button' class='indicatorChip "+(entry.kind==="debuff"?"debuffChip":"buffChip")+"' data-perk-link='"+esc(entry.id)+"' data-perk-kind='"+esc(entry.kind)+"' title='Открыть окно «Перки, баффы, скиллы»'>"+esc(entry.name)+"</button>";
  }
  function dynamicChipRows(key){
    var html="";
    activePerkRows(key).forEach(function(entry){
      html+="<div class='indicatorFact dynamicItem'>• "+chipHtml(entry,false)+"</div>";
    });
    var item=consumedItemFor(key);
    if(item) html+="<div class='indicatorFact dynamicItem'>• "+chipHtml(item,true)+" <span class='indicatorItemMeta'>усваивается "+contributionHtml(item.rate,key)+"</span></div>";
    return html;
  }
  function dynamicFactors(key){
    var rate=rateFor(key), result=[];
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
      var maxH=Math.max(1,num(vitals().maxHealth)||10000);
      var hp=num(vitals().health)/maxH*100;
      var ep=num(vitals().energy)/Math.max(1,num(vitals().maxEnergy)||10000)*100;
      var wp=num(vitals().hydration)/Math.max(1,num(vitals().maxHydration)||10000)*100;
      var basePct=hp<100&&ep>35&&wp>35?(ep>70&&wp>70?6:2):0;
      var baseRate=basePct*100/60;
      result.push(factor("Базовое восстановление здоровья",key,baseRate,"permanent",baseRate>FLAT_RATE_EPSILON));
      var stressActive=totalStressPercent()>0;
      result.push(factor("Любой стресс: восстановление ×0,75",key,stressActive?-Math.abs(baseRate)*0.25:0,"permanent",stressActive));
      var cumStress=num(conditions().cumulativeStress)>0;
      result.push(factor("Кумулятивный стресс: дополнительно ×0,5",key,cumStress?-Math.abs(baseRate)*0.25:0,"permanent",cumStress));
      var mp=metabolismPercent();
      result.push(factor("Метаболизм ниже 45%: восстановление ×0,75",key,mp<45?-Math.abs(baseRate)*0.25:0,"permanent",mp<45));
      result.push(factor("Метаболизм ≥75%: восстановление ×1,5",key,mp>=75?Math.abs(baseRate)*0.5:0,"permanent",mp>=75));
      return result;
    }
    if(key==="energy"||key==="hydration"){
      var normalBase=key==="energy"?100/30:100/90;
      var m=metabolismPercent(), mf=m<45?0.9:m>=75?1.25:1;
      var base=-normalBase*mf*100/60;
      result.push(factor(key==="energy"?"Базовый расход энергии в Нормальном состоянии":"Базовый расход жидкости в Нормальном состоянии",key,base,"permanent",true,-1));
      var needs=num(vitals().health)<num(vitals().maxHealth);
      result.push(factor(key==="energy"?"Восстановление здоровья: 1:1 по энергии":"Восстановление здоровья: 1:2 по жидкости",key,needs?rate*0.15:0,"permanent",needs,-1));
      result.push(factor("Метаболизм <45%: расход ×0,9",key,m<45?Math.abs(base)*0.1:0,"permanent",m<45,-1));
      result.push(factor("Метаболизм ≥75%: расход ×1,25",key,m>=75?-Math.abs(base)*0.25:0,"permanent",m>=75,-1));
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
      return result;
    }
    if(key==="resilience"){
      var rp=resiliencePercent(), target=hasEffect("bum")?50:60;
      var delta=target>rp?Math.min(0.8333,(target-rp)/15):-Math.min(0.8333,(rp-target)/15);
      result.push(factor("Возврат к номиналу 60%",key,hasEffect("bum")?0:delta,"permanent",!hasEffect("bum")&&Math.abs(delta)>FLAT_RATE_EPSILON));
      result.push(factor("Дебафф «Бомж» ограничивает устойчивость 50%",key,hasEffect("bum")?rate:0,"permanent",hasEffect("bum"),-1));
      return result;
    }
    if(key==="metabolism"){
      var mp2=metabolismPercent();
      result.push(factor("Возврат к номиналу 60%",key,(60-mp2)*100/60/60,"permanent",Math.abs(60-mp2)>0));
      [["cumulativeStress","Истощение стресса"],["cumulativeFatigue","Истощение усталости"],["cumulativeHydration","Истощение жидкости"],["cumulativeEnergy","Истощение энергии"]].forEach(function(entry){
        var active=num(conditions()[entry[0]])>0;
        result.push(factor(entry[1]+": −2% за 15 игровых минут",key,active?-200/60:0,"permanent",active,1));
      });
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
    if(!Number.isFinite(rate)||Math.abs(rate)<FLAT_RATE_EPSILON) return "<span class='trendFlat'>— 0 ед./мин (0 ед./час)</span>";
    var rising=rate>0, good=GOOD_DIRECTION[key]||0, positive=good!==0&&rising===(good>0);
    return "<span class='"+(positive?"trendGood":"trendBad")+"'>"+(rising?"▲":"▼")+" "+(rate>0?"+":"")+fmt(rate,1)+" ед./мин ("+(rate*60>0?"+":"")+fmt(rate*60,1)+" ед./час)</span>";
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
    var permanent=permanentFactors(key), active=permanent.filter(function(x){return x.active;}), inactive=permanent.filter(function(x){return !x.active;});
    var dynamic=dynamicFactors(key), highlighted=key===highlightKey?" indicatorHighlight":"";
    return "<section class='indicatorCard"+highlighted+"' data-scale-key='"+esc(key)+"'>"+
      "<div class='indicatorTopRow'><div class='indicatorName'>"+esc(s.label)+"</div>"+
      "<div class='indicatorDynamics'>"+headerDynamics(key)+"</div>"+
      "<div class='indicatorValue'><span class='valueReal'>("+current+" / "+maximum+")</span> <span class='valuePercent'>"+display+"%</span></div></div>"+
      "<div class='indicatorFactors'>"+dynamicChipRows(key)+
      dynamic.map(function(x){return renderDynamicFactor(x,key);}).join("")+
      active.map(function(x){return renderFactor(x,key);}).join("")+
      inactive.map(function(x){return renderFactor(x,key);}).join("")+
      "</div></section>";
  }
  function render(){
    if(!body||!snapshot||!v()) return;
    var markup=(v().SCALES||[]).map(function(s){return renderCard(s.key);}).join("");
    if(markup!==lastMarkup){
      lastMarkup=markup; body.innerHTML=markup;
      if(highlightKey) scrollToHighlight();
    }
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
      if(Array.isArray(message.itemCatalog)) itemCatalog=message.itemCatalog;
      if(snapshot&&message.conditionRates) snapshot.conditionRates=message.conditionRates;
      if(message.daylight) snapshot.daylight=message.daylight;
      simulationRunning=!!message.simulationRunning; simulationPaused=!!message.simulationPaused;
    } else if(message.type==="live_state"){
      if(!snapshot) snapshot={};
      if(message.player) snapshot.player=message.player;
      if(message.playerVitals) snapshot.playerVitals=message.playerVitals;
      if(message.conditions) snapshot.conditions=message.conditions;
      if(message.character) snapshot.character=message.character;
      if(message.conditionRates) snapshot.conditionRates=message.conditionRates;
      if(message.daylight) snapshot.daylight=message.daylight;
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
      if(item) send({action:"open_items",itemId:item.getAttribute("data-item-link")||""});
    });
    if(global.chrome&&global.chrome.webview) global.chrome.webview.addEventListener("message",function(event){try{applyMessage(typeof event.data==="string"?JSON.parse(event.data):event.data);}catch(_){ }});
    global.setInterval(function(){if(simulationRunning&&!simulationPaused) render();},1000);
    send({action:"indicators_ready"});
  }
  document.addEventListener("keydown",function(event){if(event.key==="Escape")send({action:"close_indicators"});});
  global.AssistIndicators={render:render,setHighlight:setHighlight,factorsFor:function(key){return dynamicFactors(key).concat(permanentFactors(key));},stateFromRates:stateFromRates};
  if(document.readyState==="loading") document.addEventListener("DOMContentLoaded",init); else init();
})(typeof window!=="undefined"?window:this);
