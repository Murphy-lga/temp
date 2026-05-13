## 方案 B 原理详解

### 核心思想

**把计算和排序工作从应用层移到数据库层**，让数据库做它擅长的事。

---

### 当前方案（应用层计算）的问题

```
┌─────────────────────────────────────────────────────────┐
│  应用层                                                   │
│                                                          │
│  1. 查询 100 家企业 ──────────────────────────────► 数据库   │
│     SELECT * FROM company LIMIT 100                      │
│                                                          │
│  2. 对每家企业查询计数（200 次请求）                        │
│     SELECT COUNT(*) FROM review WHERE company_id = ?     │
│     SELECT COUNT(*) FROM comment WHERE company_id = ?    │
│                                                          │
│  3. 在内存中排序                                          │
│     _allCompanies.OrderByDescending(x => x.ReviewCount)  │
│                                                          │
│  4. 内存分页                                              │
│     _sortedCompanies.Skip(0).Take(10)                    │
└─────────────────────────────────────────────────────────┘
```

**问题**:
- 201 次数据库往返
- 只能排已加载的 100 条，不是全局排名
- 应用层做聚合计算效率低

---

### 方案 B（数据库层计算）

```
┌─────────────────────────────────────────────────────────┐
│  应用层                           数据库层                 │
│                                                       │
│  1 次查询 ─────────────────────►  物化视图/子查询        │
│     SELECT * FROM company_ranking   预先计算好          │
│     ORDER BY review_count DESC      所有企业的计数      │
│     LIMIT 10                        直接返回全局排名     │
│                                   第 1-10 名            │
└─────────────────────────────────────────────────────────┘
```

---

## 三种实现方式（从简单到复杂）

### 方式 1：子查询（最简单，无需改数据库结构）

直接在查询时做关联聚合：

```csharp
// 使用 Supabase 的原始查询或自定义视图
var query = @"
    SELECT 
        c.id, c.company_name, c.industry, c.city,
        c.credit_code,
        COUNT(r.id) AS review_count,
        COUNT(cm.id) AS comment_count
    FROM company c
    LEFT JOIN review r ON r.company_id = c.id AND r.is_valid = true
    LEFT JOIN company_comment cm ON cm.company_id = c.id AND cm.is_deleted = false
    WHERE c.is_active = true
    GROUP BY c.id, c.company_name, c.industry, c.city, c.credit_code
    ORDER BY review_count DESC
    LIMIT 10 OFFSET 0
";
```

**优点**: 
- 无需修改现有表结构
- 1 次查询搞定

**缺点**: 
- Supabase C# SDK 可能不支持复杂子查询
- 每次查询都要实时计算

---

### 方式 2：数据库视图 + 定时刷新（推荐）

```sql
-- 1. 创建物化视图（在 Supabase SQL Editor 执行）
CREATE MATERIALIZED VIEW company_ranking_view AS
SELECT 
    c.id, 
    c.company_name AS name,
    c.industry, 
    c.city,
    c.credit_code,
    COUNT(DISTINCT r.id) AS review_count,
    COUNT(DISTINCT cm.id) AS comment_count
FROM company c
LEFT JOIN review r ON r.company_id = c.id AND r.is_valid = true
LEFT JOIN company_comment cm ON cm.company_id = c.id AND cm.is_deleted = false
WHERE c.is_active = true
GROUP BY c.id, c.company_name, c.industry, c.city, c.credit_code;

-- 2. 创建索引加速排序
CREATE INDEX idx_ranking_review ON company_ranking_view(review_count DESC);
CREATE INDEX idx_ranking_comment ON company_ranking_view(comment_count DESC);

-- 3. 创建刷新函数
CREATE OR REPLACE FUNCTION refresh_company_ranking() 
RETURNS void AS $$
BEGIN
    REFRESH MATERIALIZED VIEW company_ranking_view;
END;
$$ LANGUAGE plpgsql;
```

**C# 代码**:
```csharp
// 直接查视图，像查普通表一样
var response = await client
    .From<CompanyRankingView>()  // 新模型映射到视图
    .Order(x => x.ReviewCount, Ordering.Descending)
    .Range((page - 1) * pageSize, page * pageSize - 1)
    .Get();
```

**优点**:
- 查询极快（预计算）
- 支持真正的全局分页
- 代码改动最小

**缺点**:
- 数据不是实时的（需要定时刷新）
- 需要在 Supabase 后台配置定时任务

---

### 方式 3：冗余字段 + 触发器（最复杂，但实时）

```sql
-- 1. 在 company 表添加冗余字段
ALTER TABLE company ADD COLUMN review_count INTEGER DEFAULT 0;
ALTER TABLE company ADD COLUMN comment_count INTEGER DEFAULT 0;

-- 2. 创建触发器自动更新计数
CREATE OR REPLACE FUNCTION update_company_review_count()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        UPDATE company SET review_count = review_count + 1 WHERE id = NEW.company_id;
    ELSIF TG_OP = 'DELETE' THEN
        UPDATE company SET review_count = review_count - 1 WHERE id = OLD.company_id;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_review_count
AFTER INSERT OR DELETE ON review
FOR EACH ROW EXECUTE FUNCTION update_company_review_count();
```

**优点**:
- 实时准确
- 查询简单快速

**缺点**:
- 需要维护触发器
- 写入性能略有影响

---

## 实现难度对比

| 方式                      | 数据库改动 | C# 代码改动 | 实时性             | 推荐度   |
| ------------------------- | ---------- | ----------- | ------------------ | -------- |
| 方式 1: 子查询            | 无         | 中等        | 实时               | ⭐⭐⭐   |
| 方式 2: 物化视图          | 中等       | 小          | 延迟（可配置刷新） | ⭐⭐⭐⭐ |
| 方式 3: 冗余字段 + 触发器 | 大         | 小          | 实时               | ⭐⭐⭐   |

---

## 我的建议

**对于你目前的项目规模**，推荐 **方式 1（子查询）** 作为过渡：

1. 不需要改数据库结构
2. 可以在 Supabase 中创建一个 **RPC 函数**，返回排序后的分页数据
3. C# 代码只需改 `RankingViewModel.LoadRankingAsync` 方法

如果你告诉我：
- 当前企业数量大概多少？
- 是否接受排行榜数据有几分钟延迟？

我可以给你更具体的实现建议。