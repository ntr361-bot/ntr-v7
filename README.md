# ntr-v7 当前运行说明

本仓库包含 V7 正式预测、P25 网页资料观察、Forward50 前瞻模型以及历史静态站点文件。不要因为目录或工作流名称较旧就直接删除；先按下面的职责区分。

## 当前正式 V7 生产链

正式日更入口：`.github/workflows/run-prediction.yml`

主要流程：

1. 每天 22:00（UTC 14:00）主运行；23:00（UTC 15:00）作为安全重试。
2. 每 15 分钟检查一次智能账本提交的 V7 手动运行请求。
3. 刷新并校验最新开奖数据。
4. 生成正式 V7、自动学习、100期以及已接入的 Forward50 结果。
5. 执行生产安全校验。
6. 提交 `site/data/**` 里的正式数据。
7. 通过 `/api/v7-sync/publish` 同步到智能账本。

当前智能账本生产地址：

`https://smart-ledger-2026.ntr133.chatgpt.site`

## V6 与 V7 的关系

V6 仓库 `ntr361-bot/ntr-v6` 仍是独立运行链，保留其定时任务、历史数据和 `/api/v6-sync/*` 同步能力。

**不要因为 V7 是当前主要展示链就关闭、删除或改写 V6。** V6 与 V7 应按两条独立链排查。

## P25 网页资料观察

工作流：`.github/workflows/check-p25-web.yml`

- 每天 12:00 和 17:00（中国标准时间）检查网页资料。
- 如果当期 P25 已生成，代码会跳过重复网页请求。
- 有新的 P25 runtime-state 时，通过现有 V7 同步入口发布到智能账本。
- P25 属于独立观察模型，不应被误当成 V7 正式成绩链。

## Forward50

50期稳定度和50期周期公平版已经接入正式 V7 生成链。

`.github/workflows/publish-forward50.yml` 现在仅作为**手动维护/恢复工具**，不会再因源码 push 自动执行。正常日更不要单独运行它。

## 智能账本补同步

`.github/workflows/sync-smart-ledger-state.yml` 是**手动恢复工具**，用于把仓库中已经提交的 V7 状态重新发布到智能账本，并验证线上 P25。

它不是正式日更主入口；正常同步由 `run-prediction.yml` 完成。

## 历史 GitHub Pages 静态站点

`.github/workflows/deploy-site.yml` 只用于旧静态站点维护，目前仅允许手动运行。

它**不是**当前 `smart-ledger-2026.ntr133.chatgpt.site` 的发布链。

`site/` 目录不能整体删除：

- `site/data/**`：当前 V7 正式生产数据，仍在使用。
- `site/index.html`、`site/app.js` 等静态页面：属于历史 GitHub Pages 前端，可保留作历史/排查用途。

## 修改原则

- V6、V7 正式运行链未经明确确认不要停用或删除。
- 预测逻辑、生产数据和前端发布要分开排查。
- 不要用 GitHub Pages 是否更新来判断智能账本是否发布成功。
- 不要把历史 README、旧 Worker 名或旧静态页面当成当前生产环境的最终事实。
