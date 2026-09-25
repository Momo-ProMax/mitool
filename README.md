# mitool

小米 / 红米笔记本的轻量化固件控制工具 —— 性能档位切换 + 充电保护，纯 CLI、零常驻。

替代小米电脑管家在 Windows 上对应的两个功能：

- 性能档位：`均衡` / `高性能`（还可通过 `FN+K` 切换，但软件常驻）
- 充电保护：开（限充至 80%） / 关

走厂商固件暴露的 `MICommonInterface` WMI 接口，**不写驱动、不装服务、不写注册表、不监听系统事件**。

---

## 安装

从 [Releases](https://github.com/your-org/mitool/releases) 下载 `mitool.exe`，或自行构建：

```powershell
cd src\mitool
dotnet build -c Release
```

产物：`src\mitool\bin\Release\net8.0-windows\mitool.exe`（~149 KiB，需 .NET 8 runtime，Win 11 22H2+ 默认自带）

需要分发到无 runtime 的机器时：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

产物在 `bin\Release\net8.0-windows\win-x64\publish\mitool.exe`（~70 MB，双击即跑）。

---

## 使用

**必须用管理员终端**（Win + X → 终端(管理员)）。

```powershell
mitool probe                  # 探测通道（首次运行必跑）
mitool status                 # 汇总当前状态（档位 + 充电 + 供电 + 电量）
mitool mode get               # 读当前档位
mitool mode set balanced      # 切到均衡
mitool mode set performance   # 切到高性能

mitool mode b                 # ≡ mode set balanced（短别名）
mitool mode p                 # ≡ mode set performance
mitool mode bal               # 同 bal/b
mitool mode perf              # 同 perf/p

mitool charge get             # 读充电状态（固件读不可用，仅返回 Windows 侧信息）
mitool charge on              # 开启充电保护（限充至 80%）
mitool charge off             # 关闭充电保护

mitool reset                  # 档位恢复均衡

mitool --log <cmd>            # 把命令与固件往返记入 mitool.exe 旁的 mitool.log
```

### 输出示例

```
> mitool status
档位:均衡
供电:插电
电量:100%
Windows 视角充电中:否

> mitool mode set performance
已切换到:高性能

> mitool charge on
已开启充电保护(80%)
```

### 返回码

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 命令或子命令参数错误 / 读取失败 |
| 3 | 未分类异常 |
| 5 | 权限不足（非管理员） |

---

## 红线

不可妥协的四条设计原则：

1. **不碰 Windows 电源模式** —— 永不写电源计划 / Overlay / EPP；对电源侧只读
2. **不联动睡眠** —— 不监听睡眠 / 唤醒事件，不阻止系统睡眠，不设唤醒定时器
3. **只走固件通道** —— 只调用厂商 WMI；不注入、不 hook、不写驱动
4. **零常驻** —— 不写计划任务、不开后台进程、不托盘驻留

机型不支持 → 直接报"本机不支持"，**不允许退化为改 Windows 电源设置**。

---

## 与厂商软件的关系

小米电脑管家会**按自己的注册表值重新下发**配置，可能覆盖 mitool 的设置。需要纯 mitool 管控时，请卸载或禁用管家的开机自启。

管家卸载后，本工具**仍可正常工作** —— WMI 类由微软内置的 `WmiAcpi` 驱动承载，类定义来自固件 MOF，与厂商软件无关。

---

## 目录结构

```
mitool/
├── README.md
├── LICENSE                       MIT
├── .gitignore
├── src/mitool/
│   ├── mitool.csproj             .NET 8 项目
│   ├── app.manifest              requireAdministrator
│   └── Program.cs                全部实现，单文件
├── docs/
│   ├── design.md                 设计方案 + 协议细节 + 决策记录
│   └── testing.md                测试步骤 + 验收记录表
└── research/                     RE 调查归档（git 已忽略）
```

---

## 协议来源

非逆向猜测，**权威依据**：

- Linux 主线驱动：`drivers/platform/x86/bitland-mifs-wmi.c`
- 内核文档：<https://docs.kernel.org/wmi/devices/bitland-mifs-wmi.html>

二者 MOF 定义与本机 `Get-CimClass` 读出完全一致（已交叉验证）。扩展功能（如读风扇/温度）请**优先查内核文档**。

---

## 已知限制

- **仅适用小米 / 红米笔记本**（依赖 MIFS WMI 接口）
- **充电读不可用**：本机固件对充电读命令仅回显，无法回读确认
- **充电档位固定 80%**：固件实际支持 0/40/50/60/70/80 等多档，本工具按需固定两态
- **需管理员权限**：厂商 WMI 类的 ACL 对非管理员直接拒绝

完整限制与设计决策见 [`docs/design.md`](docs/design.md)。

---

## 贡献

- 一个 PR 一件事；不要"重构 + 新功能 + 修 bug"混在一起
- 保持单 exe 形态（拒绝引入第二个运行时）
- 提交前 `dotnet build -c Release` 必须 0 警告 0 错误
- 新机型支持：把硬编码移到 `profiles.json`，按机型/BIOS 版本匹配（详见 `docs/design.md` §「机型 Profile 解耦」）

提交 Issue 附：

- `mitool probe` 输出
- `mitool status --log` 输出（去敏感信息）
- 机型 / BIOS 版本 / Windows 版本

---

## 许可证

[MIT](LICENSE) © 2026 mitool contributors
