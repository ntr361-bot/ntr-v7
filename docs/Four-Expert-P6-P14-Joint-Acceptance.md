# 四专家 P6–P14 联合验收

结论：PASS（四专家合成样本技术联合验收）。修复后流程连通性及新V65修订同源依赖检查均通过。不能据此启用正式 Macro或声称预测收益改善。

本次使用 CurrentExpertCatalog 四个 macro-full12 修订、真实 V65/Integrated-V7 适配器和独立临时 SQLite 存储。只有测试内存中的注册视图临时开启资格；持久化注册目录始终 Disabled/Ineligible。输入为140条确定性合成历史，用于软件集成验证，不是实际开奖表现回测。

## 已验证

- 同目标期、同历史截止期的四份完整快照经过 P6 密封、解析和 Pool 持久化。
- P7 读取实际适配器排名，P8 构建假设，P9/P10 搜索支持和反证，P11/P12 执行批判与置信度，P13 决策，P14 输出完整12生肖排名。
- 没有历史专家预测成绩时，系统保持 HOLD/REJECT；测试提议的1%权重转移未生效，NoAction排名保持一致。该提议是显式测试输入，并非自动学习生成。
- 缺失快照被标记 Missing；已冻结四专家Pool缺一份快照时P7拒绝。
- 目标期实际结果进入前缀时Critic否决，P13保留原权重。
- 正式V65完整结果、V7特征/分数/Top3/Top6保持一致。V7每次调用自动生成的CreatedAt不参加确定性比较。
- 未调用生产存储或PredictionHistory写入；没有云端发布、COUNTER运行或专家持久化启用。

## 已修复的阻断项

CurrentExpertCatalog 的 SharedHistory 边仍绑定 `@v65-rule-current` 旧修订。P7严格按两端ExpertRevisionId筛选依赖，因此三个新的V65 macro-full12修订没有组成已登记的同源组。实测 `V65_NEW_REVISION_SHARED_GROUP=False`。

经用户授权修复后，CurrentExpertCatalog为三个新V65修订两两追加SharedHistory和SharedFeatures，共6条对称边。SharedFeatures表示共享计算定义，并不表示不同窗口的特征数值相同。两端绑定明确Revision，规范排序，保存源码引用和共享输入ID；不属于消费边，Depth仍为0。旧边和Auto父版本均保留。

复验：`V65_NEW_REVISION_SHARED_GROUP=True`，四专家联合验收、P5注册表回归、P6快照回归均退出码0。正式V65/V7输出一致，持久化专家保持关闭。修改仅更新目录初始化逻辑，没有迁移或重写现存生产注册数据库。

## 验证范围限制

这是单个合成目标期的技术集成验收，不证明真实历史收益、置信度校准或自主动作提议能力。未执行Walk-Forward、Live Shadow或Reflection学习闭环，也未证明重建模式贯通P14。历史成绩缺失不能被解释成零命中率。

验收入口：`--four-expert-chain-smoke`。失败/条件通过以非零进程状态返回；本次测试构建成功，0错误，保留239条编译警告。
