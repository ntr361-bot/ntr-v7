# 论坛采集监控与追溯

适用：正式采集、显式期号隔离测试。只增加运行证据，不修改计分、作者选择、冻结数据或账本。

## 查看入口

- 正式最近状态：`status/latest.json`；每次运行：`status/runs/<run>-<attempt>.json`。
- 隔离测试最近状态：`status/tests/latest.json`；每次测试：`status/tests/runs/<run>-<attempt>.json`。
- 远端状态变化历史：`status/events/`、`status/tests/events/`。每次上报单独追加文件。
- Actions 完整日志产物：正式 `forum-monitor-<run>-<attempt>`；隔离测试 `forum-historical-test-<issue>`。
- 完整采集阶段日志在产物的 `monitor/events.jsonl`；最新本地状态为 `monitor/latest.json`。运行监督和远端发布失败日志在 `runtime-monitor/monitor/`。

## 生命周期

采集入口在读取配置、启动浏览器之前写 `starting`。每个正文采集阶段写 `running` 事件；每15秒本地心跳保存当前阶段及最后进度。完成、覆盖不完整、异常或信号终止分别记录 `completed`、`incomplete`、`failed`、`interrupted`。每次入口具有独立 trace_id，旧事件不删除。失败时保留阶段、期号、错误和最后计数。

工作流在安装采集依赖之前上报启动；监督程序约每60秒上报远端。最近120秒有经过验证的非空正文及正文摘要才标记 `collecting_effectively`。心跳更新不表示有效采集，必须同时看 progress_updated_at 和 last_verified_post。远端发布失败会留下 publication_failed 本地事件，并由 always 上传完整日志；不能把旧状态当作当前正常运行。

隔离测试只运行 audit 入口，不调用 freeze/cycle/账本发布，不使用正式调度窗口或已有冻结的重复采集门禁；单次测试仍最多30分钟。测试状态独立存放，不覆盖正式最近状态。测试默认不因源代码 push 自动采集历史期号，仅手动 dispatch 或向专用 forum-monitor-test 分支写入 test-290.json 触发标记。

GitHub接口、runner突然消失或强制SIGKILL可能阻止最后状态上传；应将长期不更新标为失联/未验证，不能补造成功。远端分钟级状态不是每一条正文操作，逐阶段完整证据保存在本地产物日志。

涉及 AGENTS 约束5、6的正文计数与约束7的状态报告；不改变任何评分规则、冻结内容、正式V7、D1或页面布局。

加精优先验收以正文队列为准：priority_audit.first_ordinary_attempt 保存第一篇普通正文开始时的加精入队/尝试/结束检查数；violation_count 非零为顺序违规。报告中的加精计数只读取 article_details.featured，停用评论计数后不会再误报为0。历史资料缺少正文计数时标记未统计，不推断为0；旧冻结报告不回写。
