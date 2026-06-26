using System;
using System.IO;
using UnityEngine;

namespace COR
{
    /// <summary>
    /// 调试处理器接口：定义日志管线的节点契约。
    /// 
    /// ⚠️ 与 IQuestProcessor 的关键语义差异：
    /// 任务链中 Process 可能终止传递（独占消费）；
    /// 日志链中 Process 应始终调用 base.Process（广播消费）。
    /// 但接口签名完全相同（void + 单参数），无法从类型层面区分这两种行为。
    /// 
    /// ⚠️ void 返回值使调用方无法得知：
    ///   - 文件写入是否成功
    ///   - null 检查是否触发了过滤
    ///   - 状态保存是否因异常被跳过
    /// 所有结果信息通过 Debug.Log/ErrorLog 副作用传递，不可组合、不可断言。
    /// </summary>
    public interface IDebugProcessor
    {
        /// <summary>
        /// 设置下一个处理器，返回自身以支持流畅链接。
        /// ✅ 正面评价：DebugProcessorBase 中对 null 参数做了防御性检查，
        /// 比 QuestProcessorBase 更健壮。
        /// </summary>
        IDebugProcessor SetNext(IDebugProcessor processor);

        /// <summary>
        /// 处理调试消息并传递给下一个处理器。
        /// ⚠️ 缺少 CancellationToken 参数：文件 IO 操作无法被取消，
        /// 在 Unity 主线程上执行时可能导致帧率卡顿。
        /// </summary>
        void Process(DebugMessageBase message);
    }

    /// <summary>
    /// 处理器基类：提供默认的"透传"行为和链式连接逻辑。
    /// 
    /// ✅ 优于 QuestProcessorBase 的设计：
    /// SetNext 中对 null 参数抛出 ArgumentNullException，
    /// 在构建阶段就暴露配置错误，而非运行时静默断链。
    /// 
    /// ⚠️ next?.Process 仍使用 null-conditional 运算符：
    /// 当到达链尾时消息被静默丢弃。对于广播型日志链这是合理的，
    /// 但对于 StateSaveProcessor 这类关键操作，静默丢弃可能是灾难性的。
    /// </summary>
    public abstract class DebugProcessorBase : IDebugProcessor
    {
        private IDebugProcessor next;

        public IDebugProcessor SetNext(IDebugProcessor processor)
        {
            return next = processor ?? throw new ArgumentNullException(nameof(processor));
        }

        public virtual void Process(DebugMessageBase message) => next?.Process(message);
    }

    /// <summary>
    /// Null 检查处理器：链首防御节点。
    /// 
    /// ⚠️ 存在即原罪：
    /// 此处理器的必要性证明了 DebugMessageBase 类型允许 null 是设计缺陷。
    /// 在函数式范式中，应在类型层面消除 null 可能性：
    ///   - 使用 NonNullable 约定 + C# nullable reference types
    ///   - 或使用 Optional&lt;DebugMessageBase&gt; 让调用方显式处理 None
    /// 而非在运行时管线的第一步做命令式检查。
    /// 
    /// ⚠️ return 终止传递 vs base.Process 继续传递：
    /// 此处选择 return 是正确的（null 消息不应被下游处理器消费），
    /// 但调用方无法区分"消息被 null 检查过滤"和"消息正常处理完毕"。
    /// </summary>
    public class NullCheckProcessor : DebugProcessorBase
    {
        public override void Process(DebugMessageBase message)
        {
            if (message == null || message.Message == null)
            {
                Debug.LogError("NullCheckProcessor: Null message detected!");
                return; // 终止传递，保护下游处理器
            }

            base.Process(message); // 非 null 则继续广播
        }
    }

    /// <summary>
    /// 控制台日志处理器：最纯粹的日志节点。
    /// 
    /// ✅ 正确实现了广播语义：处理后始终调用 base.Process。
    /// ⚠️ Debug.Log 本身是 Unity 主线程同步调用，高频日志可能造成性能瓶颈。
    /// 函数式改进：将日志收集为纯数据列表，批量异步刷新到控制台。
    /// </summary>
    public class ConsoleLogProcessor : DebugProcessorBase
    {
        public override void Process(DebugMessageBase message)
        {
            Debug.Log($"ConsoleLogProcessor: {message.Message}");
            base.Process(message); // 始终继续传递
        }
    }

