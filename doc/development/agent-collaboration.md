# MD2Word 的 Agent 协作与提示词

本说明用于保持后续 Vibe Coding 任务不偏离 Windows Desktop MVP。项目事实以 `doc/requirement/requirements.md`、`doc/requirement/ui-spec.md`、`doc/architecture/electron-csharp.md` 和当前代码为准；外部案例只提供方法，不替代本项目验收。

## 协作边界

OpenAI 的 [Multi-agent 指南](https://developers.openai.com/api/docs/guides/agents-api/multi-agent)建议只将独立、范围明确的工作交给 subagent，并让主 Agent 汇总结果；共享文件的并行写入需要协调。对本仓库，适合并行的是独立的官方资料核对、只读视觉审查、测试用例清单；Renderer/Main/Worker 共用契约、版本号、发布安全和最终 Word 成品应由主 Agent 统一集成与签收。

[OpenAI 关于 AGENTS.md 的实践建议](https://developers.openai.com/blog/rethinking-skills-and-prompts-for-gpt-6-astra)强调按任务读取相关文档，避免每个小改动都加载全仓资料；完成标准要包含运行、检查和修复。[Agents SDK 仓库实践](https://developers.openai.com/blog/skills-agents-sdk)也采用简短的条件规则，把详细的可重复工作放在技能或脚本中。本仓库因此在 `AGENTS.md` 只保留安全边界、任务触发条件和验收入口，具体审查矩阵放在 `doc/uiPrototype/README.md`。

## 可直接复用的任务提示词

```text
目标：<描述用户能完成什么；给出前后行为示例>
范围：<页面/模块/文件；列出明确不改的协议或数据>
事实来源：<需求、UI、架构与已有代码的链接>
验收：<每个页面和按钮、空/加载/错误/禁用/成功/取消状态；1100x720、1280x800、1440x900 和断点两侧>
桌面边界：<哪些必须用真实 Electron/Main/Worker/Word 验证；浏览器 mock 只用于哪些状态>
成品审查：<若涉及 DOCX，用公开合成夹具核对 Open XML 和 Word 导出的逐页 PDF>
交付：<代码、测试、同步文档、截图与实际运行结果；失败要修到通过，未跑的检查必须注明>
```

派发独立任务时补充：`只读或可编辑范围`、`不得碰的共享文件`、`预期返回的证据`、`发现跨范围问题时先报告主 Agent`。主 Agent 在合并前重新检查接口契约和测试，不把 subagent 的一句“完成”当作验收。

## 官方案例与本项目借鉴

1. [OpenAI Agents SDK 的维护实践](https://developers.openai.com/blog/skills-agents-sdk)：仓库级规则、局部技能和 CI 把验证、发布检查与 PR 审查变成可重复流程。文章报告两个仓库三个自然月合并 PR 数从 316 增至 457；这是整体实践的同期结果，不应单独归因于 AGENTS.md。MD2Word 借鉴的是“短规则 + 条件触发 + 可执行验收”。
2. [OpenAI DevDay 的 Codex 实践](https://developers.openai.com/blog/codex-at-devday)：团队同时交给 Codex 三到四项互不依赖的工作；另一个演示经比较后由多 Agent 改为单 Agent 架构。MD2Word 借鉴的是先判断依赖关系，不为并行而并行。
3. [Codex 长任务实验](https://developers.openai.com/blog/run-long-horizon-tasks-with-codex)：一个设计工具实验使用目标/限制、里程碑、验证和状态文档维持约 25 小时任务的一致性，作者明确称其不是生产发布。MD2Word 借鉴其“固定完成标准、逐阶段验证、持续记录已知问题”，但不会把实验结果当成产品质量证明。

升级检查实现另参考 [GitHub latest release API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)与 [Electron 更新指南](https://www.electronjs.org/docs/latest/tutorial/updates)。匿名 API 被限流时的固定 Releases/latest HEAD 重定向是本项目的实测回退策略，不是 GitHub REST 契约；回退结果不含版本说明。当前只做正式 Release 的检查和人工下载安装入口，未配置静默自动更新。
