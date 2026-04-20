# MEMORY.md

## EVEMon 项目关键约定

- **Seren 国服 SDE fallback 机制**：当 Seren ESI 数据缺失时，优先从本地英文 SDE 资源（eve-items-en-US.xml.gzip）加载 type 详情，不依赖 TQ HTTP 请求。本地资源路径按以下顺序查找：
  1. `{AppDomain.CurrentDomain.BaseDirectory}/Resources/eve-items-en-US.xml.gzip`
  2. `{Environment.SpecialFolder.ApplicationData}/EVEMon/eve-items-en-US.xml.gzip`
- 本地资源解析使用 `XmlSerializer` 直接读取 gzip 流，递归遍历 `SerializableMarketGroup` 的 `Items` 和 `SubGroups`
- `ESISdeDownloader.FetchTypeDetailsAsync` 的 `FallbackMode.FallbackToTQ` 流程：Seren ESI → 本地 TQ 资源（eve-items-en-US.xml.gzip，零网络）
- `SerenDataLoader.ReadCacheWithFallbackAsync` 辅助方法对所有 endpoint 数据使用 fallback 读取
- 所有 endpoint 列表数据（categories/groups/types/market_groups 等）通过 `ESISdeCacheManager.ReadCacheWithFallbackAsync` 读取，Seren 无缓存时自动使用 TQ 缓存
