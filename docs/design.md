# mitool 设计方案

**替代臃肿整机管家的轻量化系统调整工具**

> 一句话定位：用最小的用户态程序，直接调用厂商已暴露的固件接口，只做两件事 —— **性能档位切换** 和 **电池充电管理**。

| | |
|---|---|
| 版本 | v1.3 |
| 日期 | 2026-09-25 |
| 目标机型 | 小米/红米笔记本（本机：Redmi Book 14 2025 / Ryzen 7 7735H） |
| 原理 | 复用系统 WmiAcpi 通道，不装驱动、不装服务 |

---

## 0. 设计红线（三条，不可违反）

| # | 红线 | 含义 |
|---|---|---|
| 1 | **不碰 Windows 电源模式** | 永不写电源计划、电源模式 Overlay、EPP；只读 |
| 2 | **不联动睡眠** | 不做睡眠/休眠策略；不监听睡眠/唤醒事件；不阻止系统睡眠；不设唤醒定时器 |
| 3 | **只走固件通道** | 只用厂商已对外暴露的 ACPI-WMI 接口；不注入、不 hook、不写驱动 |

**由红线推出的硬约束**：机型不支持固件方法时，功能标记为「本机不支持」，**不允许退化成去改 Windows 电源设置**。宁缺勿滥。

---

## 1. 形态与资源占用

最终形态 = 三个文件，**没有任何常驻组件**：

```
mitool.exe        单文件可执行程序（带 requireAdministrator 清单）
profiles.json     机型 → 通道类型 / 档位编码表 / 已知坑
state.json        写入前快照，用于一键回滚
```

| 维度 | 现管家（本机实测） | mitool |
|---|---|---|
| 常驻进程 | 8 个 | **0** |
| 常驻内存 | 330.7 MB | **0**（仅命令执行期 < 10 MB） |
| 内核驱动 | 2 个 | **0** |
| 自启服务 | 10 个 | **0** |
| 安装体积 | 1878.4 MB | **< 1 MB** |
| 唤醒后动作 | 回灌蓝牙省电/互联省电/充电保护 | **无动作** |
| 磁盘 IO | 持续写日志 | 默认静默（`--verbose` 才写） |

---

## 2. 为什么可行（本机实测依据）

厂商把控制接口暴露成了**标准 WMI 门面**，位于 `root\wmi` 命名空间：

| WMI 类 | 方法 | 参数签名 | 用途 |
|---|---|---|---|
| `MICommonInterface` | `MiInterface` | `InData(uint8[]) / OutData(uint8[]) / ReturnCode(uint16)` | **字节隧道**：IFOK、REGSET、GET/SET_CHARGING_PROTECT、OS_TURBO_CMD_* 等由此进出 |
| `HQWmiCommonInterface` | `SetPerformanceMode` 等 11 个 | `req(string) / ret(string)` | **ODM（华勤）BIOS 方法**，字符串收发，语义清晰 |

- **载体设备**：`ACPI\PNP0C14`，3 个实例（`0` / `AOD` / `MIFS`），由系统 `WmiAcpi` 服务承载
- **固件方法名表**（从 `SvrCModule.dll` 串池提取）：
  `IFOK` · `REGSET` · `LAST_MODE` · `GET/SET_BATTERY_ORIGINAL_INFO` · `GET_BATTERY_STATUS` · `GET/SET_CHARGING_PROTECT` · `MI_CONTROL_FLAG` · `OS_TURBO_CMD_{QUERY,REPLY,START,STOP}`
- **权限实测**：非管理员访问上述两个类均返回**「拒绝访问」**
- **结论**：纯用户态即可实现，**不需要自写内核驱动**

---

## 3. 架构（只有一个 Provider）

```
mitool.exe
 │
 ├─ 命令解析层
 │
 ├─ 业务层
 │    ├─ 档位逻辑：幂等判断 · 能力校验 · 回读确认 · 失败回滚
 │    └─ 充电逻辑：阈值读写 · 护栏 · 保护开关
 │
 └─ 通道层（唯一写入口）
      └─ 固件 Provider ──> MICommonInterface / HQWmiCommonInterface
                │
                └─ 目标：固件 → EC / SMU 寄存器
```

**没有第二层通道**——原设计中的「Windows 标准降级通道」已按红线 1 删除。

---

## 4. 命令接口

