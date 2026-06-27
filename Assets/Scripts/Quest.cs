using System.Collections.Generic;
using JetBrains.Annotations;
using Unity.Behavior.GraphFramework;
using UnityEngine;

namespace COR
{
    /// <summary>
    /// 责任链处理器接口。
    /// 
    /// ⚠️ 函数式视角的核心缺陷：
    /// Process 返回 void，调用方完全无法得知：
    ///   1. 消息是否被当前处理器消费
    ///   2. 状态变更是否成功
    ///   3. 是否发生了验证失败（如状态不匹配）
    /// 所有结果信息通过 Debug.Log 和直接修改字典副作用传递，
    /// 这使得单元测试必须 mock 整个 Dictionary 和日志系统。
    /// </summary>
    public interface IQuestProcessor
    {
        /// <summary>
        /// 设置链中下一个处理器，返回自身以支持流畅链接。
        /// SetNext 的返回值类型应为具体实现类而非接口才能完美流畅，
        /// 此处返回 IQuestProcessor 是合理的抽象选择。
        /// </summary>
        IQuestProcessor SetNext(IQuestProcessor processor);

        /// <summary>
        /// 处理消息并可能将其传递给下一个处理器。
        /// ⚠️ 传入可变 Dictionary 引用意味着每个处理器都有全局写权限，
        /// 无法保证处理器之间的隔离性，也无法追踪哪个处理器修改了哪个任务。
        /// </summary>
        void Process(QuestMessageBase message, Dictionary<SerializableGUID, Quest> quests);
    }

    /// <summary>
    /// 处理器基类：提供默认的"透传"行为。
    /// 子类若不重写 Process，消息将自动沿链向下游传递。
    /// 
    /// ⚠️ next?.Process 使用 null-conditional 运算符，
    /// 当到达链尾且无处理器匹配时，消息被静默丢弃，无任何反馈。
    /// 这是 COR 模式中最隐蔽的错误吞没点。
    /// </summary>
    public abstract class QuestProcessorBase : IQuestProcessor
    {
        private IQuestProcessor next;

        public IQuestProcessor SetNext(IQuestProcessor processor) => next = processor;

        public virtual void Process(QuestMessageBase message, Dictionary<SerializableGUID, Quest> quests) =>
            next?.Process(message, quests);
    }

    /// <summary>
    /// 泛型处理器基类：意图是通过类型约束自动过滤消息，避免手动 is 检查。
    /// 
    /// ⚠️ 关键问题：此类体为空，未实际实现泛型过滤逻辑！
    /// 预期的实现应类似：
    ///   public override void Process(QuestMessageBase msg, ...) {
    ///       if (msg is TMessage typed) HandleTyped(typed, quests);
    ///       else base.Process(msg, quests);
    ///   }
    /// 当前的空实现使其完全退化为普通 QuestProcessorBase，
    /// FailQuestProcessor 等子类仍在重复手写 is 检查，泛型抽象形同虚设。
    /// 这恰好说明了 OOP 继承体系在消息分发场景下的表达力不足。
    /// </summary>
    public class GenericQuestProcessor<TMessage> : QuestProcessorBase where TMessage : QuestMessageBase
    {
        // TODO: 缺少泛型分发的实际实现，子类仍被迫手动进行运行时类型检查
    }

    /// <summary>
    /// 任务失败处理器。
    /// 
    /// ⚠️ 三重运行时检查暴露了类型系统的无力：
    ///   1. message is FailQuestMessage — 消息类型在运行时才确定
    ///   2. TryGetValue — 任务是否存在在运行时才确定
    ///   3. quest.State == InProgress — 状态转换合法性在运行时才验证
    /// 任一检查失败都走不同分支，但所有分支最终都 return void，
    /// 调用方无法区分"成功失败"、"任务不存在"、"状态不允许"三种截然不同的语义。
    /// 
    /// ⚠️ 直接赋值 quest.State = Failed 是隐式副作用，
    /// 无法回滚、无法审计、无法在纯函数测试中验证。
    /// </summary>
    public class FailQuestProcessor : QuestProcessorBase
    {
        public override void Process(QuestMessageBase message, Dictionary<SerializableGUID, Quest> quests)
        {
            Debug.Log($"{GetType().Name}: Processing message of type {message.GetType().Name}");

            if (message is FailQuestMessage failMessage &&
                quests.TryGetValue(failMessage.QuestId, out var quest))
            {
                if (quest.State == QuestState.InProgress)
                {
                    quest.State = QuestState.Failed; // 💥 隐式可变状态变更
                    Debug.Log($"Quest '{quest.Name}' failed.");
                }
                // ⚠️ 状态不是 InProgress 时：静默忽略，无任何反馈
                
                return; // 消息已消费，不再传递
            }
            
            // 消息类型不匹配或任务不存在，传递给下一个处理器
            base.Process(message, quests);
        }
    }

