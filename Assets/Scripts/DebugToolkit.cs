using System;
using UnityEngine;

namespace COR
{
    /// <summary>
    /// 调试工具包：基于责任链的多目标日志分发器。
    /// 
    /// ⚠️ 架构定位：
    /// 与 QuestManager 的"独占消费"链不同，此日志链是"广播消费"模型——
    /// 每条消息预期被所有处理器处理（控制台+文件+状态保存），而非被第一个匹配者拦截。
    /// 这意味着 NullCheckProcessor 不应 return 终止传递，而应始终调用 base.Process。
    /// 
    /// ⚠️ 函数式视角的核心缺陷：
    /// 1. Log 返回 void：调用方无法得知日志是否成功写入文件、是否因 null 被过滤
    /// 2. FileLogProcessor 持有文件句柄副作用：无法在纯函数测试中验证
    /// 3. StateSaveProcessor 语义模糊："保存状态"与"记录日志"混在同一管线中，违反单一职责
    /// 4. 链结构硬编码：无法运行时动态启用/禁用某个日志目标（如 Release 构建关闭文件日志）
    /// </summary>
    public class DebugToolkit : MonoBehaviour
    {
        /// <summary>
        /// 日志文件路径，通过 Inspector 配置。
        /// ⚠️ 相对路径在 Unity 中解析行为依赖平台：
        ///   - Editor: 相对于项目根目录
        ///   - Standalone: 相对于可执行文件目录
        ///   - Android/iOS: 可能无写权限导致 FileLogProcessor 静默失败
        /// 函数式改进：将路径解析与IO操作分离，构造时验证路径有效性并返回 Either。
        /// </summary>
        [SerializeField] private string logFilePath = "debug_log.txt";

        /// <summary>
        /// 日志处理链头节点。
        /// 链顺序隐含优先级假设：Null检查 → 控制台 → 文件 → 状态保存。
        /// ⚠️ 若 FileLogProcessor 抛出异常且未捕获，后续 StateSaveProcessor 将被跳过，
        /// 且 Log 方法的 void 返回值使调用方对此完全无感知。
        /// </summary>
        private IDebugProcessor chain;

        /// <summary>
        /// 初始化日志处理链。
        /// 
        /// ⚠️ 设计观察：
        /// NullCheckProcessor 作为链首是防御性编程的命令式表达。
        /// 在函数式范式中，null 检查应在类型层面消除（Optional/NonNullable），
        /// 而非作为运行时管线的第一步。此处理器的存在本身就是类型系统不足的补丁。
        /// 
        /// ⚠️ StateSaveProcessor 的命名暗示它执行的是持久化副作用，
        /// 而非纯粹的日志记录。将其放在日志链中混淆了"观测"与"变更"的边界。
        /// 函数式重构应将日志（Reader/Writer Effect）与状态保存（State Monad）分离。
        /// </summary>
        private void Awake()
        {
            chain = new NullCheckProcessor();
            chain.SetNext(new ConsoleLogProcessor())
                .SetNext(new FileLogProcessor(logFilePath))
                .SetNext(new StateSaveProcessor());
        }

        /// <summary>
        /// 向日志管线发送消息。
        /// 
        /// ⚠️ void 返回值意味着：
        ///   - 文件写入失败？不知道。
        ///   - 消息被 NullCheck 过滤？不知道。
        ///   - StateSave 抛异常？被吞没或崩溃，取决于处理器内部是否有 try-catch。
        /// 
        /// 函数式替代签名示例：
        ///   public LogResult Log(DebugMessage message)
        ///   其中 LogResult 包含每个处理器的成功/失败状态，
        ///   或使用 Writer<LogEntry, Unit> Monad 将日志作为可组合的计算上下文。
        /// </summary>
        public void Log(DebugMessageBase message) => chain.Process(message);
    }
}