```bash
mitool probe                    # 探测通道与能力（枚举类/方法 + 读当前档位）
mitool status                   # 汇总：当前档位 + 充电话路 + 供电 + 电量
mitool mode get                 # 读当前档位（解析 OutData[4]）
mitool mode set balanced        # 切到均衡档（写后自动回读确认）
mitool mode set performance     # 切到高性能档（写后自动回读确认）
mitool mode b                   # 同 mode set balanced（mode 子级短别名）
mitool mode p                   # 同 mode set performance
mitool charge get               # 读充电状态（只读；本机固件仅回显）
mitool charge on                # 开启充电保护（固定 80%）
mitool charge off               # 关闭充电保护
mitool reset                    # 档位恢复均衡
```

档位短别名：

| 短别名 | 等价规范名 | 档位码 |
|---|---|---|
| `b` / `bal` / `balanced` | `balanced` | `0x09` |
| `p` / `perf` / `performance` | `performance` | `0x03` |

全局开关：

| 开关 | 作用 |
|---|---|
| `--log` | 把每次命令与固件往返追加到 `mitool.exe` 旁的 `mitool.log`，便于事后取证 |

> 说明：早期版本的 `fn get/set`、`raw`、`dump`、`--dry-run`、`--json` 已在代码清理中移除，
> 只保留用户实际会用到的命令。

### 只保留两个档位

工具只暴露用户真正会用到的两档，不给认知添负担：

| 本工具档位 | 固件档位 | **档位码**（function 8 的取值） | 定位 |
|---|---|---|---|
| `balanced`（均衡） | 智能模式 | **`0x09`** | 日常默认，性能/噪音/温度三者兼顾 |
| `performance`（高性能） | 极速模式 | **`0x03`** | 需要性能时：游戏、渲染、编译 |

映射写在 `profiles.json`，**不硬编码**（BIOS 更新可能改表）。

两点说明：

- 固件另外两档（静谧 `0x02`、省电 `0x0a`）**本工具不暴露**。若被系统快捷键（FN+K）或管家切到，`mode get` 会**如实报出**并显示其名称，**不擅自改回**。
- 高性能档在**电池供电**时可能受固件自身策略限制（固件的狂暴模式明确要求插电）。工具不干预固件判断，只在探测到电池供电时提示一句「高性能档建议插电使用」。

---

## 5. 性能档位切换：实现思路

### 写入流程

```
用户执行 mitool mode set performance
   │
   ├─ 1. 读当前档位 ──── 相同则直接返回（幂等，不做无谓写入）
   │
   ├─ 2. 探测固件方法是否可用
   │      可用   → 走固件 Provider
   │      不可用 → 报「本机不支持」，退出（禁止降级去改 Windows 电源设置）
   │
   ├─ 3. 写入前存快照到 state.json
   │
   ├─ 4. 下发 32 字节 InData：00 FB 00 08 <档位码> 00 × 27
   │
   └─ 5. 回读确认 → 失败重试一次 → 仍失败则回滚上一档
```

> 固件自己已带幂等（`Break by LastMode = CurMode`），工具侧的幂等判断只是为了少发一次调用。

### 两条写入通道

| 优先级 | 通道 | 状态 |
|---|---|---|
| **唯一在用的** | **`MICommonInterface.MiInterface(InData, OutData, ReturnCode)`** | ✅ **格式已完全解出**（见 §5.1）——管家真正写档位走的就是它 |
| 备选（未采用） | `HQWmiCommonInterface.SetPerformanceMode(req, ret)` | 字符串收发，`WmiMethodId=9`；但管家并不用它调档位，语义与实际固件行为无法互证，**暂不使用** |

## 5.1 ⭐ 协议（最终确认 · 实测验证通过 2026-09-25 17:40）

### 权威来源：Linux 主线驱动 `bitland-mifs-wmi`

**本协议的最终依据不是逆向，而是 Linux 内核主线驱动及其官方文档** —— 它支持的正是小米/红米笔记本的 **MIFS WMI 接口**：

| | |
|---|---|
| 源码 | `drivers/platform/x86/bitland-mifs-wmi.c`（Linux 主线） |
| 文档 | https://docs.kernel.org/wmi/devices/bitland-mifs-wmi.html |

文档给出的 MOF 定义，与本机 `Get-CimClass` 读出的**完全一致**（交叉验证成功）：

```
[WMI, Dynamic, provider("WmiProv"), guid("{b60bfb48-3e5b-49e4-a0e9-8cffe1b3434b}")]
class MICommonInterface {
  [key, read] string InstanceName;
  [read] boolean Active;
  [WmiMethodId(1), Implemented, read, write]
  void MiInterface([in] uint8 InData[32], [out] uint8 OutData[30], [out] uint16 Reserved);
};
```

### 对象路径（厂商逐字）

```
MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'        ← 单引号 + 单反斜杠
命名空间 root\wmi，方法 MiInterface（WmiMethodId = 1）
```

### 报文格式（32 字节，`__packed`）