    /// <summary>
    /// 状态保存处理器：⚠️ 整个文件中最严重的设计问题。
    /// 
    /// 🔴 职责违反：这不是日志处理器，而是持久化命令执行器。
    /// 将其放在日志链中意味着：
    ///   1. 文件 IO 异常可能影响后续日志记录（尽管有 try-catch）
    ///   2. 日志频率决定了存档频率，两者生命周期不应耦合
    ///   3. ConsoleLogProcessor 会输出 "Save State: xxx" 文本，污染诊断日志
    /// 
    /// 🔴 类型不安全的序列化：
    /// StateData 类型为 object，JsonUtility.ToJson(object) 在 Unity 中行为不可靠：
    ///   - 不支持多态序列化
    ///   - 不支持 Dictionary/HashSet
    ///   - 私有字段被忽略
    /// 编译期完全无法验证传入的数据类型是否可序列化。
    /// 
    /// 🔴 文件名拼接无验证：
    /// $"{stateMessage.StateData}_state.json" 直接将对象 ToString() 作为文件名，
    /// PlayerData 的默认 ToString() 返回类型名而非有意义标识，
    /// 且未过滤非法文件名字符（/\:*?"<>|）。
    /// 
    /// 函数式重构方向：
    /// 将状态保存提取为独立的 Effect/Command，接受强类型泛型参数：
    ///   SaveState&lt;T&gt;(string name, T data) where T : ISerializable
    /// 返回 Either&lt;SaveError, Unit&gt;，与日志系统完全解耦。
    /// </summary>
    public class StateSaveProcessor : DebugProcessorBase
    {
        public override void Process(DebugMessageBase message)
        {
            if (message is StateSaveMessage stateMessage)
            {
                // ⚠️ 文件名由 StateData.ToString() 生成，几乎必然产生无效或无意义文件名
                string filePath = $"{stateMessage.StateData}_state.json";
                try
                {
                    // ⚠️ JsonUtility.ToJson(object) 对非 MonoBehaviour/纯数据类支持极差
                    string json = JsonUtility.ToJson(stateMessage.StateData);
                    // ⚠️ 同步文件写入阻塞主线程，大状态数据可导致帧率骤降
                    File.WriteAllText(filePath, json);
                    Debug.Log($"StateSaveProcessor: State '{stateMessage.StateName}' saved to {filePath}");
                }
                catch (Exception e)
                {
                    // ⚠️ 异常被捕获并转为日志，调用方无法程序化响应保存失败
                    Debug.LogError($"StateSaveProcessor: Failed to save state '{stateMessage.StateName}'. Error: {e.Message}");
                }

                base.Process(message); // 即使保存失败也继续传递日志
            }
            // ⚠️ 非 StateSaveMessage 时不调用 base.Process！
            // 这是一个隐蔽的 bug：如果 StateSaveProcessor 不在链尾，
            // 普通日志消息将被此处理器静默吞没，后续处理器永远收不到。
            // 应改为：if (...) { ... } base.Process(message); （将 base 移到 if 外部）
        }
    }

    /// <summary>
    /// 文件日志处理器：持久化诊断日志节点。
    /// 
    /// ⚠️ File.AppendAllText 每次调用都打开/关闭文件句柄：
    /// 高频日志场景下 IO 开销巨大。应使用 StreamWriter 保持句柄，
    /// 或在内存中缓冲后定期批量写入。
    /// 
    /// ⚠️ 无并发保护：多线程或多实例同时写入同一文件会导致内容交错或异常。
    /// 
    /// ⚠️ DateTime.Now 格式未指定：不同区域设置下日志时间戳格式不一致，
    /// 解析困难。应使用 DateTime.UtcNow.ToString("o") 或 ISO 8601 格式。
    /// 
    /// ✅ 正确实现了广播语义：try-catch 包裹后始终调用 base.Process，
    /// 确保文件写入失败不会阻断后续处理器。
    /// </summary>
    public class FileLogProcessor : DebugProcessorBase
    {
        private string logFilePath;

        public FileLogProcessor(string logFilePath)
        {
            this.logFilePath = logFilePath;
        }

        public override void Process(DebugMessageBase message)
        {
            try
            {
                File.AppendAllText(logFilePath, $"{DateTime.Now}: {message.Message}\n");
            }
            catch (Exception e)
            {
                Debug.LogError($"FileLogProcessor: Failed to write to log file. Error: {e.Message}");
            }

            base.Process(message); // 始终继续传递
        }
    }

    /// <summary>
    /// 调试消息基类：携带只读 Message 属性。
    /// 
    /// ✅ 优于 QuestMessageBase 的设计：
    /// Message 通过构造函数注入且只有 getter，保证了不可变性。
    /// 
    /// ⚠️ 继承体系的消息分发 vs 函数式 Discriminated Union：
    /// 新增消息类型需新建子类 + 修改对应处理器，编译器不会提醒遗漏。
    /// 函数式替代（C# 12+ sealed interface / abstract record）配合 switch expression
    /// 可获得穷尽性检查，新增消息时编译器强制要求处理所有分支。
    /// </summary>
    public abstract class DebugMessageBase
    {
        public string Message { get; }

        protected DebugMessageBase(string message) => Message = message;
    }

    /// <summary>通用文本日志消息。纯诊断用途，无额外载荷。</summary>
    public class GeneralDebugMessage : DebugMessageBase
    {
        public GeneralDebugMessage(string message) : base(message) { }
    }

    /// <summary>
    /// 状态保存消息：⚠️ 再次强调，这不应是日志消息的子类型。
    /// 
    /// ⚠️ StateData 类型为 object：丢失了所有编译期类型安全。
    /// 任何类型都可传入，但只有少数类型能被 JsonUtility 正确序列化。
    /// 这是 COR 模式中"消息基类过度抽象"的典型症状——
    /// 为了适配统一接口，牺牲了具体消息的类型精确性。
    /// 
    /// 函数式替代：
    ///   public record StateSaveCommand&lt;T&gt;(string Name, T Data) where T : ISerializable;
    /// 泛型约束在编译期阻止不可序列化的类型进入管线。
    /// </summary>
    public class StateSaveMessage : DebugMessageBase
    {
        public string StateName { get; }
        public object StateData { get; }

        public StateSaveMessage(string stateName, object stateData)
            : base($"Save State: {stateName}")
        {
            StateName = stateName;
            StateData = stateData;
        }
    }
}