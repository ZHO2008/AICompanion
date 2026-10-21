# 知识库目录说明

本目录由 AI 伴侣的 UnifiedKnowledgeManager 扫描。启动时加载全部 `*.json`，
之后通过 FileSystemWatcher 监听变化并热重载，**无需重启程序**。

## 文件格式

一个 `.json` 文件可以是单个条目对象，也可以是条目数组：

```json
{
  "id": "8f0f1c1e-0000-0000-0000-000000000000",
  "type": "Preprocess",
  "priority": 100,
  "triggers": ["你好", "hi"],
  "replyTemplate": "你好呀，今天过得怎么样？",
  "animation": "wave",
  "voiceConfig": {
    "fallbackPiperVoice": "zh_CN-default",
    "language": "zh",
    "speed": 1.0,
    "emotionIntensity": 1.0,
    "emotionStyle": "neutral"
  },
  "isActive": true
}
```

## 字段说明

| 字段 | 说明 |
| --- | --- |
| `type` | `Preprocess` / `Summary` / `CorePersonality` / `LongTermMemory` / `AnchorMemory` |
| `priority` | 排序权重，越大越优先 |
| `triggers` | 触发关键词，输入包含任一关键词即命中（**仅 `Preprocess` 参与直接回复**） |
| `replyTemplate` | 命中后直接返回的回复文本 |
| `voiceConfig.fallbackPiperVoice` | **必填**。缺失时该条目会被拒绝加载（防止降级到 Piper 时无音色可用） |
| `isActive` | 置为 `false` 可临时禁用而不删除 |
| `expiresIn` | 可选，形如 `"07:00:00"`；超期条目自动失效 |

## 重要约定

- **知识库只接受人工编写或审核确认的内容**。程序自身永远不会写入本目录。
- 模型在空闲时生成的候选条目只会出现在 `.pending/` 子目录中，必须由你在
  "待审核知识"界面确认后才会移入本目录。
- `.pending/` 是子目录，热重载**不会**扫描它，因此候选内容永远不会被当成正式知识。