```
InData（32 字节）:
  偏移   0         1             2          3          4 .. 31
         reserved1 operation     reserved2  function   payload
         0         0xFA=读/0xFB=写  0          功能号     数据

OutData（30 字节）:
  00 80 00 08 <值> 00 …         → 返回数据在 offset 4
```

### 性能模式（function = 8）

```
读:  00 FA 00 08 00 …（32B）   →   OutData[4] = 当前档位码
写:  00 FB 00 08 <码> 00 …（32B）
```

| 档位 | 档位码 |
|---|---|
| 静谧 | `0x02` |
| 极速（高性能） | `0x03` |
| **智能（均衡）** | **`0x09`** |
| 省电 | `0x0a` |

### 其它已确认的功能号

| function | 用途 | 返回位置 |
|---|---|---|
| 13 | 三路风扇转速 | `data[0]/[2]/[6]`，LE16（CPU / GPU / SYS） |
| 19 | 电源适配器类型 | `data[0]`：1 = Type-C，2 = DC |
| 22 | CPU 温度 | `data[0]` |
| 23 | **CPU 功耗** | `data[0]`（文档注明仅 Redmi 机型可用） |

### 实测证据（2026-09-25 17:35~17:40）

```
输入: raw 00 FA 00 08
输出: ReturnCode = 0
      OutData    = 00 80 00 08 09 00 …      → 档位 0x09（智能 / 均衡）
```

写入侧：`mode set` 写入后自动回读，值一致。

### ⚠️ 对早前结论的修正（重要，避免后人重蹈）

| 早前结论 | 实际 |
|---|---|
| 载荷格式是 `01 16 <码> 00…` | ❌ **完全错误**。那是对管家日志 `HandleRegResult:01160200…` 的误读 —— **它是 EC 寄存器层的中间表示，不是 WMI 的 InData** |
| `0x8004100F` = 「已执行但响应编组失败」 | ❌ 实测 = **固件拒绝非法请求**（载荷格式不对） |
| 档位码：智能 = 02，静谧 = 09 | ❌ 弄反了。实测：智能 = **09**，静谧 = **02** |
| 「写入生效、性能下降 60%」 | ❌ 误判。该差异来自**中途更换了 bench 的负载实现**，基线不可比 |
| 「必须先发 `01 28 01` 接管才能写」 | ❌ 无此必要；管家 UI 路径也不发它，照样写成功 |
| 「管家日志可作读数裁判」 | ⚠️ 仅**用户操作**路径可；启动/唤醒路径是「先写后记录」，且其启动写入实测未生效 |

**根本教训**：**先搜开源实现（Linux 主线驱动 / 内核文档），再考虑逆向。** 本次在拿到这套文档之前，先做了「从厂商日志猜报文 → 反汇编 → 试载荷」的完整流程，得出的格式是错的，且在错前提下耗掉十几轮。

### 卸载小米电脑管家后还能用吗？—— **能**

**证据**：承载这些 WMI 类的是 `WmiAcpi`（`InfPath=wmiacpi.inf`、`ProviderName=Microsoft`），**微软内置驱动**；小米安装目录里**没有任何 `.mof` 文件**。类定义来自**固件 MOF**，由微软的 `WmiAcpi` 在启动时读出并注册 —— 厂商软件只是使用者。

**最直接的实证**：本项目的全部 WMI 探测，都是用**我们自己的进程**跑的（非 MI 进程），类、方法签名、实例信息全都能拿到。

**卸载后会失去**：管家日志（诊断参考）、模式联动项（蓝牙省电 / 互联省电 / 充电保护）、场景识别服务（含会「抢方向盘」的 `SPPTControlThread`）、FN+K 热键的接收方（**未实测**）。

**建议的验证方式（可逆，优于真卸载）**：临时停止 MI 的进程与服务 → 再次访问 WMI 类 → 恢复原状。

---

## 6. 电池充电管理：实现思路

### 功能归属（决定实现路线，重要）

**充电阈值 / 充电保护的本体在固件 + EC（充电控制器），不在 Windows。**

| 层 | 角色 |
|---|---|
| EC / 充电控制器 | **真正的执行者**：决定何时停止充电；**关机 / 睡眠时依然生效** |
| UEFI / BIOS 固件 | 提供控制接口（部分机型直接在 BIOS 里给出开关） |
| **OS（Windows）** | **仅透传**。本机枚举 25 个 Windows 原生电池/电源类（`BatteryStatus`、`BatteryFullChargedCapacity`、`MSPower`…），**全部「无方法，只读属性」** —— 没有任何"写充电阈值"的接口；`powercfg` 也只管电源计划与睡眠，**不涉及充电上限** |
| 厂商应用 | 调用厂商私有类（本机为 `MICommonInterface`）下发参数 |

