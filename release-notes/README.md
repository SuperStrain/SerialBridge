# 中文发布说明维护规则

SerialBridge 的定时发布、Actions 手动发布和手动 Tag 发布，均由
`scripts/generate_release_notes.py` 生成 **中文** Release 正文。

## 默认行为

- 自动查询 GitHub 上最后一个**已成功发布且非草稿、非预发布**的正式 Release，使用其 Tag 与当前构建提交确定变更范围；即使存在发布失败的更高 Tag，也不会遗漏未发布的变更。
- 仅遍历主线的 first-parent 提交：合并 PR 只形成一条变更，不重复列出其内部提交；直接推送到主分支的真实改动仍会保留。按提交标题前缀 `feat/fix/perf/refactor/docs/ci/chore` 等分类。
- 版本概述、分类标题、下载说明、完整变更记录都使用中文。
- 英文 PR 标题不会原样作为中文发布说明。未提供中文说明时使用保守的中文描述与 PR 链接，避免编造修复内容。
- 版本没有可见变更时（例如人工重新发布），也能生成有效的中文维护说明。
- 只对新创建的 Release 设置正文；再次运行已有 Tag 时只更新安装包附件，不覆盖人工编辑的发布说明。

## 让重要版本有“正式发布说明”的信息密度

强烈建议在重要功能或问题修复 PR 中，添加一份 `release-notes/changes/<PR编号>.md`，
例如 `release-notes/changes/42.md`：

```text
分类：问题修复
说明：修复客户端异常断开后，部分连接历史未能正确保存的问题
注意：此版本升级后无需迁移原有映射配置
```

- **分类**：新增功能、问题修复、功能优化、其他更新，四选一。
- **说明**：面向最终用户的真实变更说明，必须用中文撰写；技术名词、文件名可保留原文。
- **注意**：可选。仅在确有迁移、兼容性、删除数据等重要提醒时填写。
- 字段每行一个，不要在文件里使用 Markdown 表格；支持以 `#` 开头的注释行。
- 未提供中文说明不会阻止自动发布，但文案会比较笼统；**不会自动翻译或臆测英文 PR 的具体内容**。

如需为整个版本提供概述或额外提醒，可添加 `release-notes/highlights/vX.Y.Z.md`：

```text
概述：本版本重点改善串口客户端的连接历史可靠性。
注意：升级后请先确认串口映射配置是否符合预期。
```

这属于可选的版本补充说明。自动版本号可能由定时工作流决定，因此
**推荐按 PR 编号维护中文内容，而不要依赖人工事先猜测下一版本号**。

## 本地预览

```bash
python scripts/generate_release_notes.py --tag v1.3.18 --previous-tag v1.3.17 --output dist/release-notes.md
python -m unittest discover -s tests -p test_release_notes.py -v
```

本地预览可通过 `--previous-tag` 显式指定**真实已发布的正式版本**；CI 则通过 `GITHUB_TOKEN` 自动查询 GitHub Release 列表。命令只创建本地 Markdown 文件，不创建 Tag、Release 或发布动作。
生成器只使用 Git 提交记录及仓库内的中文说明文件，不依赖付费 API 或 AI 服务。

## 不变的发布机制

原有 4 天发布间隔、手动发布可跳过间隔、版本递增、Windows 测试、
Inno Setup / Portable ZIP 构建和安装包上传逻辑不变。