    /// <summary>
    /// 任务完成处理器。结构与 FailQuestProcessor 高度重复。
    /// 
    /// ⚠️ 代码异味：三个处理器（Start/Complete/Fail）的骨架完全相同，
    /// 仅消息类型、目标状态、日志文本不同。这种重复正是泛型抽象试图解决但失败的，
    /// 也是函数式 Pattern Matching 能一行消除的：
    ///   match (message, quest.State) with
    ///   | (CompleteMsg, InProgress) -> Right(quest.WithState(Completed))
    ///   | (FailMsg, InProgress)     -> Right(quest.WithState(Failed))
    ///   | _                         -> Left(StateTransitionError(...))
    /// </summary>
    public class CompleteQuestProcessor : QuestProcessorBase
    {
        public override void Process(QuestMessageBase message, Dictionary<SerializableGUID, Quest> quests)
        {
            Debug.Log($"{GetType().Name}: Processing message of type {message.GetType().Name}");

            if (message is CompleteQuestMessage completeMessage &&
                quests.TryGetValue(completeMessage.QuestId, out var quest))
            {
                if (quest.State == QuestState.InProgress)
                {
                    quest.State = QuestState.Completed; // 💥 同样的隐式副作用
                    Debug.Log($"Quest '{quest.Name}' completed.");
                }

                return;
            }
            
            base.Process(message, quests);
        }
    }

    /// <summary>
    /// 任务启动处理器。链中的第一个节点。
    /// 
    /// ⚠️ 与 Complete/Fail 处理器的唯一区别是前置状态为 NotStarted。
    /// 三个处理器共享相同的"查找→检查状态→修改→日志"四步模板，
    /// 违反 DRY 原则。GenericQuestProcessor 本应提取此模板，但因实现缺失而失效。
    /// </summary>
    public class StartQuestProcessor : QuestProcessorBase
    {
        public override void Process(QuestMessageBase message, Dictionary<SerializableGUID, Quest> quests)
        {
            Debug.Log($"{GetType().Name}: Processing message of type {message.GetType().Name}");

            if (message is StartQuestMessage startMessage && 
                quests.TryGetValue(startMessage.QuestId, out var quest))
            {
                if (quest.State == QuestState.NotStarted)
                {
                    quest.State = QuestState.InProgress; // 💥 同样的隐式副作用
                    Debug.Log($"Quest '{quest.Name}' started.");
                }
                
                return;
            }
            
            base.Process(message, quests);
        }
    }

    /// <summary>
    /// 任务消息基类：仅携带 QuestId。
    /// 
    /// ⚠️ 继承体系的消息分发 vs 函数式 Discriminated Union：
    /// 当前设计新增消息类型需：1) 新建子类 2) 新建处理器 3) 修改链初始化
    /// 三处改动分散在不同文件，编译器不会提醒你遗漏了任何一处。
    /// 
    /// 函数式替代方案（C# 12+ sealed interface 或 abstract record）：
    ///   public abstract record QuestMessage(SerializableGUID QuestId);
    ///   public record StartQuest(SerializableGUID QuestId) : QuestMessage;
    ///   public record CompleteQuest(SerializableGUID QuestId) : QuestMessage;
    ///   public record FailQuest(SerializableGUID QuestId) : QuestMessage;
    /// 配合 switch expression 的穷尽性检查，新增消息时编译器强制要求处理所有分支。
    /// </summary>
    public abstract class QuestMessageBase
    {
        public SerializableGUID QuestId;
    }
    
    /// <summary>启动任务消息。无额外数据，仅靠类型标识语义。</summary>
    public class StartQuestMessage : QuestMessageBase { }
    
    /// <summary>完成任务消息。生产环境可能需要携带奖励数据、完成时间戳等。</summary>
    public class CompleteQuestMessage : QuestMessageBase { }
    
    /// <summary>失败任务消息。生产环境可能需要携带失败原因枚举、重试次数等。</summary>
    public class FailQuestMessage : QuestMessageBase { }
}