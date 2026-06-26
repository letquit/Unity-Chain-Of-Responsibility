using System;
using System.Collections.Generic;
using UnityEngine;

namespace COR
{
    /// <summary>
    /// 任务管理器：作为责任链的入口点和任务数据的唯一持有者。
    /// 
    /// ⚠️ 架构观察（函数式视角）：
    /// 此类混合了两个职责：1) 维护可变字典状态；2) 构建并触发命令式处理链。
    /// 在纯函数式重构中，这两者应分离为：
    ///   - QuestRepository: 纯数据查询接口，返回 Optional<Quest>
    ///   - QuestReducer: 纯函数 (Quest, Message) => Either<Error, Quest>
    /// 当前设计是典型的 OOP 起点，适合作为重构前后的对照样本。
    /// </summary>
    public class QuestManager : MonoBehaviour
    {
        /// <summary>
        /// 任务注册表：以 SerializableGUID 为键的运行时任务存储。
        /// ⚠️ 使用 SerializableGUID 而非 System.Guid 是为了兼容 Unity Inspector 序列化，
        /// 这是 Unity 项目中 GUID 作为字典键的标准实践。
        /// 注意：Dictionary 本身不可序列化，此数据仅存在于运行时内存中。
        /// </summary>
        private Dictionary<SerializableGUID, Quest> quests = new();

        /// <summary>
        /// 责任链头节点引用。
        /// 所有消息从 chain 进入，沿链表依次传递直到被某个处理器消费或到达链尾。
        /// </summary>
        private IQuestProcessor chain;

        /// <summary>
        /// 初始化责任链：Start → Complete → Fail 的固定顺序。
        /// 
        /// ⚠️ 关键设计隐患：
        /// 1. 处理器顺序硬编码且隐含业务优先级假设。若新增 "PauseQuestProcessor"，
        ///    必须人工判断其应插入链的哪个位置，错误排序会导致消息被错误处理器拦截。
        /// 2. SetNext 返回 IQuestProcessor 支持流畅链接，但链的结构在运行时不可 introspect，
        ///    无法序列化/可视化/动态调整，调试时需逐层步进。
        /// 3. 每个处理器内部通过 void 返回值"吞掉"消息，调用方无法得知消息是否被成功处理。
        ///    这正是 Either Monad 要解决的"可观测的结果传递"问题。
        /// </summary>
        private void Awake()
        {
            chain = new StartQuestProcessor();
            chain.SetNext(new CompleteQuestProcessor()).SetNext(new FailQuestProcessor());
        }

        /// <summary>
        /// 注册新任务到运行时字典。
        /// ⚠️ 无重复键检查：quests.Add 在 Id 已存在时抛出 ArgumentException。
        /// 函数式改进方向：返回 Either<string, Unit> 或将 Add 改为 TryAdd + Optional 结果。
        /// </summary>
        public void RegisterQuest(Quest quest) => quests.Add(quest.Id, quest);

        /// <summary>
        /// 将消息注入责任链进行处理。
        /// ⚠️ 返回 void 意味着调用方完全丧失了对处理结果的感知能力：
        ///   - 不知道消息是否被任何处理器匹配
        ///   - 不知道任务状态是否实际发生了变更
        ///   - 不知道处理过程中是否出现了验证失败
        /// 这是 COR 模式与函数式范式最根本的冲突点，也是后续重构的核心目标。
        /// </summary>
        public void UpdateQuest(QuestMessageBase message) => chain.Process(message, quests);
    }

    /// <summary>
    /// 任务数据模型。
    /// [Serializable] 使其可在 Inspector 中编辑和 ScriptableObject 中持久化。
    /// 
    /// ⚠️ 可变状态警告：
    /// State 字段为 public 且无访问控制，任何持有引用的代码均可直接修改。
    /// 在函数式重构中，Quest 应为 immutable record/class，
    /// 状态变更通过 WithState(newState) 返回新实例来表达，消除副作用。
    /// </summary>
    [Serializable]
    public class Quest
    {
        /// <summary>
        /// 全局唯一标识符。使用 SerializableGUID 确保跨场景/资产引用稳定。
        /// 作为字典键时必须保证 Equals/GetHashCode 正确实现（SerializableGUID 通常已处理）。
        /// </summary>
        public SerializableGUID Id;

        /// <summary>
        /// 任务显示名称。public 字段在 Unity 序列化中比属性更可靠，
        /// 但牺牲了封装性。生产环境建议配合 [field: SerializeField] 属性使用。
        /// </summary>
        public string Name;

        /// <summary>
        /// 当前任务状态。默认 NotStarted 确保反序列化/新建时的安全初始值。
        /// ⚠️ 枚举状态机的局限：状态转换规则未编码在类型中，
        /// 例如 Completed → InProgress 的非法转换不会被编译器阻止。
        /// 函数式改进：用 ADT（代数数据类型）或状态机 DSL 编码合法转换路径。
        /// </summary>
        public QuestState State = QuestState.NotStarted;
    }

    /// <summary>
    /// 任务生命周期状态枚举。
    /// 定义了四个离散状态，但状态间的合法转换关系仅存在于处理器逻辑中（隐式），
    /// 而非类型定义中（显式）。这是传统枚举状态机的固有缺陷。
    /// </summary>
    public enum QuestState
    {
        NotStarted,
        InProgress,
        Completed,
        Failed
    }

    /// <summary>
    /// 任务事件类型枚举。
    /// 作为 QuestMessageBase 的多态标签，驱动责任链中的处理器匹配。
    /// 
    /// ⚠️ 与 QuestState 的映射关系未形式化：
    /// Start 事件应仅作用于 NotStarted 状态，Complete 仅作用于 InProgress，
    /// 但这些约束分散在各 Processor 内部的 if 检查中，而非类型系统保证。
    /// 函数式改进：将 Event + CurrentState 组合为 sealed interface 的合法输入类型，
    /// 使非法组合在编译期不可表达。
    /// </summary>
    public enum QuestEvent
    {
        Start,
        Complete,
        Fail,
    }
}