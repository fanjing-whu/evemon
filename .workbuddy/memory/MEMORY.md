# MEMORY.md

## EVEMon 项目关键约定

- **Seren 国服启动流程（2026-04-20 重构）**：非阻塞架构，零网络等待：
  1. 检查 ESI 缓存 → 有则直接使用
  2. 无缓存 → 从本地 TQ 资源（eve-items-en-US.xml.gzip）立即加载（零网络）
  3. 后台 → 异步刷新 ESI 缓存
  - `SerenDataLoader.LoadAsync` 优先本地 TQ 资源，后台排队 ESI 下载
  - `ESISdeDownloader.DownloadAllSdeDataAsync` 对所有 endpoint 检查本地 TQ 资源，有则直接缓存，无则后台下载

- **Seren 国服 SDE fallback 机制**：当 Seren ESI 数据缺失时，优先从本地英文 SDE 资源（eve-items-en-US.xml.gzip）加载 type 详情，不依赖 TQ HTTP 请求。本地资源路径按以下顺序查找：
  1. `{AppDomain.CurrentDomain.BaseDirectory}/Resources/eve-items-en-US.xml.gzip`
  2. `{Environment.SpecialFolder.ApplicationData}/EVEMon/eve-items-en-US.xml.gzip`
- 本地资源解析使用 `XmlSerializer` 直接读取 gzip 流，递归遍历 `SerializableMarketGroup` 的 `Items` 和 `SubGroups`
- `ESISdeDownloader.FetchTypeDetailsAsync` 的新架构（非阻塞，异步后台刷新）：
  1. 立即从内存缓存返回（零等待）
  2. 未命中时从本地 TQ 资源加载（eve-items-en-US.xml.gzip，零网络）
  3. 仍未命中则排队到后台队列，等待全局速率限制器调度ESI请求
  4. 避免等待：首次调用立即返回，后续调用（如数据已刷新）返回最新缓存
- `SerenDataLoader.ReadCacheWithFallbackAsync` 辅助方法对所有 endpoint 数据使用 fallback 读取
- 所有 endpoint 列表数据（categories/groups/types/market_groups 等）通过 `ESISdeCacheManager.ReadCacheWithFallbackAsync` 读取，Seren 无缓存时自动使用 TQ 缓存
- `universe_systems` 详情不通过 ESI 获取（8537个星系太慢），改为从本地 TQ 资源按需加载

- **Pyfa FSD 数据作为备用技能数据源（2026-04-20，更新 2026-04-21）**：
  - Pyfa 项目公开了其 FSD 数据，位于 `D:\Works\codes\fanjing\evemon_cn\Pyfa\staticdata\fsd_built`
  - 转换脚本位于 `scripts/pyfa_to_evemon/generate_eve_skills_from_pyfa.py`
  - 输出：`eve-skills-pyfa-zh-CN.xml.gzip`（中文，27KB）和 `eve-skills-en-US.xml.gzip`（英文，27KB）
  - Pyfa FSD types 数据含 `typeName_zh`/`groupName_zh`/`categoryName_zh` 字段，591 个技能全部有中文名
  - **三级 fallback 架构（SerenDataLoader + ESISdeDownloader）**：
    - Level 1: ESI 缓存（Seren 国服 `eve-skills-esi.xml.gzip`）
    - Level 2: Pyfa 中文静态数据（`eve-skills-pyfa-zh-CN.xml.gzip`）
    - Level 3: TQ 英文 SDE（`eve-skills-en-US.xml.gzip`）
    - Level 4: 抛出 `InvalidOperationException` + 错误日志
  - **方法命名约定**：
    - `LoadLocalTypeDetailsAsync()`（非 TQ-only，混合来源）
    - `LoadFromLocalResourceAsync()`（非 TQ-only）
    - `LoadSkillsWithFallback()`（level2/3/4 fallback 链）