> **对照**：Linux 反而提供了**统一呈现层** `/sys/class/power_supply/BAT0/charge_control_end_threshold`，
> 但底层实现仍是每家一套 —— 例如 Clevo 用 ACPI 方法 `0x76`(写) / `0x77`(读)，
> 读回 32 位值的位布局为 `[23:16]=end  [15:8]=start  [7:1]=unused  [0]=on/off`。
> 可见"充电阈值"的典型编码形态是**一个打包字段（阈值 + 开关位）**。

**这解释了 mitool 为什么成立**：通道（`WmiAcpi`）是微软内置的、类定义来自**固件 MOF**，
而功能本体在**固件与 EC** —— 因此卸载厂商软件后，第三方程序照样能调用。

### ⭐ 协议已解出（2026-09-25 19:10，反汇编确认）

**充电用 function = `0x10`（16），与性能模式（fn 8）共用同一隧道**：

| 操作 | 载荷（32 字节） |
|---|---|
| **读充电** | `00 FA 00 10 03 00 00 …` |
| **写充电** | `00 FB 00 10 02 00 <值> 00 …` |

**注意与性能模式的字段差异**（同一隧道内不同功能可以不同布局）：

| | 性能模式 (fn 8) | 充电 (fn 0x10) |
|---|---|---|
| `payload[0]` | 就是值 | **子命令**（读=03 / 写=02） |
| 值所在偏移 | `InData[4]` | **`InData[6]`** |

**来源（非推测）**：
- `MiDeviceServiceEntry.dll` 中的立即数 `0x1000FA00`（VA `0x180c5b35c`）与 `0x1000FB00`（VA `0x180c5fac9`）
- 该写函数位于「读 `ChargingProtect`，`cmp == 1` 才继续」的分支内 —— 上游逻辑与本机注册表配置完全对应
- 通过精确扫描指令编码（`C7 44 24 dd imm32` / `C7 45 dd imm32`）得到设备服务能发出的**完整命令集，恰好 6 条**：
  `FA/08` `FB/08`（性能模式）· `FA/0A` `FB/0A`（与字符串 `"mute"` 相关的功能，非充电）· `FA/10` `FB/10`（充电）

**写入值的语义**：`InData[6]` 直接取自注册表 `…\PerformanceMode\POWER\ChargingThreshold`（本机当前 = 1）；
上游以 `ChargingProtect == 1` 为开关（=0 时该函数直接 `return`）。
与厂商日志 `Write input_buffer_[4]:2`（用户开启充电保护那一刻）**完全吻合**。

### ⚠️ 实测定性：本机固件的充电是「只写」

| 请求 | 响应 | 判读 |
|---|---|---|
| `00 FA 00 08 00 …`（读档位） | `00 80 00 08 **09** 00 …` | `[4]` 由 `00`→`09` ⇒ **有真实数据** |
| `00 FA 00 10 03 …`（读充电） | `00 80 00 10 **03** 00 …` | `[4]` 与请求相同 ⇒ **仅回显，无数据** |

判读规则：响应 = 请求、仅 `[1]` 由 `FA` 变 `80`；**`[4]` 是否改变 = 有无真实数据**。

旁证：管家在 **17:06:47**（当时充电保护 = 0）也记录过一模一样的 `HandleIfokResult:008000100300…`
⇒ `[4]=03` 与充电保护状态无关，是固定应答。
⇒ **本机固件只实现充电「写」，未实现「读」**。厂商软件同样不读固件，而是以注册表值为准、只做「写」下发。

**厂商 UI 侧**：文案只有「恢复/开启充电保护」（内部动作 `ResetChargingProtect` / `set_charging_protect`），
全库未找到百分比档位文案 ⇒ 疑为**开/关型**，`ChargingThreshold` 可能是预设索引而非百分比，**语义未证实**（如实标注）。

### ⭐⭐ 充电档位码：`{0,1,4,5,6,7,8}`，不是百分比（2026-09-25 21:40 最终确认）

**「上限没生效」的根因**：早期把固件的「档位码」当成「百分比」下发（80 / 100），被固件按非法值**静默丢弃**。

**三方交叉验证**：

1. **SvrCModule.dll** 里 `SetChargingProtect2` 的值校验（反汇编）：

   ```asm
   cmp ebx, 1
   jbe  合法                 ; 值 ≤ 1
   lea eax, [rbx - 4]
   cmp eax, 4
   jbe  合法                 ; 4 ≤ 值 ≤ 8
   → 其它 → "SetChargingProtect2 mode: ... invalid"（丢弃）
   ```

   ⇒ **合法集 = `{0,1,4,5,6,7,8}`**

