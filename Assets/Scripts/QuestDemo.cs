using UnityEngine;

namespace COR
{
    /// <summary>
    /// COR 任务系统的运行时演示组件。
    /// 
    /// ⚠️ 作为函数式重构的对照基线，此文件的核心价值不在于"如何正确使用 COR"，
    /// 而在于暴露 COR 消费端的三个结构性缺陷：
    ///   1. void 返回值使调用方无法感知处理结果
    ///   2. 合法/非法操作在调用端语法上完全不可区分
    ///   3. 状态变更的正确性只能靠人眼阅读日志验证，无法自动化断言
    /// 
    /// 生产环境中此类应仅存在于 Tests/Demo 场景，不应进入正式游戏逻辑。
    /// </summary>
    public class QuestDemo : MonoBehaviour
    {
        /// <summary>
        /// 通过 Inspector 注入的任务管理器引用。
        /// [SerializeField] 确保 Unity 序列化系统能正确保存跨场景/预制体引用。
        /// ⚠️ 未添加 [Required] 或 null 检查：若 Inspector 中未赋值，
        /// Start 中将抛出 NullReferenceException。
        /// 函数式改进方向：使用 Optional<QuestManager> 或启动时 Validate 并报错。
        /// </summary>
        [SerializeField] private QuestManager questManager;

        private void Start()
        {
            // ═══════════════════════════════════════════
            // 步骤1: 创建并注册任务
            // ═══════════════════════════════════════════
            
            // SerializableGUID 默认构造函数生成新的唯一标识。
            // ⚠️ 注意：new SerializableGUID() 的行为取决于其实现——
            // 若内部调用 Guid.NewGuid() 则安全；若仅分配零值 GUID 则会导致键冲突。
            // 建议显式使用 SerializableGUID.New() 工厂方法以消除歧义。
            SerializableGUID questId = new SerializableGUID();
            
            // RegisterQuest 内部调用 Dictionary.Add，重复 Id 将抛 ArgumentException。
            // ⚠️ 此处无异常处理：若注册失败，后续所有 UpdateQuest 都将因找不到任务而静默失败，
            // 且无任何错误反馈。这是 void API 的典型连锁风险。
            questManager.RegisterQuest(new Quest { Id = questId, Name = "Find the treasure" });

            // ═══════════════════════════════════════════
            // 步骤2-4: 发送三条消息 — 结果黑洞演示
            // ═══════════════════════════════════════════
            
            // ✅ 合法转换: NotStarted → InProgress
            // 调用方无法从返回值确认此操作是否成功，只能依赖 Debug.Log 输出。
            questManager.UpdateQuest(new StartQuestMessage { QuestId = questId });
            
            // ✅ 合法转换: InProgress → Completed
            // 语法上与上一行完全相同，无法从代码结构上表达"这步依赖于上步成功"。
            questManager.UpdateQuest(new CompleteQuestMessage { QuestId = questId });
            
            // ❌ 非法转换: Completed → Failed（业务规则不允许已完成的任务再失败）
            // ⚠️ 关键痛点：此行代码与上面两行在语法、类型、调用方式上完全一致。
            // 编译器不会警告，运行时不会抛异常，UpdateQuest 返回 void，
            // FailQuestProcessor 内部的 if (quest.State == InProgress) 检查失败后直接 return，
            // 消息被静默消费但状态未变更。调用方对此一无所知。
            // 
            // 这正是 Either Monad 的核心动机：
            //   var result = questManager.UpdateQuest(new FailQuestMessage(...));
            //   // result 类型为 Either<QuestError, Quest>
            //   // Left(InvalidStateTransition(Completed, Fail))  ← 显式、可断言、可组合
            questManager.UpdateQuest(new FailQuestMessage { QuestId = questId });
        }
    }
}