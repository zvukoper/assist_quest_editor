import fs from "node:fs";
import path from "node:path";
const root=process.cwd();
const read=file=>fs.readFileSync(path.join(root,file),"utf8");
const models=read("src/AssistQuestEditor.Domain/DynamicEventModels.cs");
const dispatcher=read("src/AssistQuestEditor.Domain/DynamicEventDispatcher.cs");
const tests=read("tests/AssistQuestEditor.Domain.Tests/DynamicEventDispatcherTests.cs");
const checks=[
 [models.includes("DynamicEventCompletionDefinition"),"нет Completion-модели"],
 [models.includes("CompleteOnDiscovery"),"нет автоматического завершения"],
 [models.includes("int Money"),"нет денежной награды"],
 [models.includes("int Experience"),"нет награды опытом"],
 [dispatcher.includes("ApplyCompletionRewards"),"Dispatcher не начисляет награды"],
 [dispatcher.includes("DynamicEventRewardGranted"),"нет события выдачи награды"],
 [dispatcher.includes("DynamicEventCompleted"),"нет события завершения"],
 [dispatcher.includes("Complete(instanceId, consumed: true)"),"тайник не деактивируется после триггера"],
 [tests.includes("TestDynamicCacheAutoCompletesOnTriggerAndGrantsRewards"),"нет C# теста test_dynamic_cache"]
];
const failures=checks.filter(x=>!x[0]).map(x=>x[1]);
if(failures.length){console.error("test_dynamic_cache: FAIL");failures.forEach(x=>console.error("  ✗ "+x));process.exitCode=1;}
else console.log("test_dynamic_cache: OK");