2. **前端 `dist/static/js/main.js`**（管家 UI 是 WebView2 应用）：

   ```js
   gp = { One:1, Four:4, Five:5, Six:6, Seven:7, Eight:8 }
   wp = new Map([
     [gp.Eight, {label:"40%",          ticks:"40"}],
     [gp.Seven, {label:"50%",          ticks:"50"}],
     [gp.Six,   {label:"60%",          ticks:"60"}],
     [gp.Five,  {label:"70%",          ticks:"70"}],
     [gp.One,   {label:"80%（推荐）",   ticks:"80"}],
   ])
   ```

3. 厂商日志 `RECORD ChargingThreshold: 1` —— 用户开启保护时实际下发的正是 **1**。

**档位码 ↔ 档位（定稿）**：

| 档位码 | 档位 | 状态 |
|---|---|---|
| **0** | **关闭** | ✅ mitool `charge off` 使用 |
| **1** | **80%（推荐）** | ✅ mitool `charge on` 使用 |
| 4 | ?（UI 未提供，语义未证实） | 不暴露 |
| 5 | 70% | 不暴露 |
| 6 | 60% | 不暴露 |
| 7 | 50% | 不暴露 |
| 8 | 40% | 不暴露 |

> 链路完全闭合：`UI set_charging_protect{mode:N}` → SvrCModule 校验（上表）→ `[node+0x10]=6`
> （= `SET_CHARGING_PROTECT` 命令号）→ IPC → `MiDeviceService` → `00 FB 00 10 02 00 <N> 00…` → 固件。

**mitool 的命令（按需求固定两态）**：

| 命令 | 下发的载荷 | 说明 |
|---|---|---|
| `charge on` | `00 FB 00 10 02 00 01 00…` | 开启充电保护（充至 80% 停止）；码 1 = 80% |
| `charge off` | `00 FB 00 10 02 00 00 00…` | 关闭充电保护；码 0 = 关闭 |

> `0` 就是厂商语义里的「关闭」且属于合法集 —— 是表达"关"的正确值。
> （早期用 `100` 表达关闭同样是非法值，一并修正。）

**早期那两条结论一并修正**：
- 「厂商的开关是一个值、0 为关」—— **只对一半**：值是**档位码**（合法集 `{0,1,4,5,6,7,8}`），不是任意整数；`0 = 关`、`1 = 80%`。
- 「`charge off` 用 100 更安全」—— **错误**：100 不在合法集内，与 80 一样会被丢弃。

### 当前状态

| 项 | 状态 |
|---|---|
| 本机是否支持 | ✅ 支持（厂商日志 `IfSupportChargingThreshold: 1`） |
| 配置存放位置 | `HKCU`/`HKLM\SOFTWARE\MI\SvrCModule\PerformanceMode\POWER` → `ChargingProtect`(0/1)、`ChargingThreshold`（**厂商私有存储，mitool 不依赖**） |
| 是否经 WMI | ✅ 是（厂商日志 `ModeControlInfo_OperateType_IFOK return from WMI`） |
| **功能号 / 载荷** | ✅ **已解出**：fn `0x10`，读 `00 FA 00 10 03 00…` / 写 `00 FB 00 10 02 00 <档位码> 00…` |
| **档位码映射** | ✅ **已解出**：`0=关 · 1=80% · 4=? · 5=70% · 6=60% · 7=50% · 8=40%` |
| **读能力** | ❌ **本机固件未实现**（仅回显） |
| 是否走内核驱动 | ❌ 否（`MiDeviceServiceEntry.dll` 无 `DeviceIoControl` / `CreateFile` 导入，仅 COM） |
| 与厂商软件共存 | ⚠️ 厂商会在其触发点（启动/唤醒/插拔电）按注册表重新下发 → 测充电前先关掉它的「充电保护」 |

**已实现命令**：`mitool charge get`（只读，如实报告「仅回显」+ Windows 侧供电/电量）、
`mitool charge on`（开启充电保护，固定 80%）、`mitool charge off`（关闭）。

**验证写入是否生效的可靠方式**（慢但确定，无需读通道）——详见 [`testing.md`](testing.md)：
1. 在厂商软件里**关闭**充电保护（此时厂商不再下发，不会覆盖我们的写入）
2. `mitool charge on`
3. 放电到 80% 以下 → 插电 → 观察是否停在 80%；或在电量已超 80% 时观察是否停止充电

### 实现护栏

