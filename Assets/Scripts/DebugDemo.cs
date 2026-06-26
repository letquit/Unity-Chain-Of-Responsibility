using System;
using UnityEngine;

namespace COR
{
    /// <summary>
    /// 调试日志系统的运行时演示组件。
    /// 
    /// ⚠️ 与 QuestDemo 的关键区别：
    /// QuestDemo 的三条消息中有一条是"非法操作被静默吞没"；
    /// 此处的三条消息预期全部被所有处理器消费（广播模型），
    /// 包括 null 消息——它被 NullCheckProcessor 显式处理而非视为错误。
    /// 这体现了日志 COR 与业务 COR 在语义上的根本差异。
    /// 
    /// ⚠️ 函数式视角的核心缺陷：
    /// 1. Log 返回 void：调用方无法确认文件写入是否成功、null 是否被正确过滤
    /// 2. null 作为合法参数：应使用 Optional<DebugMessageBase> 在类型层面表达
    /// 3. StateSaveMessage 混入日志管线："保存状态"不是"记录日志"，职责混淆
    /// 4. PlayerData 作为消息载荷：序列化/反序列化逻辑隐含在处理器中，不可测试
    /// </summary>
    public class DebugDemo : MonoBehaviour
    {
        /// <summary>
        /// 通过 Inspector 注入的调试工具包引用。
        /// ⚠️ 未添加 null 安全检查：若 Inspector 中未赋值，
        /// Start 中将抛出 NullReferenceException。
        /// 函数式改进：启动时验证依赖完整性，或使用 Optional<DebugToolkit> + Match。
        /// </summary>
        [SerializeField] private DebugToolkit debugToolkit;

        private void Start()
        {
            // ═══════════════════════════════════════════
            // 消息1: 通用文本日志
            // ═══════════════════════════════════════════
            
            // GeneralDebugMessage 是最纯粹的日志消息类型。
            // ⚠️ 调用方无法从返回值确认此消息是否成功写入文件、
            // 是否因 IO 异常而丢失。void API 使日志可靠性完全不可验证。
            debugToolkit.Log(new GeneralDebugMessage("Application started."));

            // ═══════════════════════════════════════════
            // 消息2: 状态保存消息 — 职责边界模糊
            // ═══════════════════════════════════════════
            
            // StateSaveMessage 携带结构化数据 (PlayerData)，
            // 由 StateSaveProcessor 执行持久化副作用。
            // 
            // ⚠️ 设计异味：这条消息的本质是"命令"（保存玩家状态），
            // 而非"日志"（记录诊断信息）。将其放入日志管线导致：
            //   - ConsoleLogProcessor 可能输出一串无意义的序列化文本
            //   - FileLogProcessor 可能将二进制数据写入文本日志文件
            //   - 日志链的任何异常都可能阻断状态保存这一关键业务操作
            // 
            // 函数式重构方向：将状态持久化分离为独立的 Effect/Command，
            // 日志系统仅接收纯诊断消息。两者通过组合子并行执行而非串行耦合。
            debugToolkit.Log(new StateSaveMessage("player_state", new PlayerData(100, Vector3.zero)));

            // ═══════════════════════════════════════════
            // 消息3: null 消息 — 防御性编程的命令式表达
            // ═══════════════════════════════════════════
            
            // null 在此处是"预期内的合法输入"，由 NullCheckProcessor 处理。
            // 
            // ⚠️ 类型系统缺陷：
            // C# 允许将 null 传给任何引用类型参数，编译器不会警告。
            // 调用方无法从方法签名判断 null 是否安全，只能阅读文档或源码。
            // 
            // 函数式替代方案：
            //   public void Log(Optional<DebugMessageBase> message)
            // 或更彻底地消除 null 可能性：
            //   public void Log(DebugMessageBase message) // NonNullable 约定
            //   + 调用方自行决定是否调用 Log（而非传 null 让接收方判断）
            debugToolkit.Log(null);
        }
    }

    /// <summary>
    /// 玩家数据结构体。
    /// 
    /// ✅ 正面评价：使用 struct 而非 class 是正确的值语义选择，
    /// 避免了不必要的堆分配和 null 引用风险。
    /// [Serializable] 确保 Unity Inspector 可编辑和 JsonUtility 可序列化。
    /// 
    /// ⚠️ 可变字段警告：health 和 position 为 public 可变字段。
    /// 在函数式重构中应改为 readonly 字段或 record struct，
    /// 通过 with 表达式创建修改后的副本：
    ///   var damaged = playerData with { health = playerData.health - 10 };
    /// 
    /// ⚠️ Vector3 的序列化注意事项：
    /// JsonUtility 对 Vector3 的支持有限，生产环境可能需要自定义序列化适配器。
    /// </summary>
    [Serializable]
    public struct PlayerData
    {
        public int health;
        public Vector3 position;

        /// <summary>
        /// 显式构造函数确保所有字段在构造时被初始化。
        /// ⚠️ struct 的默认无参构造函数始终存在且将所有字段置零，
        /// 无法被禁用。这意味着 default(PlayerData) 是 health=0, position=(0,0,0) 的有效值。
        /// 若业务上 health=0 表示"无效状态"，应在类型层面用 Optional<PlayerData> 
        /// 或 NonZeroHealth 包装类型来表达，而非依赖运行时检查。
        /// </summary>
        public PlayerData(int health, Vector3 position)
        {
            this.health = health;
            this.position = position;
        }
    }
}