import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root=process.cwd();
const tmp=fs.mkdtempSync(path.join(os.tmpdir(),"aq-indicators-"));
const web=path.join(tmp,"Web");
fs.mkdirSync(web,{recursive:true});

for(const name of ["theme.css","vitals.js","indicators.js","web_log.js"]){
  const source=path.join(root,"src","AssistQuestEditor.App","Web",name);
  if(fs.existsSync(source)) fs.copyFileSync(source,path.join(web,name));
}

const html=fs.readFileSync(path.join(root,"src","AssistQuestEditor.App","Web","indicators.html"),"utf8")
  .replace(/\?v=[0-9a-f]+/g,"")
  .replace("</head>","<script>window.chrome={webview:{listeners:new Map(),addEventListener(t,h){const a=this.listeners.get(t)||[];a.push(h);this.listeners.set(t,a)},postMessage(m){(window.__sent=window.__sent||[]).push(m)}}}</script></head>");
fs.writeFileSync(path.join(web,"index.html"),html);

const snapshot={
  player:{position:{x:0,y:0,z:0},speedKmh:72},
  playerVitals:{health:8000,maxHealth:10000,energy:5500,maxEnergy:10000,hydration:7000,maxHydration:10000,fatigue:4000,maxFatigue:10000,resilience:6000,metabolism:6000},
  conditions:{stress:300,cumulativeStress:0,cumulativeFatigue:10000,cumulativeEnergy:500,cumulativeHydration:0,
    stomach:{energyRemaining:1500,hydrationRemaining:500,energyPerGameSecond:0.5,hydrationPerGameSecond:0.25},
    lastConsumedItemId:"food.meal",
    effects:[{id:"bull",name:"Бык",remainingRealSeconds:3600,isDebuff:false}]},
  character:{buffs:[],debuffs:[],skills:[{id:"skill.medicine",name:"Фельдшер",level:2}]},
  conditionRates:{health:0,energy:-41.67,hydration:-10.5,fatigue:92.6,stress:0.9,resilience:0,metabolism:-1.3}
};

const {browser}=await openBrowser();
try{
  const page=await browser.newPage({viewport:{width:1000,height:900}});
  const errors=[]; page.on("pageerror",e=>errors.push(String(e)));
  await page.goto("file:///"+path.join(web,"index.html").replace(/\\/g,"/"));
  await page.evaluate(data=>{(window.chrome.webview.listeners.get("message")||[]).forEach(fn=>fn({data:JSON.stringify(data)}));},{
    type:"snapshot",snapshot,conditionRates:snapshot.conditionRates,daylight:{gameClockLabel:"12:00"},
    itemCatalog:[{id:"food.meal",name:"Паёк",description:"Нормальная еда"}],
    simulationRunning:true,simulationPaused:false
  });
  await page.waitForTimeout(80);

  const cardData=await page.$$eval(".indicatorCard",nodes=>nodes.map(node=>({
    key:node.getAttribute("data-scale-key"),
    name:node.querySelector(".indicatorName")?.textContent.trim()||"",
    value:node.querySelector(".indicatorValue")?.textContent.trim()||"",
    dynamic:node.querySelector(".indicatorDynamics")?.textContent.trim()||"",
    factors:[...node.querySelectorAll(".indicatorFact")].map(x=>({text:x.textContent.trim(),cls:x.className}))
  })));

  const failures=[];
  const check=(ok,msg)=>{if(!ok)failures.push(msg)};
  const all=cardData.map(c=>[c.name,c.value,c.dynamic,...c.factors.map(x=>x.text)].join(" ")).join(" ");
  check(errors.length===0,"ошибки страницы: "+errors.join(" | "));
  check(cardData.length===7,"ожидалось 7 шкал, получено "+cardData.length);
  check(!all.includes("Активные эффекты"),"осталась отдельная карточка эффектов");
  check(!/раст[ёе]т|падает/.test(all),"остались старые слова направления");
  const energy=cardData.find(c=>c.key==="energy");
  check(energy&&energy.dynamic.includes("▼"),"нет треугольника падения энергии");
  check(energy&&energy.dynamic.includes("41.7"),"нет скорости энергии");
  check(energy&&energy.value.includes("(")&&energy.value.endsWith("%"),"неверный формат значения энергии");
  check(energy&&energy.factors.some(x=>x.text.includes("Базовый расход энергии")),"нет базового фактора энергии");
  check(energy&&energy.factors.some(x=>x.cls.includes("permanentInactive")),"нет неактивных постоянных факторов");
  const chips=await page.$$eval(".indicatorChip",nodes=>nodes.map(n=>({text:n.textContent.trim(),perk:n.getAttribute("data-perk-link"),item:n.getAttribute("data-item-link")})));
  check(chips.some(x=>x.text==="Бык"&&x.perk),"бафф «Бык» не кликабелен");
  check(chips.some(x=>x.text==="Паёк"&&x.item),"предмет не кликабелен");
  const state=await page.$eval("#characterStateValue",n=>({text:n.textContent.trim(),cls:n.className,title:n.title}));
  check(state.text&&/state[A-Z]/.test(state.cls)&&state.title.includes("/100"),"не рассчитано состояние персонажа");
  const grid=await page.$eval(".indicatorTopRow",n=>getComputedStyle(n).gridTemplateColumns);
  check(String(grid).split(/\s+/).length===3,"верхняя строка не из трёх колонок: "+grid);
  const sent=await page.evaluate(()=>window.__sent||[]);
  check(sent.some(x=>x.action==="indicators_ready"),"нет indicators_ready");

  if(failures.length){console.error("Indicators smoke: FAIL");failures.forEach(x=>console.error("  ✗ "+x));process.exitCode=1;}
  else console.log("Indicators smoke: OK");
}finally{await closeBrowser(browser);}