- `charge on` 固定档位码 `1`（80%，厂商"推荐"档）；`charge off` 固定档位码 `0`（合法集内、且为厂商语义里的"关闭"）；都不接受任意百分比入参
- 不读写厂商注册表：不与厂商软件抢配置，只调固件
- 充电读不可用时不假装能读 —— 如实报告，并给出行为验证方法
- 写入操作不再要求 `--yes`：工具本身只执行用户显式发起的命令，误触责任归调用方（如命令行 alias / 脚本）；`--yes` 的存在反而让单条命令无法独立跑通
- 全局 `--log` 开关：把每次命令、InData/OutData、ReturnCode 追加到 `mitool.exe` 旁的 `mitool.log`，便于事后取证


### 明确不做

旁路供电、直接修改充电电流 —— 没有可靠接口，硬做风险高。

---

## 7. 兼容性与安全

### 能力探测优先，绝不假设

启动即执行 `probe`：两个 WMI 类是否存在、`ACPI\PNP0C14` 实例、`IfSupportChargingThreshold` 能力位。
探测结果决定可用功能集；做不到的开关在输出里直接不出现。

### 机型 Profile 解耦

```
profiles.json:  机型 / BIOS 版本  →  { 通道类型, 档位编码表, 充电方法名, 已知坑 }
```

新机型只加数据、不改代码。`mitool dump` 输出的证据包可直接转成 profile 草稿。
**未知机型默认只做只读探测，不盲写固件。**

### 提权模型

因为连**读取**都需要管理员（实测拒绝访问），整 exe 带 `requireAdministrator` 清单，每条命令一次 UAC。
低频使用（切档、设阈值都是偶发动作），可接受。

> 明确不采用「最高权限计划任务绕过 UAC」——属后台自动化，与红线 2 冲突。

### 回滚与坏状态恢复

- 每次写入前记录 `state.json`（旧值 + 时间戳）
- `mitool reset` 一键回到默认（档位 → balanced；阈值 → 关闭）
- `--dry-run` 先看清楚要做什么
- 连续两次写入失败 → 自动锁定该功能并提示，避免与固件状态打架

### 与现管家共存

- 检测到 `MAFSvr` / `MiDeviceService` 在运行 → 提示「两者会互相覆盖档位」
- 注意：**管家会在每次睡眠唤醒后回读并重灌**蓝牙省电/互联省电/充电保护，可能覆盖 mitool 的设置
- 不主动删除或禁用管家（不越界）；卸载 mitool 即删目录，无残留

### 合规边界

只调用厂商已对外暴露的 WMI 接口。不注入、不 hook、不 patch 固件；不做需求之外的事（例如改 BIOS 启动项，虽有接口也不纳入）。

---

## 8. 实施路线

| 阶段 | 交付 | 状态 |
|---|---|---|
| **P0 协议** | 定位实现组件 + 解出 InData 逐字节格式 + 档位码表 | ✅ **已完成**（2026-09-25）：权威依据为 Linux 主线 `bitland-mifs-wmi` 文档；实测 `raw 00 FA 00 08` → `ReturnCode = 0` |
| **P1 读能力** | `mode get` 真读 + `fn get` 系列 | ✅ **已完成**：`mode get` 解析 `OutData[4]`；`fn get` 可读风扇 / 温度 / 功耗 |
| **P2 写能力** | `mode set`（含写后回读确认） | ✅ **已完成并实机验证通过** |
| **P3 充电协议** | 充电档位码表 + `charge on/off` | ✅ **已完成**（v1.2）：fn `0x10` 解出 + 档位码集 `{0=关,1=80%,4=?,5=70%,6=60%,7=50%,8=40%}` 三方交叉验证；按需求固定为两态，不再支持自定义阈值 |
| **P3 取消 `--yes`** | 写入命令去强制确认 | ✅ **已完成**（v1.3）：CLI 命令本身代表用户意图，移除防误触设计带来的命令链断点 |
| **P3 降到 net8.0-windows** | 提升通用性 · 兼容 Win 11 22H2+ 默认 runtime | ✅ **已完成**（v1.4）：二进制 149 KiB，要求 .NET 8 runtime（用户机器已装 8.0.7，**实测可跑**） |
| **P3 性能档位短别名** | 简化日常切档操作 | ✅ **已完成**（v1.5）：`mode set` 子级 + 顶级 `b`/`p` 别名 |
| **P3 简化命令输出** | 删去调试与原理细节，只给结果 | ✅ **已完成**（v1.6）：7 个命令合计从 ~40 行输出缩到 ~15 行；日志仍记录完整数据 |
| P3 收尾 | 机型 profile 解耦（`profiles.json`） | ⏳ 未做（不阻断主流程；当前档位码硬编码在 `Program.cs`，新机型需要改代码） |

---

## 9. 未解项（诚实标注）

