# EVEMon 国服(Seren)支持 - C1方案实施计划

## 背景

由于网易国服没有提供独立的中文SDE数据包，我们采用 **C1方案：纯ESI模式 + 本地缓存**。

## 已完成工作

### 1. ESI地址更新 ✅ (2026-04-19)
已更新 `NetworkConstants.resx` 中的国服ESI地址：

| 配置项 | 旧地址 | 新地址 |
|--------|--------|--------|
| ESISerenBase | https://esi.eve.com.cn | https://ali-esi.evepc.163.com |
| SSOSerenBase | https://login.eve.com.cn | https://login.evepc.163.com |
| SSOSerenBaseV2 | https://login.eve.com.cn/v2/oauth/ | https://login.evepc.163.com/v2/oauth/ |
| EVEImageServerBaseSeren | https://images.eve.com | https://images.evepc.163.com |
| DatafilesSerenBase | https://esi.eve.com.cn | https://ali-esi.evepc.163.com |

### 2. 阶段1: 基础设施 ✅ (2026-04-19 深夜)

#### ESISdeCacheManager.cs
- 缓存管理：缓存目录 `%APPDATA%/EVEMon/cache/esi/`
- 清单管理：`cache_manifest.json` 存储ETag和过期时间
- 功能：缓存读写、验证、清除

#### ESISdeDownloader.cs
- 定义14个ESI SDE端点（按优先级P0/P1/P2分类）
- 支持分页数据自动合并
- 自动重试机制（最多3次）
- 进度报告支持

### 3. 阶段2: 数据转换 ✅ (2026-04-20 凌晨)

#### ESIToDatafileConverter.cs
- `ConvertItems()` - 将ESI universe types/groups/categories/market_groups 转换为 ItemsDatafile
- `ConvertSkills()` - 将ESI universe types 转换为 SkillsDatafile
- `ConvertGeography()` - 将ESI universe regions/constellations/systems 转换为 GeoDatafile
- `SaveDatafileToCache()` / `LoadDatafileFromCache()` - GZip压缩的XML缓存

#### StaticSkills/StaticItems 修改
- 添加 `LoadFromDatafile()` 方法支持从预加载数据文件加载

### 4. 阶段3: 集成 ✅ (2026-04-20 凌晨)

#### SerenDataLoader.cs
- 国服数据加载器入口点
- 集成ESI下载、缓存和转换功能
- `LoadAsync()` - 主加载方法
- 进度报告事件 `LoadProgress`
- `GetCacheStatus()` / `ClearCache()` 管理方法

#### MainWindow.cs 集成
- `InitializeData()` 添加国服模式检测
- 国服模式下调用 `SerenDataLoader.LoadAsync()`
- 失败时回退到本地文件加载

### 5. 阶段4: UI优化 ✅ (2026-04-20 凌晨)

#### 进度显示集成
- `SerenDataLoader` 调用 `GlobalDatafileCollection.OnDatafileLoaded()` 触发进度条更新
- 添加 `SerenDataLoader.LoadProgress` 事件处理到 `MainWindow`
- 加载时状态栏显示 "ESI: xxx"
- 进度条与ESI加载进度同步

#### 数据文件加载补全
- 添加证书、地理、蓝图、再处理、专精数据文件的加载
- 每个加载完成后触发 `GlobalDatafileCollection.OnDatafileLoaded()`

#### 错误恢复机制
- ESI连接失败检测，自动回退到本地数据文件
- 数据文件加载失败时返回状态码
- 关键数据（物品/技能）缺失时整个加载失败
- 非关键数据（证书/蓝图等）加载失败不影响主流程
- 异常详细日志记录

---

## C1方案详细设计

### 核心思想
- 国服模式下，静态数据(SDE)通过ESI API实时获取
- 数据缓存到本地 `%APPDATA%/EVEMon/cache/esi/` 目录
- 下次启动时优先从本地缓存加载，缓存过期时重新获取

### 架构设计

```
┌─────────────────────────────────────────────────────────────┐
│                    GlobalDatafileCollection                 │
│                         LoadAsync()                         │
└─────────────────────┬───────────────────────────────────────┘
                      │
          ┌───────────┴───────────┐
          │                       │
    ┌─────▼─────┐          ┌─────▼─────┐
    │ 国际服模式 │          │ 国服模式   │
    │           │          │           │
    │ 本地文件   │          │ ESI API   │
    │ 加载      │          │ + 缓存    │
    └───────────┘          └─────┬─────┘
                                 │
                    ┌────────────┴────────────┐
                    │                         │
             ┌──────▼──────┐          ┌──────▼──────┐
             │ 本地缓存存在  │          │ 本地缓存不存在│
             │             │          │             │
             │ 验证ETag    │          │ 从ESI下载   │
             │ 是否过期     │          │ 并缓存      │
             └──────┬──────┘          └──────┬──────┘
                    │                         │
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │   转换为本地XML格式      │
                    │   (兼容现有加载器)       │
                    └─────────────────────────┘
```

### 关键组件

#### 1. ESISdeDownloader
负责从ESI下载静态数据

