const state = document.querySelector('#state');
const content = document.querySelector('#content');
const refreshButton = document.querySelector('#refresh');
const scoreBody = document.querySelector('#score-body');
const detailPanel = document.querySelector('#detail-panel');
const detailTitle = document.querySelector('#detail-title');
const detailNote = document.querySelector('#detail-note');
const detailBody = document.querySelector('#detail-body');
const closeDetail = document.querySelector('#close-detail');
const cloudApi = 'https://smart-ledger-2026.ntr133.chatgpt.site/api/v7-sync';

const expectedModels = ['ai:regularity50', 'ai:period50_fair'];
const preferredOrder = [
  'ai:regularity50', 'ai:period50_fair',
  'ai:auto', 'ai:100', 'ai:50', 'ai:all',
  'special_rule', 'comprehensive_score', 'ensemble'
];

let modelRows = new Map();

async function fetchJson(path) {
  const separator = path.includes('?') ? '&' : '?';
  const response = await fetch(`${path}${separator}v=${Date.now()}`, { cache: 'no-store' });
  if (!response.ok) throw new Error(`${path} HTTP ${response.status}`);
  return response.json();
}

async function loadRecentPredictions() {
  try {
    const manifest = await fetchJson(`${cloudApi}/manifest`);
    const records = Array.isArray(manifest.records) ? manifest.records : [];
    const files = records.slice(-30);
    if (!files.length) throw new Error('云端暂无预测历史');
    const predictions = await Promise.all(files.map(file =>
      fetchJson(`${cloudApi}/prediction?file=${encodeURIComponent(file)}`)));
    return predictions.filter(x => x?.status === 'success');
  } catch (cloudError) {
    console.warn('云端30期读取失败，改读站点历史快照。', cloudError);
    const history = await fetchJson('data/daily-records/history.json');
    const predictions = Array.isArray(history.predictions) ? history.predictions : [];
    return predictions.slice(-30).filter(x => x?.status === 'success');
  }
}

function labelFor(key) {
  const labels = {
    'ai:regularity50': 'Regularity50',
    'ai:period50_fair': 'Period50-Fair',
    'ai:auto': '自动学习',
    'ai:100': '100期旧模型',
    'ai:50': '50期旧模型',
    'ai:all': '长期旧模型',
    'special_rule': '特码规律',
    'comprehensive_score': '综合评分',
    'ensemble': '集成排序'
  };
  if (labels[key]) return labels[key];
  if (key.startsWith('ai:')) return `AI ${key.slice(3)}`;
  return key;
}

function categoryFor(key) {
  if (key === 'ai:regularity50' || key === 'ai:period50_fair') return { text: 'Forward观察', cls: 'forward' };
  if (key.startsWith('ai:')) return { text: '旧模型对照', cls: 'legacy' };
  return { text: '规则/综合', cls: 'rule' };
}

function asArray(value) { return Array.isArray(value) ? value.filter(Boolean) : []; }

function addEntry(map, key, entry) {
  if (!map.has(key)) map.set(key, []);
  map.get(key).push(entry);
}