| 项 | 状态 |
|---|---|
| 充电档位码 `{4,5,6,7,8}` 对应的实际百分比 | ⚠️ **语义部分证实**（fn `0x10` 已解）。前端 `main.js` 的 `wp` Map 给出 8=40% / 7=50% / 6=60% / 5=70% / 1=80% 共 5 个；与厂商校验集 `{0,1,4,5,6,7,8}` 对照，**差码 4 无 UI 文档**，可能为厂商预留或历史遗留 —— 不影响 mitool（按需求固定用 1=80%） |
| `FN+K` 热键在卸载管家后是否仍可用 | 未实测 |
| 本机是否为虚拟化环境 | 部分 ACPI 行为可能不代表物理机 |
| 固件内 AML 究竟写 EC 还是走 SMU 邮箱 | 黑盒（本机 `HKLM\HARDWARE\ACPI` 与 `GetSystemFirmwareTable` 均被拦截）。**现已不必深究**：WMI 层已可直接读写 |

---

## 附录 A：本机实测证据

| 证据 | 来源 |
|---|---|
| 厂商 WMI 类与签名 | `Get-CimClass -Namespace root\wmi` |
| 非管理员被拒绝访问 | `Get-CimInstance -Namespace root\wmi -ClassName MICommonInterface` → 拒绝访问 |
| ACPI 设备实例 | `HKLM\SYSTEM\CurrentControlSet\Enum\ACPI\PNP0C14\{0,AOD,MIFS}` |
| 固件方法名表 | `SvrCModule.dll` 串池（ASCII 连串） |
| 对象路径字面量 | `MiDeviceServiceEntry.dll` Unicode 串池：`MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'` |
| **InData / OutData 逐字节内容** | **`C:\ProgramData\MI\MiDeviceService\log\MiDeviceService_log.txt`**，`operator_manager.cpp:243 HandleRegResult:<64hex>` / `:181,429,461 Handle*Result:<60hex>` |
| **寄存器层档位码** | 上述日志的 `Write input_buffer_[4]:<v>` 与管家日志 `updated _currentPerformanceMode to:` 按时间戳逐条对齐 |
| UI 层档位编码 | 管家日志 `updated _currentPerformanceMode to: 智能模式(10)` |
| 唤醒后回灌行为 | 2026-09-25 08:40:19 Suspend → 08:42:35 Resume → `get_workLoad_mode` 回读 mode=10 |

## 附录 B：本机事实速查

- 机型：Redmi Book 14 2025 (AMD) / Ryzen 7 7735H；BIOS RMARB4B1P0606
- 固件当前档位：**智能模式 (10)** = 均衡档（2026-09-25 08:42 回读）
- Windows 电源模式：平衡（Overlay 全 0），**从未调整**
- 睡眠能力：仅 S0 现代待机 + 休眠（无 S3）—— **本工具不涉及**
- 电池：设计 56,000 mWh / 满充 56,587 mWh（健康 ~101%）

---

## 附：演进轨迹（三次收紧）

