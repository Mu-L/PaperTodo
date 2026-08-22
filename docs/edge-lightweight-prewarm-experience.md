# Edge Lightweight Prewarm：已验证结论

> 这是一条长期经验记录，不是新的 Edge 架构说明。当前架构仍以根目录 `ARCHITECTURE.md` 和 `DECISIONS.md` 的 V3 Lite / DComp translation-only 约束为准。

## 结论

`EdgeCapsuleQueueCompositionProxy.PrewarmLightweight` 已经做过实际 A/B 对比，当前结论是：**保留 Lightweight Prewarm。**

它存在的目的不是提前构造第二套 presentation engine，而是把首次真实 Edge hover/queue-composition 会触发的一部分 WPF HWND、DComp surface/visual、cloak/uncloak 与 compositor 首用成本提前支付。既有实测确认了这条预热对首次交互有实际价值，因此后续常规代码审查不应仅因为它会创建临时 HWND/DComp 资源就再次把“是否需要 A/B、是否应该直接删除”列为待办。

## 边界

- 这条结论只认可当前 **Lightweight**、一次性、启动 idle 后执行的预热方向。
- 它不改变 V3 Lite 的 ownership：WPF/bounded host 继续拥有 shape/size/presentation，DComp 只承担 live HWND surface 的 translation/handoff。
- 不因为“预热有效”重新引入 bitmap snapshot、clip/scale/effect resize、Reveal/Conceal、第二套 presentation state 或长期 warm pool。
- 如果未来预热实现明显扩张、Windows/DWM 行为发生变化，或出现新的可复现实测回归，可以重新 benchmark；在没有新证据时，不重复推翻已完成的 A/B 结论。

## 当前代码入口

- `src/App.EdgeCapsuleComposition.cs`：启动 idle 条件与预热调度。
- `src/EdgeCapsuleQueueCompositionProxy.LightPrewarm.cs`：Lightweight Prewarm 本体。
- `DECISIONS.md` D-007～D-010：bounded live host、WPF owns shape、DComp translation-only 与 handoff authority 的长期架构边界。

## 为什么单独记这条

这轮 4.0 瘦身审查再次把 Lightweight Prewarm 误判成“尚未验证、需要重新 A/B”的候选项，说明此前的实测结论没有形成可检索的长期记录。这里补齐的目的就是避免 Agent/Codex 在以后每次性能审查时重复提出已经完成过的验证，而不是把一次 benchmark 过程本身扩写成长期验收矩阵。
