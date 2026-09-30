import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import { buildModels, percentiles, stats, chooseModel, selectLow, run } from './observe.mjs';
const root = resolve(import.meta.dirname,'../..');
const {history,daily,runtime}=JSON.parse(readFileSync(resolve(import.meta.dirname,'test-fixture.json')));
test('正式结果原样保留，V7/50实验保存真实参数并避免饱和',()=>{
  const models=buildModels(daily,runtime,history);
  assert.deepEqual(models.ai100.top6,daily.ai_zodiac['100'].top6);
  assert.deepEqual(models.comprehensive.top6,daily.comprehensive_score.map(x=>x.zodiac));
  assert.deepEqual(models.ai50.top6,daily.ai_zodiac['50'].top6);
  assert.equal(models.ai50_unsaturated.configuration.weights.period,.32);
  assert.equal(models.v7_soft.configuration.short_forbidden_multiplier,.75);
  assert.equal(new Set(Object.values(percentiles({a:1,b:2,c:3}))).size,3);
  for (const model of Object.values(models)) assert.equal(new Set(model.top6).size,6);
});
test('阻止目标期和未来数据进入预测',()=>{
  assert.throws(()=>buildModels(daily,runtime,[...history,{issue:daily.issue,special_zodiac:'猪'}]),/目标期/);
});
test('统计仅用已开奖最近50期，共同口径，待开不算失败',()=>{
  const rows=Array.from({length:60},(_,i)=>({issue:i+1,models:{v7:{top3:['虎'],top6:['虎']},ai50:{top3:['猪'],top6:['猪']}}}));
  const settled=Object.fromEntries(rows.slice(0,59).map(x=>[x.issue,{actual_zodiac:'虎'}]));
  assert.equal(stats(rows,'v7',settled).samples,50);
  assert.equal(stats(rows,'v7',settled).top6_rate,1);
  assert.equal(chooseModel(rows,settled).model,'v7');
});
test('低目标选择只看预测期之前，未来开奖不改变选择',()=>{
  const model={top3:['鼠','牛','虎'],top6:['鼠','牛','虎','兔','龙','蛇']};
  const rows=Array.from({length:12},(_,i)=>({issue:i+1,models:{ai100:model}}));
  const settlements=Object.fromEntries(rows.map(x=>[x.issue,{actual_zodiac:'鼠'}]));
  const current={issue:13,models:{ai100:model}};
  const before=selectLow(rows,current,settlements,'ai100');
  const after=selectLow([...rows,{issue:14,models:{ai100:model}}],current,{...settlements,14:{actual_zodiac:'猪'}},'ai100');
  assert.deepEqual(before,after);
  assert.equal(before.configuration.training_issues.length,12);
  assert.equal(before.configuration.objective,'minimize_past_top6_then_top3');
});
test('前瞻冻结不覆盖，开奖只追加结算，正式文件不修改',()=>{
  const temp=mkdtempSync(resolve(tmpdir(),'model-shadow-test-'));
  try {
    mkdirSync(resolve(temp,'site/data/daily-records'),{recursive:true});
    const hpath=resolve(temp,'site/data/history.json'),dpath=resolve(temp,'site/data/daily-records/2026273.json');
    writeFileSync(hpath,JSON.stringify({records:history}));writeFileSync(dpath,JSON.stringify(daily));
    writeFileSync(resolve(temp,'site/data/runtime-state.json'),JSON.stringify({predictions:runtime}));
    const source=readFileSync(dpath,'utf8');
    run(temp,new Date('2026-09-30T01:00:00Z'));
    const fpath=resolve(temp,'experiments/model-shadow-v1/forward.json');
    const first=JSON.parse(readFileSync(fpath));
    run(temp,new Date('2026-09-30T02:00:00Z'));
    assert.deepEqual(JSON.parse(readFileSync(fpath)).records,first.records);
    assert.deepEqual(JSON.parse(readFileSync(fpath)).low_records,first.low_records);
    writeFileSync(hpath,JSON.stringify({records:[...history,{issue:'2026273',special_zodiac:'虎',special_number:'05'}]}));
    run(temp,new Date('2026-09-30T15:00:00Z'));
    const settled=JSON.parse(readFileSync(fpath));
    assert.deepEqual(settled.records,first.records);
    assert.deepEqual(settled.low_records,first.low_records);
    assert.equal(settled.settlements['2026273'].actual_zodiac,'虎');
    assert.equal(readFileSync(dpath,'utf8'),source);
    assert.equal(settled.records.length,1);
  } finally {rmSync(temp,{recursive:true,force:true});}
});