function normalizePredictions(predictions) {
  const map = new Map();
  const ordered = [...predictions].sort((a, b) => Number(a.issue) - Number(b.issue));

  for (const prediction of ordered) {
    const actual = prediction.verification?.actual_zodiac || null;
    const verified = prediction.verification?.status === 'verified' && !!actual;
    const generatedAt = prediction.generated_at || '';

    for (const [name, item] of Object.entries(prediction.ai_zodiac || {})) {
      const top3 = asArray(item?.top3);
      const top6 = asArray(item?.top6);
      const fullRanking = asArray(item?.full_ranking);
      const modelActual = actual || item?.actual_zodiac || null;
      const modelVerified = verified || typeof item?.top6_hit === 'boolean';
      addEntry(map, `ai:${name}`, {
        issue: prediction.issue, generatedAt, top3, top6, actual: modelActual,
        verified: modelVerified,
        top3Hit: typeof item?.top3_hit === 'boolean' ? item.top3_hit : (modelVerified && modelActual ? top3.includes(modelActual) : null),
        top6Hit: typeof item?.top6_hit === 'boolean' ? item.top6_hit : (modelVerified && modelActual ? top6.includes(modelActual) : null),
        actualRank: modelActual && fullRanking.includes(modelActual) ? fullRanking.indexOf(modelActual) + 1 :
          modelActual && top6.includes(modelActual) ? top6.indexOf(modelActual) + 1 : null
      });
    }

    if (prediction.special_rule) {
      const top6 = asArray(prediction.special_rule.zodiacs);
      addEntry(map, 'special_rule', {
        issue: prediction.issue, generatedAt, top3: [], top6, actual, verified,
        top3Hit: null,
        top6Hit: typeof prediction.special_rule.hit === 'boolean' ? prediction.special_rule.hit :
          (verified ? top6.includes(actual) : null),
        actualRank: actual && top6.includes(actual) ? top6.indexOf(actual) + 1 : null
      });
    }

    for (const key of ['comprehensive_score', 'ensemble']) {
      const ranking = asArray(prediction[key]).map(x => x?.zodiac).filter(Boolean);
      if (!ranking.length) continue;
      const top3 = ranking.slice(0, 3);
      const top6 = ranking.slice(0, 6);
      addEntry(map, key, {
        issue: prediction.issue, generatedAt, top3, top6, actual, verified,
        top3Hit: verified ? top3.includes(actual) : null,
        top6Hit: verified ? top6.includes(actual) : null,
        actualRank: actual && ranking.includes(actual) ? ranking.indexOf(actual) + 1 : null
      });
    }
  }

  for (const key of expectedModels) if (!map.has(key)) map.set(key, []);
  return map;
}

function missStats(entries) {
  const verified = entries.filter(x => x.verified && typeof x.top6Hit === 'boolean');
  let maxMiss = 0, current = 0;
  for (const row of verified) {
    if (row.top6Hit) current = 0;
    else { current += 1; maxMiss = Math.max(maxMiss, current); }
  }
  let currentMiss = 0;
  for (let i = verified.length - 1; i >= 0 && !verified[i].top6Hit; i--) currentMiss++;
  return { maxMiss, currentMiss };
}

function rate(entries, key) {
  const eligible = entries.filter(x => x.verified && typeof x[key] === 'boolean');
  if (!eligible.length) return null;
  return eligible.filter(x => x[key]).length / eligible.length;
}

function rateText(value) { return value == null ? '—' : `${(value * 100).toFixed(1)}%`; }
function rateClass(value) {
  if (value == null) return '';
  if (value >= .55) return 'rate-good';
  if (value >= .50) return 'rate-mid';
  return 'rate-bad';
}
function zodiacText(values) { return values?.length ? values.join(' ') : '—'; }

function makeCell(text, className = '') {
  const td = document.createElement('td');
  td.textContent = text;
  if (className) td.className = className;
  return td;
}

