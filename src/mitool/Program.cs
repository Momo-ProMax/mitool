using System.Management;
using System.Runtime.InteropServices;

namespace mitool;

/// <summary>
/// mitool —— 小米/红米笔记本轻量化控制工具（走 ACPI-WMI 固件通道）
///
/// ── 设计红线（不可违反）──────────────────────────────────────────
///   1. 不碰 Windows 电源模式（Overlay / EPP / 电源计划），对电源侧只读
///   2. 不联动睡眠（不监听睡眠/唤醒事件，不阻止睡眠，不设唤醒定时器）
///   3. 只走固件通道（只调用厂商已对外暴露的 ACPI-WMI 接口）
///   4. 0 常驻 / 0 驱动 / 0 服务 / 0 托盘 —— 只在用户显式调用时动作
///
/// ── 协议来源（权威，非逆向推测）────────────────────────────────
///   Linux 主线驱动：drivers/platform/x86/bitland-mifs-wmi.c
///   内核文档     ：https://docs.kernel.org/wmi/devices/bitland-mifs-wmi.html
///   其 MOF 定义与本机 Get-CimClass 读出的完全一致（交叉验证）
///   充电功能号由 MiDeviceServiceEntry.dll 的立即数常量确认（见下）
///
/// ── 通道 ────────────────────────────────────────────────────
///   root\wmi : MICommonInterface.MiInterface(WmiMethodId = 1)
///   载体设备 ：ACPI\PNP0C14（实例 MIFS_0），服务 WmiAcpi（微软内置）
///   对象路径 ：MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'
///
/// ── 报文（32 字节 __packed）──────────────────────────────────
///   InData[0]  = reserved1   固定 0
///   InData[1]  = operation   0xFA = 读 / 0xFB = 写
///   InData[2]  = reserved2   固定 0
///   InData[3]  = function    功能号
///   InData[4..31] = payload
///   OutData[30]：返回数据位于 offset 4
///
/// ── 性能模式（function = 8）──────────────────────────────────
///   读：00 FA 00 08 00 …     写：00 FB 00 08 <码> 00 …
///   档位码：静谧 0x02 · 极速（高性能）0x03 · 智能（均衡）0x09 · 省电 0x0a
///
/// ── 充电（function = 0x10）───────────────────────────────────
///   读：00 FA 00 10 03 00 …            写：00 FB 00 10 02 00 <档位码> 00 …
///   （payload[0] 为子命令；档位码写于 InData[6]）
///   档位码（非百分比）：1 = 80%（开） · 0 = 关 · 4/5/6/7/8 = 其它档（未暴露）
///   ⚠️ 本机固件对读命令仅回显、不返回数据 ⇒ 充电参数是「只写」的；
///      厂商软件亦以注册表值为准，只做「写」下发。
///
/// 注：以上通道不依赖小米电脑管家。承载这些 WMI 类的是微软内置的
///     WmiAcpi 驱动，类定义来自固件 MOF —— 卸载厂商软件后依然可用。
/// </summary>
internal static class Program
{
    // ---------- 通道常量 ----------
    private const string Namespace = @"root\wmi";
    private const string ClassName = "MICommonInterface";
    private const string MethodName = "MiInterface";
    private const string ObjectPathVendor = @"MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'";
    private const int InDataLen = 32;

    // ---------- 操作码与功能号 ----------
    private const byte OpGet = 0xFA;   // 250 读
    private const byte OpSet = 0xFB;   // 251 写
    private const byte FnSystemPerMode = 0x08;   // 性能模式
    private const byte FnCharge = 0x10;          // 充电（保护 / 阈值）

    // ---------- 充电子命令与值时序 ----------
    private const byte ChargeSubRead = 0x03;    // payload[0]
    private const byte ChargeSubWrite = 0x02;   // payload[0]
    private const int ChargeValueOffset = 6;    // 写时 <档位码> 所在的 InData 偏移

    /// <summary>
    /// 充电保护档位码（固件代码，不是百分比）。
    /// 本工具按用户决定**固定两态**：开 = 80%（码 1），关 = 码 0。
    /// 来源（三方交叉验证）：
    ///   1. SvrCModule.dll 校验 `SetChargingProtect2`：合法集 = {0,1,4,5,6,7,8}（cmp/jbe 指令）
    ///   2. 前端 main.js：`gp` 枚举 = {One:1, Four:4, Five:5, Six:6, Seven:7, Eight:8}
    ///   3. 前端 main.js：`wp` Map = {8→"40%", 7→"50%", 6→"60%", 5→"70%", 1→"80%（推荐）"}
    /// 因此此前发 80/100（当成百分比）被固件丢弃，即「上限没生效」的根因。
    /// </summary>
    private const byte ChargeCodeOn = 1;    // 80%（厂商「推荐」档）
    private const byte ChargeCodeOff = 0;   // 关闭