```csharp
public class ESISdeDownloader
{
    // 下载并缓存ESI数据
    Task<ESICacheInfo> DownloadAndCacheAsync(string endpoint, string cacheFile);
    
    // 验证缓存有效性
    bool IsCacheValid(ESICacheInfo cache, TimeSpan maxAge);
    
    // 批量下载所有SDE数据
    Task DownloadAllSdeDataAsync(IProgress<int> progress);
}
```

#### 2. ESICacheManager
管理ESI数据的本地缓存

```csharp
public class ESICacheManager
{
    // 缓存目录
    string CacheDirectory => Path.Combine(
        EveMonClient.EVEMonDataDir, "cache", "esi");
    
    // 获取缓存路径
    string GetCachePath(string endpoint);
    
    // 读取缓存
    Task<ESICacheInfo> ReadCacheAsync(string endpoint);
    
    // 写入缓存
    Task WriteCacheAsync(string endpoint, string data, string etag, DateTime expires);
    
    // 检查缓存是否过期
    bool IsCacheExpired(ESICacheInfo cache);
}
```

#### 3. ESIToDatafileConverter
将ESI JSON数据转换为本地XML格式

```csharp
public class ESIToDatafileConverter
{
    // 转换技能数据
    SkillsDatafile ConvertSkills(EsiTypeListResponse response);
    
    // 转换物品数据
    ItemsDatafile ConvertItems(EsiTypeListResponse response);
    
    // 转换证书数据
    CertificatesDatafile ConvertCertificates(...);
    
    // 转换精通数据
    MasteriesDatafile ConvertMasteries(...);
}
```

---

## ESI端点映射

| 数据文件 | ESI端点 | 优先级 | 说明 |
|----------|---------|--------|------|
| eve-skills | /universe/groups/?category=skill | P0 | 技能组 |
| eve-skills | /universe/types/?type_id=X (遍历) | P0 | 技能详情 |
| eve-items | /universe/categories/ | P0 | 物品类别 |
| eve-items | /universe/groups/ | P0 | 物品组 |
| eve-items | /universe/types/ (分页) | P0 | 物品类型 |
| eve-blueprints | /universe/types/?category=blueprint | P1 | 蓝图 |
| eve-certificates | /characters/{id}/certificates/ | P2 | 证书(用户数据) |
| eve-masteries | 无ESI对应端点 | P2 | 可能需要硬编码或跳过 |
| eve-geography | /universe/regions/ | P1 | 区域 |
| eve-geography | /universe/constellations/ | P1 | 星座 |
| eve-geography | /universe/systems/ | P1 | 星系 |
| eve-reprocessing | /universe/types/ (包含reprocessing) | P1 | 回收加工 |

---

## 实施步骤

### 阶段1: 基础设施 (优先级: 高)

1. **创建 ESICacheManager**
   - 缓存目录管理
   - 缓存读写操作
   - ETag/Expires 验证

2. **创建 ESISdeDownloader**
   - ESI API 调用封装
   - 分页数据获取
   - 错误处理和重试

### 阶段2: 数据转换 (优先级: 高)

3. **创建 ESIToDatafileConverter**
   - JSON到XML转换器
   - 类型映射逻辑
   - 数据验证

4. **修改 DatafileConstants**
   - 添加国服模式判断
   - 支持从缓存加载

### 阶段3: 集成 (优先级: 中)

5. **修改 GlobalDatafileCollection**
   - 国服模式分支
   - 异步加载逻辑

6. **修改各 Static* 类**
   - 支持从ESI数据加载
   - 保持向后兼容

### 阶段4: 优化 (优先级: 低)

7. **进度显示**
   - 下载进度条
   - 状态提示

8. **错误恢复**
   - 网络错误处理
   - 缓存损坏处理

---

## 缓存策略

### 缓存有效期
- 默认缓存时间: 7天
- 缓存文件格式: `endpoint_name.json`
- 缓存元数据: `cache_manifest.json`

### 缓存结构
```
%APPDATA%/EVEMon/
└── cache/
    └── esi/
        ├── cache_manifest.json    # 缓存清单
        ├── universe_types.json    # 物品类型
        ├── universe_groups.json   # 物品组
        ├── universe_regions.json  # 区域
        └── ...
```

### 缓存清单格式
```json
{
  "lastUpdate": "2026-04-19T12:00:00Z",
  "endpoints": {
    "universe_types": {
      "etag": "\"abc123\"",
      "expires": "2026-04-26T12:00:00Z",
      "file": "universe_types.json"
    }
  }
}
```

---

## 风险与限制

1. **中文本地化**: ESI返回的数据是英文的，无法获取中文名称
   - 解决方案: 显示英文名称，或提供可选的中文语言包

2. **数据完整性**: ESI可能不包含SDE的所有数据
   - 解决方案: 逐步完善，优先实现P0数据

3. **首次加载慢**: 首次需要下载所有数据
   - 解决方案: 显示进度条，后台下载

---

## 验证清单

- [ ] ESI地址更新后能正常访问国服ESI
- [ ] 缓存机制正常工作
- [ ] 数据转换逻辑正确
- [ ] 国服模式下能正常加载技能和物品
- [ ] 缓存过期后能正确刷新数据
- [ ] 网络错误时能优雅降级
