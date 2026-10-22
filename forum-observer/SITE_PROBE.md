# 118图库独立采集测试

复用 PR #36 的 `scrapling_assist`：公用 `snapshot_selector` 解析 DOM，
并调用原 `inspect_listing` 输出论坛结构诊断。没有替换正式采集器。

本入口保存原始 HTML、正文文本、链接、图片候选、脚本地址以及逐跳监控报告。
不把广告图标、导航页或动态页面空壳当成有效资料；图片只记录地址，未执行 OCR。
不解析计分、不读写正式输出目录，不调用模型、冻结、结算或发布。

在 `forum-observer` 目录运行：

```bash
pip install 'scrapling[fetchers]==0.5'
python -m forum_observer.site_probe 'https://yydsddd.이미지갤러리갤.닷컴:8443/#0411/' --out /tmp/118tk-test-unique
```

默认 HTTP 模式识别实测入口的静态跳转、二进制网址数组和网页版按钮的 XOR 网址模板。
只解码限定模板，不执行任意网页 JavaScript。多个备用线路暂按原页面顺序选择第一条，
失败会留存原因，不宣称所有线路均失败。最多15跳，每跳最多30秒，默认12跳、15秒。
每次必须指定新目录，防止覆盖测试证据。`report.json` 在每跳请求前及结束时原子更新。

如果最终页面需要 JavaScript 渲染，使用原采集器相同的 Playwright 渲染方式：

```bash
python -m playwright install chromium
python -m forum_observer.site_probe '实际资料地址' --renderer browser --out /tmp/118tk-browser-unique
```

浏览器模式未自动操作登录、付费、验证码、下载或任意广告按钮。仅有页面快照不能证明
资料已验证；当前 `verified_material_count` 固定为0，须在确认实际资料结构后另做定向解析。
`snapshot_captured` 表示取得快照；`dynamic_render_required` 表示需要浏览器渲染。

```bash
python -m unittest discover -s tests -p 'test_scrapling_assist.py' -v
python -m unittest discover -s tests -p 'test_site_probe.py' -v
```

涉及 AGENTS.md 条目5、9、10：仅测试页面与候选资源，未启用评论或图片识别，
不读取/覆盖冻结记录，不修改 D1、正式 V7 或账本布局。

## 2026-10-22 实测

- 原有辅助诊断测试及新入口测试：全量193项通过。
- 用户入口 → `aa.118ddhh.app:8443/two/` → `/three/` → `finish.html`
  → `/four/` → `j.yikesongkeji.net:118` → `118d5.com`：取得导航页面快照。
- 网页版按钮解出的目标为 `https://probe01.xn--118-ti4bzj2dci4954e8d2d.xn--q9jyb4c:8443/`，
  HTTP 200，状态 `dynamic_render_required`。`probe01` 对应原按钮生成的7位子域名。
- 没有取得可验证的资料正文，不能报告资料采集成功。
- Chromium 下载包无法解压，当前环境未完成浏览器渲染实测；浏览器模式仍待验证。
- 入口网络出现过12秒超时，报告保留了请求地址、时间和异常；不推断整个网站不可用。