    /// <summary>是否已带 --log（把每次命令与固件往返追加到 exe 旁的 mitool.log）。</summary>
    private static bool LogFlag;

    // ---------- 档位（本工具只暴露两档）----------
    /// <summary>短别名 → 规范名（"b/bal/balanced" 与 "p/perf/performance" 都接受）。</summary>
    /// <remarks>
    /// 别名仅在 `mode` 子命令下生效（如 `mitool mode b`、`mitool mode set perf`），
    /// 不在顶级命名空间生效 —— 即不接受 `mitool b` 这种单字母顶级指令。
    /// </remarks>
    private static readonly Dictionary<string, string> ModeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["b"] = "balanced",
        ["bal"] = "balanced",
        ["balanced"] = "balanced",
        ["p"] = "performance",
        ["perf"] = "performance",
        ["performance"] = "performance",
    };

    /// <summary>规范名 → 档位码（写入固件用）。</summary>
    private static readonly Dictionary<string, byte> ModeCodes = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
    {
        ["balanced"] = 0x09,      // 智能模式 = 均衡
        ["performance"] = 0x03,   // 极速模式 = 高性能
    };

    /// <summary>档位码 -> 可读名称（mode get 回读时用，含本工具不暴露的两档）。</summary>
    private static readonly Dictionary<byte, string> ModeNames = new Dictionary<byte, string>()
    {
        [0x02] = "静谧模式",
        [0x03] = "极速模式（高性能）",
        [0x09] = "智能模式（均衡）",
        [0x0a] = "省电模式",
    };

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        LogFlag = args.Any(x => x.Equals("--log", StringComparison.OrdinalIgnoreCase));

        if (args.Length == 0) { Help(); return 0; }

        Log("cmd: mitool " + string.Join(" ", args));

        var a = args.Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (a.Length == 0) { Help(); return 0; }

        try
        {
            return a[0].ToLowerInvariant() switch
            {
                "probe" => Probe(),
                "status" => Status(),
                "mode" => Mode(a),
                "charge" => Charge(a),
                "reset" => Reset(),
                "help" => HelpOk(),
                _ => HelpAndFail(),
            };
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("拒绝访问：访问 root\\wmi 厂商接口需要管理员权限，请以管理员身份运行。");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("错误：" + ex.Message);
            return 3;
        }
    }

    // ================= 核心：调用固件方法 =================

    private sealed record Result(int Rc, byte[]? Out, string? Err)
    {
        public bool Ok => Err == null;
    }

    private static string LogPath => Path.Combine(AppContext.BaseDirectory, "mitool.log");

    private static void Log(string msg)
    {
        if (!LogFlag) return;
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + msg + Environment.NewLine); }
        catch { /* 日志失败不影响主流程 */ }
    }

    private static Result Invoke(byte[] inData)
    {
        try
        {
            using var mc = new ManagementClass(Namespace, ClassName, null);
            using var inParams = mc.GetMethodParameters(MethodName);
            inParams["InData"] = inData;   // 原生数组绑定（PowerShell 做不到，必须用编译型语言）

            ManagementObject? target = null;
            foreach (ManagementBaseObject o in mc.GetInstances())
            {
                target = o as ManagementObject;
                if (target != null) break;
            }
            if (target == null)
                return new Result(0, null, "未找到 " + ClassName + " 实例（应绑定在 ACPI\\PNP0C14 上）");

            using (target)
            using (var outParams = target.InvokeMethod(MethodName, inParams, null))
            {
                int rc = 0;
                try { if (outParams["ReturnCode"] is { } v && v is not DBNull) rc = Convert.ToInt32(v); }
                catch { /* 保持 0 */ }

                var outData = outParams["OutData"] as byte[];
                Log($"  MiInterface in= {Hex(inData)}  rc={rc}  out= {Hex(outData)}");
                return new Result(rc, outData, null);
            }
        }
        catch (ManagementException me)
        {
            return new Result(0, null, $"WMI 0x{(int)me.ErrorCode:x8} {me.Message}");
        }
        catch (Exception e)
        {
            return new Result(0, null, e.Message);
        }
    }

    /// <summary>组装 MIFS 功能帧（32 字节）。</summary>
    private static byte[] PackFn(byte operation, byte function, byte? value)
    {
        var b = new byte[InDataLen];
        b[1] = operation;
        b[3] = function;
        if (value.HasValue) b[4] = value.Value;
        return b;
    }

    private static byte[] PackMode(byte code) => PackFn(OpSet, FnSystemPerMode, code);
    private static byte[] PackModeRead() => PackFn(OpGet, FnSystemPerMode, null);

    /// <summary>充电读：00 FA 00 10 03 00 …（sub 可换，用于探测子命令）</summary>
    private static byte[] PackChargeRead(byte sub = ChargeSubRead)
    {
        var b = new byte[InDataLen];
        b[1] = OpGet;
        b[3] = FnCharge;
        b[4] = sub;
        return b;
    }

    /// <summary>充电写：00 FB 00 10 02 00 &lt;value&gt; 00 …</summary>
    private static byte[] PackChargeWrite(byte value)
    {
        var b = new byte[InDataLen];
        b[1] = OpSet;
        b[3] = FnCharge;
        b[4] = ChargeSubWrite;
        b[ChargeValueOffset] = value;
        return b;
    }

    private static string Hex(byte[]? b) =>
        b == null ? "(null)" : (b.Length == 0 ? "(空)" : BitConverter.ToString(b).Replace('-', ' '));

    /// <summary>读当前档位码；失败返回 null。</summary>
    private static byte? ReadModeCode(out string? err)
    {
        err = null;
        var r = Invoke(PackModeRead());
        if (!r.Ok) { err = r.Err; return null; }
        if (r.Out is not { Length: > 4 }) { err = "固件返回数据不足 5 字节"; return null; }
        return r.Out[4];
    }

    // ================= probe =================

    private static int Probe()
    {
        var code = ReadModeCode(out _);
        Console.WriteLine(code.HasValue ? "通道：可用" : "通道：不可用");
        if (code.HasValue) Console.WriteLine($"档位：{ModeShortName(code.Value)}");
        return code.HasValue ? 0 : 1;
    }

    // ================= status =================

    private static int Status()
    {
        var code = ReadModeCode(out var err);
        Console.WriteLine(code.HasValue ? $"档位：{ModeShortName(code.Value)}" : $"档位：失败");

        if (GetSystemPowerStatus(out var p))
        {
            Console.WriteLine($"供电：{(p.ACLineStatus == 1 ? "插电" : "电池")}");
            Console.WriteLine($"电量：{p.BatteryLifePercent}%");
            Console.WriteLine($"Windows 视角充电中：{((p.BatteryFlag & 8) != 0 ? "是" : "否")}");
        }
        else
        {
            Console.WriteLine("供电：读取失败");
        }
        return code.HasValue ? 0 : 1;
    }

    // ================= mode =================

    private static int Mode(string[] a)
    {
        if (a.Length < 2)
        {
            Console.Error.WriteLine("用法: mitool mode get  |  mitool mode set <balanced|performance>（短别名：b / p）");
            return 1;
        }

        // 短别名直达：mitool mode b → 等价于 mitool mode set balanced
        // 仅在 `mode` 子命令作用域生效，不下沉到顶级（mitool b 不工作）
        if (ModeAliases.ContainsKey(a[1]))
        {
            return SetMode(a[1]);
        }

        switch (a[1].ToLowerInvariant())
        {
            case "get":
            {
                var code = ReadModeCode(out _);
                if (!code.HasValue) { Console.Error.WriteLine("档位：失败"); return 1; }
                Console.WriteLine($"档位：{ModeShortName(code.Value)}");
                return 0;
            }

            case "set":
            {
                if (a.Length < 3)
                {
                    Console.Error.WriteLine("用法: mitool mode set <balanced|performance>（短别名：b / p）");
                    return 1;
                }
                return SetMode(a[2]);
            }

            default:
                Console.Error.WriteLine("未知子命令，可用：get / set（短别名：b / p）");
                return 1;
        }
    }

    /// <summary>
    /// 写档位 + 回读确认。`alias` 可以是规范名或短别名。
    /// 被 `mitool mode set <alias>` 与 `mitool mode <alias>` 两条路径调用。
    /// </summary>
    private static int SetMode(string alias)
    {
        if (!ModeAliases.TryGetValue(alias, out var canonical))
        {
            Console.Error.WriteLine($"未知档位「{alias}」，可用：balanced / performance（短别名：b / bal · p / perf）");
            return 1;
        }
        var code = ModeCodes[canonical];

        var w = Invoke(PackMode(code));
        if (!w.Ok) { Console.Error.WriteLine($"切换失败：{w.Err}"); return 1; }

        var v = ReadModeCode(out _);
        bool ok = v.HasValue && v.Value == code;
        Console.WriteLine(ok ? $"已切换到：{ModeShortName(code)}" : "切换失败：固件未确认");
        return ok ? 0 : 1;
    }

    // ================= charge =================

    private static int Charge(string[] a)
    {
        if (a.Length < 2)
        {
            Console.Error.WriteLine("用法: mitool charge get | charge on | charge off");
            return 1;
        }

        switch (a[1].ToLowerInvariant())
        {
            case "get": return ChargeGet();
            case "on":  return ChargeWrite(ChargeCodeOn);
            case "off": return ChargeWrite(ChargeCodeOff);
            default:
                Console.Error.WriteLine("未知子命令，可用：get / on / off");
                return 1;
        }
    }

    /// <summary>
    /// 读充电状态。固件层读命令仅回显、不返回数据，无法读出当前充电保护开关状态；
    /// 这里只输出 Windows 侧供电信息。
    /// </summary>
    private static int ChargeGet()
    {
        Console.WriteLine("充电状态：固件层读不可用");
        if (GetSystemPowerStatus(out var p))
        {
            Console.WriteLine($"供电：{(p.ACLineStatus == 1 ? "插电" : "电池")}");
            Console.WriteLine($"电量：{p.BatteryLifePercent}%");
            Console.WriteLine($"Windows 视角充电中：{((p.BatteryFlag & 8) != 0 ? "是" : "否")}");
        }
        else
        {
            Console.WriteLine("供电：读取失败");
        }
        return 0;
    }

    /// <summary>写充电保护：1 = 开（80%）、0 = 关。</summary>
    private static int ChargeWrite(byte code)
    {
        var w = Invoke(PackChargeWrite(code));
        if (!w.Ok) { Console.Error.WriteLine($"设置失败：{w.Err}"); return 1; }
        Console.WriteLine(code == ChargeCodeOn ? "已开启充电保护（80%）" : "已关闭充电保护");
        return 0;
    }

    // ================= reset =================

    private static int Reset()
    {
        var w = Invoke(PackMode(ModeCodes["balanced"]));
        if (!w.Ok) { Console.Error.WriteLine($"重置失败：{w.Err}"); return 1; }
        Console.WriteLine("已恢复到：均衡");
        return 0;
    }

    /// <summary>档位码 → 简短名（用户面向：只给"均衡/高性能"，不带 0x09 等）。</summary>
    private static string ModeShortName(byte code) => code switch
    {
        0x02 => "静谧",
        0x03 => "高性能",
        0x09 => "均衡",
        0x0a => "省电",
        _ => "未知",
    };

    // ================= 平台调用 =================

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus s);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    // ================= help =================

    private static void Help()
    {
        Console.WriteLine("mitool —— 小米/红米笔记本轻量化控制工具（固件通道）");
        Console.WriteLine();
        Console.WriteLine("  mitool probe                        探测通道与能力");
        Console.WriteLine("  mitool status                       汇总：档位 + 充电 + 供电 + 电量");
        Console.WriteLine("  mitool mode get                     读当前档位");
        Console.WriteLine("  mitool mode set balanced            切到均衡档（智能模式）");
        Console.WriteLine("  mitool mode set performance         切到高性能档（极速模式）");
        Console.WriteLine("  mitool mode b                       同 mode set balanced（mode 子级短别名）");
        Console.WriteLine("  mitool mode p                       同 mode set performance");
        Console.WriteLine("  mitool charge get                   读充电状态（只读；本机固件仅回显）");
        Console.WriteLine("  mitool charge on                    开启充电保护（充至 80% 停止）");
        Console.WriteLine("  mitool charge off                   关闭充电保护");
        Console.WriteLine("  mitool reset                        档位恢复为均衡");
        Console.WriteLine();
        Console.WriteLine("档位短别名（仅 mode 子命令下生效）：b / bal / balanced · p / perf / performance");
        Console.WriteLine();
        Console.WriteLine("全局开关：--log 把命令与固件往返记入 exe 旁的 mitool.log");
        Console.WriteLine();
        Console.WriteLine("说明：需管理员权限；写入档位后会自动回读确认；写入充电保护后请用实际充电行为验证。");
        Console.WriteLine("红线：不碰 Windows 电源模式；不联动睡眠；只走固件通道；不常驻。");
    }

    private static int HelpOk() { Help(); return 0; }
    private static int HelpAndFail() { Help(); return 1; }
}
