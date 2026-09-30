import { readFileSync, writeFileSync, mkdirSync, renameSync, existsSync, readdirSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

export const VERSION = 'model-shadow-v1';
export const LABELS = {
  v7: 'V7原模型', ai50: '50期旧模型', ai100: '100期模型（保持原算法）', comprehensive: '综合评分（保持原算法）',
  v7_soft: 'V7软惩罚', v7_none: 'V7取消禁选', v7_normalized: 'V7统一量纲+软惩罚',
  ai50_unsaturated: '50期去饱和', ai50_shrunk: '50期去饱和+周期收缩', selector: '开奖前动态选择（V7/50期）', low_ai100: '100期低命中目标旁路', low_comprehensive: '综合评分低命中目标旁路',
};
const WEIGHTS = { frequency: .16, trend: .16, omission: .20, hot_cold: .16, period: .32, consecutive: 0 };
const parse = (text, fallback = []) => { try { return JSON.parse(text || ''); } catch { return fallback; } };
const mean = a => a.length ? a.reduce((s, x) => s + x, 0) / a.length : 0;
export function percentiles(values) {
  const items = Object.entries(values);
  return Object.fromEntries(items.map(([key, value]) => [key, items.length < 2 ? .5 :
    (items.filter(([, x]) => x < value - 1e-12).length + (items.filter(([, x]) => Math.abs(x - value) <= 1e-12).length - 1) / 2) / (items.length - 1)]));
}
function ranked(scores, issue, config) {
  const tie = z => createHash('sha256').update(`${VERSION}|${config}|${issue}|${z}`).digest('hex');
  const ranking = Object.keys(scores).sort((a, b) => scores[b] - scores[a] || tie(a).localeCompare(tie(b)));
  return { top3: ranking.slice(0, 3), top6: ranking.slice(0, 6), full_ranking: ranking, scores };
}
function baseline(top3, top6, extra = {}) {
  if (!Array.isArray(top3) || top3.length !== 3 || !Array.isArray(top6) || top6.length !== 6) return null;
  if (new Set(top6).size !== 6 || top3.some(z => !top6.includes(z))) throw new Error('不完整或不一致的预测');
  return { top3: [...top3], top6: [...top6], ...extra };
}
export function buildModels(daily, runtime, prefix) {
  const issue = Number(daily.issue);
  if (!prefix.length || prefix.some(x => Number(x.issue) >= issue)) throw new Error('历史前缀含目标期或未来期');
  const models = {};
  const ai = daily.ai_zodiac || {};
  for (const [key, id] of [['50', 'ai50'], ['100', 'ai100']]) {
    const record = baseline(ai[key]?.top3, ai[key]?.top6, {
      source: 'frozen_formal_snapshot', original_weight_snapshot: ai[key]?.weight_snapshot_json || '',
      factor_scores: ai[key]?.factor_scores || {}, ranking: ai[key]?.ranking || [],
    });
    if (record) models[id] = record;
  }
  const comprehensive = (daily.comprehensive_score || []).map(x => x.zodiac);
  if (comprehensive.length >= 6) models.comprehensive = baseline(comprehensive.slice(0, 3), comprehensive.slice(0, 6));
  const v7 = runtime.find(x => Number(x.issue) === issue && x.modelVersion === 'V7' && x.analysisPeriods === 7000);
  if (v7) {
    const formal = baseline(v7.predictZodiac?.split(','), v7.top6Zodiac?.split(','));
    if (formal) models.v7 = { ...formal, source: 'frozen_formal_snapshot', feature_snapshot: parse(v7.featureSnapshotJson) };
    const features = parse(v7.featureSnapshotJson);
    if (features.length === 12) {
      const frequency = Object.fromEntries(features.map(x => [x.Zodiac, x.Recent10Count + x.Recent20Count * .5 + x.Recent50Count * .2]));
      const omission = Object.fromEntries(features.map(x => [x.Zodiac, Math.min(x.CurrentOmission, x.AverageOmission * 2 + 1)]));
      const repeat = Object.fromEntries(features.map(x => [x.Zodiac, x.ShortCycleRepeatCount]));
      const pf = percentiles(frequency), po = percentiles(omission), pr = percentiles(repeat);
      for (const config of ['v7_soft', 'v7_none', 'v7_normalized']) {
        const scores = Object.fromEntries(features.map(x => {
          const raw = .55 * frequency[x.Zodiac] + .45 * omission[x.Zodiac] + .1 * repeat[x.Zodiac];
          const value = config === 'v7_normalized' ? .55 * pf[x.Zodiac] + .45 * po[x.Zodiac] + .1 * pr[x.Zodiac] : raw;
          return [x.Zodiac, value * (config !== 'v7_none' && x.ShortForbidden ? .75 : 1)];
        }));
        models[config] = { ...ranked(scores, issue, config), configuration: {
          frequency_weight: .55, omission_weight: .45, repeat_weight: .1,
          short_forbidden_multiplier: config === 'v7_none' ? 1 : .75,
          scale: config === 'v7_normalized' ? 'midrank_percentile' : 'original_units',
          feature_source: 'frozen_V7_feature_snapshot', score_type: 'ranking_score_not_probability',
        } };
      }
    }
  }
  const features = parse(ai['50']?.feature_snapshot_json);
  if (features.length === 12 && ai['50']?.ranking?.length === 12) {
    const slice = prefix.slice(-50), seq = slice.map(x => x.special_zodiac);
    const n = slice.length;
    const frequency = Object.fromEntries(features.map(x => [x.Zodiac, x.TotalAppear / n]));
    const trend = Object.fromEntries(features.map(x => [x.Zodiac,
      .5 * x.Appear10 / Math.min(10, n) + .3 * x.Appear30 / Math.min(30, n) + .2 * x.Appear50 / Math.min(50, n)]));
    const pf = percentiles(frequency), pt = percentiles(trend);
    const totals = Object.fromEntries(ai['50'].ranking.map(x => [x.zodiac, x.total_score]));
    for (const config of ['ai50_unsaturated', 'ai50_shrunk']) {
      const scores = {}, adjustments = {};
      for (const f of features) {
        const gaps = []; let previous = -1;
        seq.forEach((z, i) => { if (z === f.Zodiac) { if (previous >= 0) gaps.push(i - previous); previous = i; } });
        const reliability = gaps.length / (gaps.length + 5);
        const period = config === 'ai50_shrunk' ? 50 + reliability * (f.PeriodPatternScore - 50) : f.PeriodPatternScore;
        const delta = WEIGHTS.frequency * (100 * pf[f.Zodiac] - f.FrequencyScore) +
          WEIGHTS.trend * (100 * pt[f.Zodiac] - f.RecentTrendScore) + WEIGHTS.period * (period - f.PeriodPatternScore);
        scores[f.Zodiac] = totals[f.Zodiac] + delta;
        adjustments[f.Zodiac] = { frequency: 100 * pf[f.Zodiac], trend: 100 * pt[f.Zodiac], period, delta, period_gap_samples: gaps.length };
      }
      models[config] = { ...ranked(scores, issue, config), adjustments, configuration: {
        weights: WEIGHTS, calibration: 'preserve_frozen_formal_total_then_apply_factor_delta',
        frequency_trend_transform: 'midrank_percentile_no_saturation', cycle_shrink_prior: 50,
        cycle_shrink_samples: config === 'ai50_shrunk' ? 5 : 0,
        original_weight_snapshot: ai['50'].weight_snapshot_json || '',
        weight_provenance: 'fixed_period50_weights_from_V65ExperimentPipeline',
        score_type: 'ranking_score_not_probability',
      } };
    }
  }
  return models;
}
export function stats(records, model, settlements, limit = 50) {
  const settled = records.filter(r => r.models[model] && settlements[String(r.issue)]).sort((a,b) => a.issue-b.issue).slice(-limit);
  let hits3 = 0, hits6 = 0, streak = 0, maximum = 0;
  for (const row of settled) {
    const actual = settlements[String(row.issue)].actual_zodiac;
    hits3 += Number(row.models[model].top3.includes(actual));
    const hit = row.models[model].top6.includes(actual); hits6 += Number(hit);
    streak = hit ? 0 : streak + 1; maximum = Math.max(maximum, streak);
  }
  const n = settled.length, p = n ? hits6/n : 0, z = 1.96;
  const center = n ? (p + z*z/(2*n))/(1+z*z/n) : null;
  const width = n ? z*Math.sqrt(p*(1-p)/n+z*z/(4*n*n))/(1+z*z/n) : null;
  return { samples: n, hits3, hits6, top3_rate: n ? hits3/n : null, top6_rate: n ? p : null,
    top6_wilson95: n ? [center-width, center+width] : null, max_top6_misses: maximum, issues: settled.map(x=>x.issue) };
}
export function chooseModel(records, settlements) {
  const common = records.filter(x => x.models.v7 && x.models.ai50 && settlements[String(x.issue)]).sort((a,b)=>a.issue-b.issue).slice(-50);
  const candidates = ['v7', 'ai50'].map(key => ({ key, ...stats(common, key, settlements) }));
  candidates.sort((a,b)=>(b.top6_rate ?? -1)-(a.top6_rate ?? -1) || (b.top3_rate ?? -1)-(a.top3_rate ?? -1) || a.key.localeCompare(b.key));
  return { model: candidates[0].key, statistics: candidates, method: 'same_settled_issues_last50_top6_then_top3_then_key' };
}
// Fixed, auditable search space. Selection only sees draws prior to the target.
export function lowCandidates(model, issue) {
  const order = ['鼠','牛','虎','兔','龙','蛇','马','羊','猴','鸡','狗','猪'];
  const saved = model.ranking?.map(x=>x.zodiac) || [];
  const base = saved.length === 12 ? saved : [...model.top6, ...order.filter(x=>!model.top6.includes(x)).sort((a,b)=>
    createHash('sha256').update(VERSION+'|low-tie|'+a).digest('hex').localeCompare(createHash('sha256').update(VERSION+'|low-tie|'+b).digest('hex')))];
  if (base.length !== 12 || new Set(base).size !== 12) throw new Error('低命中实验需要12生肖候选集');
  const candidates = { original: baseline(model.top3,model.top6) };
  for (const shift of [3,6,9]) {
    const ranking = [...base.slice(shift),...base.slice(0,shift)];
    candidates['shift'+shift] = baseline(ranking.slice(0,3),ranking.slice(0,6));
  }
  const reversed = [...base].reverse();
  candidates.reverse = baseline(reversed.slice(0,3),reversed.slice(0,6));
  return candidates;
}
export function selectLow(records, current, settlements, baseKey) {
  const candidates = lowCandidates(current.models[baseKey],current.issue);
  const training = records.filter(x=>x.issue<current.issue && x.models[baseKey] && settlements[String(x.issue)]).sort((a,b)=>a.issue-b.issue).slice(-50);
  const measurements = Object.keys(candidates).map(key=>{
    const rows = training.map(row=>({issue:row.issue,models:{candidate:lowCandidates(row.models[baseKey],row.issue)[key]}}));
    return {key,...stats(rows,'candidate',settlements)};
  });
  measurements.sort((a,b)=>(a.top6_rate??1)-(b.top6_rate??1) || (a.top3_rate??1)-(b.top3_rate??1) || a.key.localeCompare(b.key));
  const selected = training.length >= 10 ? measurements[0].key : 'original';
  return {...candidates[selected],configuration:{objective:'minimize_past_top6_then_top3',selected_rule:selected,
    candidate_rules:Object.keys(candidates),training_issues:training.map(x=>x.issue),training_statistics:measurements,
    minimum_training_samples:10,rule_ties:'lexicographic',warning:'historical_selection_may_overfit; validate_only_with_frozen_forward_results',
    comprehensive_unranked_tail:'fixed_hash_tie_break_not_formal_scores'}};
}
const read = path => JSON.parse(readFileSync(path, 'utf8'));
function atomic(path, value) { mkdirSync(dirname(path), {recursive:true}); const tmp = path+'.tmp'; writeFileSync(tmp, JSON.stringify(value, null, 2)+'\n'); renameSync(tmp,path); }
export function run(root, now = new Date()) {
  const directory = resolve(root, 'experiments/model-shadow-v1');
  const history = read(resolve(root,'site/data/history.json')).records.filter(x => x.special_zodiac).sort((a,b)=>Number(a.issue)-Number(b.issue));
  if (!history.length || new Set(history.map(x=>String(x.issue))).size !== history.length) throw new Error('开奖历史为空或期号重复');
  const latest = Number(history.at(-1).issue);
  const runtime = read(resolve(root,'site/data/runtime-state.json')).predictions;
  const codeHash = createHash('sha256').update(readFileSync(fileURLToPath(import.meta.url))).digest('hex');
  const path = resolve(directory, 'forward.json');
  const archive = existsSync(path) ? read(path) : { version: VERSION, started_at: now.toISOString(), records: [], settlements: {} };
  if (archive.version !== VERSION) throw new Error('观察档案版本不同，禁止混写');
  for (const record of archive.records) {
    const draw = history.find(x=>Number(x.issue)===record.issue);
    if (draw && !archive.settlements[String(record.issue)]) archive.settlements[String(record.issue)] = {
      actual_zodiac: draw.special_zodiac, actual_number: draw.special_number, settled_at: now.toISOString(),
    };
    if (draw && archive.settlements[String(record.issue)].actual_zodiac !== draw.special_zodiac) throw new Error('已结算开奖有冲突，需要人工检查');
  }
  const dailyDir = resolve(root,'site/data/daily-records');
  const replay = [], formal = [];
  archive.low_records ??= [];
  const allSettlements = Object.fromEntries(history.map(x=>[String(x.issue),{actual_zodiac:x.special_zodiac}]));
  for (const file of readdirSync(dailyDir).filter(x=>/^\d+\.json$/.test(x)).sort()) {
    const daily = read(resolve(dailyDir,file)), issue = Number(daily.issue);
    const prefix = history.filter(x=>Number(x.issue)<issue);
    if (!prefix.length || Number(daily.source_issue)!==Number(prefix.at(-1).issue)) continue;
    const models = buildModels(daily,runtime,prefix);
    const row = { issue, models };
    for (const [key,baseKey] of [['low_ai100','ai100'],['low_comprehensive','comprehensive']]) {
      if (models[baseKey]) models[key]=selectLow(replay,row,allSettlements,baseKey);
    }
    if (issue <= latest) { replay.push(row); formal.push(row); }
    if (issue > latest && (issue === latest+1 || (issue%1000===1 && Math.floor(issue/1000)===Math.floor(latest/1000)+1)) && !archive.low_records.some(x=>x.issue===issue)) {
      const low = Object.fromEntries(['low_ai100','low_comprehensive'].filter(key=>models[key]).map(key=>[key,models[key]]));
      if (Object.keys(low).length === 2) archive.low_records.push({issue,created_at:now.toISOString(),source_issue:latest,code_sha256:codeHash,models:low});
    }
    // Only next available issue; no post-draw backfill, no overwrite, no speculative future chains.
    if (issue > latest && Number(prefix.at(-1).issue) === latest && !archive.records.some(x=>x.issue===issue)) {
      if (issue !== latest+1 && !(issue%1000===1 && Math.floor(issue/1000)===Math.floor(latest/1000)+1)) continue;
      if (!models.v7 || !models.ai50) continue;
      const selection = chooseModel(formal, Object.fromEntries(history.map(x=>[String(x.issue),{actual_zodiac:x.special_zodiac}])));
      models.selector = { ...baseline(models[selection.model].top3,models[selection.model].top6), chosen_model: selection.model, selection };
      archive.records.push({ issue, created_at: now.toISOString(), source_issue: latest, code_sha256: codeHash,
        source_sha256: createHash('sha256').update(readFileSync(resolve(dailyDir,file))).digest('hex'),
        history_prefix_sha256: createHash('sha256').update(JSON.stringify(prefix)).digest('hex'), models });
    }
  }
  atomic(path,archive);
  const replaySettlements = Object.fromEntries(history.map(x=>[String(x.issue), {actual_zodiac:x.special_zodiac}]));
  const summarize = (records, settlements) => {
    const keys = Object.keys(LABELS).filter(key=>records.some(x=>x.models[key]));
    const common = records.filter(x=>keys.every(key=>x.models[key]) && settlements[String(x.issue)]).sort((a,b)=>a.issue-b.issue).slice(-50);
    return { per_model: Object.fromEntries(keys.map(key=>[key,stats(records,key,settlements)])),
      common_issue_comparison: Object.fromEntries(keys.map(key=>[key,stats(common,key,settlements)])), common_issues: common.map(x=>x.issue) };
  };
  const combined = archive.records.map(row=>({...row,models:{...row.models,...(archive.low_records.find(x=>x.issue===row.issue)?.models || {})}}));
  const report = { version: VERSION, updated_at: now.toISOString(), latest_draw: latest, forward: summarize(combined,archive.settlements),
    retrospective_replay: { ...summarize(replay,replaySettlements), warning: '历史回放，仅用于诊断；不计入前瞻命中率，不宣称校准器重新训练回测。基线为已保存正式结果。' },
    latest_prediction: combined.at(-1) || null, labels: LABELS };
  atomic(resolve(directory,'report.json'),report);
  const format = x=>x===null?'—':(100*x).toFixed(1)+'%';
  const table = obj=>['|模型|已开奖|前3|前6|前6 95%区间|最长连不中|','|---|---:|---:|---:|---|---:|',
    ...Object.entries(obj).map(([key,s])=>`|${LABELS[key]}|${s.samples}|${format(s.top3_rate)}|${format(s.top6_rate)}|${s.top6_wilson95?.map(format).join('–') || '—'}|${s.max_top6_misses}|`)].join('\n');
  const latestRow=report.latest_prediction;
  writeFileSync(resolve(directory,'REPORT.md'),`# 模型旁路观察\n\n更新：${report.updated_at}；最新开奖：${latest}。\n\n100期和综合评分原结果保持原样，另设低命中目标实验，按过去最多50期选择最低命中候选规则，真实效果只看前瞻记录。实验不会写入正式模型、数据库或正式预测目录。分数只代表排序，不代表概率。\n\n## 前瞻观察（真实开奖前冻结）\n\n${table(report.forward.common_issue_comparison)}\n\n共同期号：${report.forward.common_issues.join('、') || '暂无，等待开奖'}。只统计已开奖，最多50期。\n\n## 当前冻结预测\n\n${latestRow ? `期号：${latestRow.issue}；冻结时间：${latestRow.created_at}；数据截止：${latestRow.source_issue}。\n\n|模型|前3|前6|\n|---|---|---|\n`+Object.entries(latestRow.models).map(([key,m])=>`|${LABELS[key]}|${m.top3.join(' ')}|${m.top6.join(' ')}|`).join('\n') : '无可用待开奖正式快照，不补造前瞻记录。'}\n\n## 历史诊断（不计入前瞻）\n\n${report.retrospective_replay.warning}\n\n${table(report.retrospective_replay.common_issue_comparison)}\n\n共同期号：${report.retrospective_replay.common_issues.join('、')}。\n\n[配置说明](README.md) · [冻结记录](forward.json) · [完整统计](report.json)\n`);
  return report;
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
  const report=run(root); console.log(`旁路已更新：开奖${report.latest_draw}，冻结${report.latest_prediction?.issue || '无'}，已结算共同样本${report.forward.common_issues.length}`);
}