function renderScoreboard() {
  scoreBody.replaceChildren();
  const keys = [...modelRows.keys()].sort((a, b) => {
    const ai = preferredOrder.indexOf(a), bi = preferredOrder.indexOf(b);
    if (ai >= 0 || bi >= 0) return (ai < 0 ? 999 : ai) - (bi < 0 ? 999 : bi);
    return a.localeCompare(b, 'zh-CN');
  });

  for (const key of keys) {
    const entries = modelRows.get(key) || [];
    const verifiedSamples = entries.filter(x => x.verified && typeof x.top6Hit === 'boolean').length;
    const latest = entries.length ? entries[entries.length - 1] : null;
    const top3Rate = rate(entries, 'top3Hit');
    const top6Rate = rate(entries, 'top6Hit');
    const { maxMiss, currentMiss } = missStats(entries);
    const category = categoryFor(key);

    const tr = document.createElement('tr');
    tr.append(makeCell(labelFor(key), 'model-cell'));
    const statusCell = document.createElement('td');
    const tag = document.createElement('span');
    tag.className = `tag ${category.cls}`;
    tag.textContent = category.text;
    statusCell.append(tag);
    tr.append(statusCell);
    tr.append(makeCell(String(verifiedSamples)));
    tr.append(makeCell(rateText(top3Rate), rateClass(top3Rate)));
    tr.append(makeCell(rateText(top6Rate), rateClass(top6Rate)));
    tr.append(makeCell(verifiedSamples ? String(maxMiss) : '—'));
    tr.append(makeCell(verifiedSamples ? String(currentMiss) : '—'));
    tr.append(makeCell(zodiacText(latest?.top3), 'zodiac-list'));
    tr.append(makeCell(zodiacText(latest?.top6), 'zodiac-list'));
    const detailCell = document.createElement('td');
    const button = document.createElement('button');
    button.className = 'detail-button';
    button.type = 'button';
    button.textContent = '查看30期';
    button.disabled = entries.length === 0;
    button.addEventListener('click', () => showDetails(key));
    detailCell.append(button);
    tr.append(detailCell);
    scoreBody.append(tr);
  }

  document.querySelector('#model-count').textContent = String(keys.length);
}

function resultText(hit) {
  if (hit === true) return ['命中', 'hit'];
  if (hit === false) return ['未中', 'miss'];
  return ['待开奖', 'pending'];
}

function showDetails(key) {
  const entries = [...(modelRows.get(key) || [])].sort((a, b) => Number(b.issue) - Number(a.issue)).slice(0, 30);
  detailTitle.textContent = `${labelFor(key)} · 最近30期明细`;
  detailNote.textContent = categoryFor(key).text;
  detailBody.replaceChildren();

  for (const row of entries) {
    const tr = document.createElement('tr');
    tr.append(makeCell(String(row.issue)));
    tr.append(makeCell(zodiacText(row.top3), 'zodiac-list'));
    tr.append(makeCell(zodiacText(row.top6), 'zodiac-list'));
    tr.append(makeCell(row.verified ? (row.actual || '—') : '未开奖'));
    tr.append(makeCell(row.verified ? (row.actualRank ? String(row.actualRank) : '>6') : '—'));
    const [top3Text, top3Class] = row.top3.length >= 3 ? resultText(row.top3Hit) : ['—', ''];
    const [top6Text, top6Class] = resultText(row.top6Hit);
    tr.append(makeCell(top3Text, top3Class));
    tr.append(makeCell(top6Text, top6Class));
    tr.append(makeCell(row.generatedAt ? new Date(row.generatedAt).toLocaleString('zh-CN') : '—'));
    detailBody.append(tr);
  }

  detailPanel.hidden = false;
  detailPanel.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

async function load() {
  refreshButton.disabled = true;
  state.hidden = false;
  state.className = 'state';
  state.textContent = '正在读取最近30期模型历史...';
  content.hidden = true;
  detailPanel.hidden = true;
  try {
    const predictions = await loadRecentPredictions();
    if (!predictions.length) throw new Error('没有可用的预测历史');
    modelRows = normalizePredictions(predictions);
    renderScoreboard();
    document.querySelector('#latest-issue').textContent = String(predictions[predictions.length - 1].issue || '—');
    document.querySelector('#record-count').textContent = `${predictions.length}期`;
    state.hidden = true;
    content.hidden = false;
  } catch (error) {
    state.className = 'state error';
    state.textContent = `加载失败：${error.message}`;
  } finally {
    refreshButton.disabled = false;
  }
}

closeDetail.addEventListener('click', () => { detailPanel.hidden = true; });
refreshButton.addEventListener('click', load);
load();