| 版本 | 变化 |
|---|---|
| v0.1 初版 | CLI + 托盘 + 提权 Broker + 三层 Provider（含 Windows 标准降级）→ 0 驱动 / 0 服务 / < 2 MB |
| v0.2 红线 1+2 | 删「Windows 标准降级通道」；取消计划任务；不联动睡眠 |
| **v0.3 红线 3** | **删托盘 + 删提权 Broker → 单 exe，纯 CLI，0 常驻** |
| **v0.4 档位精简** | **档位从四档收敛为两档：均衡 / 高性能** |
| **v0.5 更名** | **工具名定为 mitool** |
| **v0.6 P0 拆分** | **P0 拆为 P0a 静态解格式（只读零风险）+ P0b 幂等载荷实测** |
| **v0.7 协议解出** | **找到真正的实现组件（`MiDeviceServiceEntry.dll`，在 `Timi Personal Computing` 下）；从设备服务日志白拿报文，解出 `01 16 <档位码> 00×29` 与寄存器层档位码表；纠正「载荷内容不参与判定」等三条误判** |
| **v0.8 权威协议** | **改走 Linux 主线驱动 `bitland-mifs-wmi` 的 MIFS 协议，`raw 00 FA 00 08` 读回档位成功 → 读通道打通；纠正 `01 16 xx` 格式（那是 EC 寄存器层中间表示，非 WMI 载荷）** |
| **v0.9 充电解出 + 代码清理** | **充电协议解出（fn `0x10`，读 `00 FA 00 10 03 00…` / 写 `00 FB 00 10 02 00 <值> 00…`，值在 `InData[6]`）；删除 `fn` / `raw` 及全部逆向脚手架，代码 1033 行 → 486 行；新增 `charge get|set`** |
| **v1.0 实测定性** | **实读确认本机固件的充电为「只写」：读命令仅回显（`[4]` 与请求相同）；据此把 `charge get` 改为如实报告、`charge set` 改为写原始值；新增「响应 [4] 是否改变 = 有无数据」的判读规则；设备服务完整命令集确认为 6 条** |
| **v1.1 更名 + 开关简化** | **命令名统一小写（`mitool`，工程目录/程序集/manifest 同步改名）；从厂商反汇编确认「开关与阈值是同一个值」，据此把充电命令简化为 `charge on [阈值]` / `charge off`，**不再依赖厂商注册表**；关闭限充取 100 而非 0（安全取值）；新增 `mitool-测试说明.md`（已并入 `docs/testing.md`，其中「100 更安全」已被 v1.2 证伪）** |
| **v1.2 档位码纠错 + 测试说明修正** | **「上限没生效」根因定案：充电参数是档位码（合法集 `{0,1,4,5,6,7,8}`）不是百分比，早期下发的 80/100 被固件静默丢弃；从 SvrCModule 校验 + 前端 `main.js` 的 `gp`/`wp` 映射定稿 `0=关 · 1=80% · 5=70% · 6=60% · 7=50% · 8=40%`；按需求删除自定义阈值，固定 `charge on`（80%）/`charge off`；新增 `--log` 日志开关；同步修正 `docs/testing.md`** |
| **v1.3 取消 `--yes` 确认** | **删除 `YesFlag` 与 `charge on/off` 的 `--yes` 强制确认：CLI 命令本身就是用户意图的明确表达，多敲 `--yes` 让单条命令无法独立跑通（写脚本 / alias 时尤甚）；帮助文本、设计方案与测试说明同步清理；变更后 `charge on/off` 单一命令即可生效** |
| **v1.4 降 TargetFramework 到 net8.0-windows** | **`mitool.csproj` 中 `net10.0-windows` → `net8.0-windows`、`System.Management` 9.0.0 → 8.0.0；修复两处 net9 特有语法（target-typed `new` → `new Dictionary<TKey, TValue>(...)`）；编译产物从 159 KiB 缩到 **149 KiB**，runtime 要求从 .NET 10（用户机器没有）变成 **.NET 8（Win 11 22H2+ 默认装，开箱即用）**；已实测编译 0 警告 0 错误** |
| **v1.5 性能档位短别名** | **`Modes` 拆为 `ModeAliases`（含 `b`/`bal`/`balanced` 与 `p`/`perf`/`performance`）+ `ModeCodes`（规范名→码），在 `mode` 子命令作用域生效；提取 `SetMode(alias)` 辅助函数统一处理；错误信息：`未知档位「{}」，可用：balanced / performance（短别名：b / bal · p / perf）`** |
| **v1.6 简化命令输出** | **删除所有协议细节、hex 载荷、function 号、原理说明：① `probe` 从 9 行缩到 2 行（通道/档位）；② `status` 从 8 行缩到 4 行（档位/供电/电量/充电中）；③ `mode get` 单行输出档位短名（无 0x 码）；④ `mode set` 从 4 行（切档/下发/回读）合并为单行"已切换到：xxx"；⑤ `charge get` 去掉 hex/判读说明，只保留 Windows 供电/电量；⑥ `charge on/off` 从 4 行合并为单行"已开启/已关闭"；⑦ `reset` 单行"已恢复到：均衡"。新增 `ModeShortName()` 给出"静谧/均衡/高性能/省电"四档用户面向短名；删除未使用的 `ModeName()` 方法。输出策略：用户只看结果，协议细节由 `--log` 写入 `mitool.log` 留痕** |

**三条方法论教训**（都付了学费）：
1. **先搜开源实现**（Linux 主线驱动 / 内核文档），再考虑逆向 —— 答案常常公开摆着
2. **别只看 `C:\Program Files\<厂商>`** —— 服务 `ImagePath` 指向的目录才是真相（本例在 `Timi Personal Computing` 下）
3. **找功能号别"扫号试错"，要扫"拼装缓冲区的立即数"** —— 同一个隧道内不同功能的 `payload` 布局可以不同（性能模式值在 `[4]`、充电值在 `[6]`），扫号法会给出假阴性

> 转折点：前几轮一直在 `C:\Program Files\MI` 里找，**方向错了**。真正的实现属于另一个产品主体（Timi Personal Computing），而且它**把收发缓冲区直接写进了日志**——静态分析十几轮没拿到的答案，日志里白纸黑字。

每一条约束都在把工具推向更薄，方向自洽。
