# win-safe-eject

Windows 原生环境下的外接 USB 存储设备底层安全卸载工具。基于 Win32 存储 I/O 控制指令与即插即用（PnP）配置管理器 API 构建，实现外接机械硬盘与多分区设备的一键高速缓存落盘、全卷安全卸载与磁头平稳停转，且保证重新接入时 100% 自动挂载。

A native Windows low-level safe ejection tool for external USB storage devices. Built upon Win32 storage I/O controls and Plug and Play (PnP) Configuration Manager APIs, it guarantees full cache flushing, multi-volume safe dismounting, and mechanical spindle spin-down, ensuring 100% automated remounting upon reconnection.

---

## 目录 / Table of Contents

- 中文文档
  - 核心痛点与技术边界分析
  - 方案演进历史
  - 内核执行流程
  - 构建与使用
  - 故障排查
- English Documentation
  - Technical Boundaries & Pitfalls
  - Version History
  - Architecture & Execution Flow
  - Build & Usage
  - Troubleshooting
- 开源协议 / License

---

## 中文文档

### 核心痛点与技术边界分析（为什么淘汰其他方案）

在 Windows 存储子系统与驱动通信层面上，常见方案存在以下根本缺陷：

#### 1. 为什么不能使用纯 BAT 方案？
- **无内核 I/O 交互通道**：CMD 原生环境缺乏对 Win32 设备句柄的访问能力。内置的 `mountvol /p` 仅移除驱动器卷挂载点，无法向底层总线驱动下发停机请求，机械硬盘磁头无法归位停转，直接断电会造成磁头非受控着陆。
- **持久化脱机副作用**：在 BAT 中调用 `diskpart offline disk` 会在磁盘签名与注册表中永久写入脱机状态标志。该标志具备持久化属性，拔出再次插入后磁盘仍处于“脱机”状态，不会自动分配盘符，必须手动进入磁盘管理恢复。

#### 2. 为什么不能使用 BAT + PowerShell / C# 混编方案？
- **字符编码与变量转义损坏**：在 CMD 中动态 echo 拼凑 PS1、或调用 Base64 编码命令时，极易因控制台代码页（CP936 与 UTF-8）冲突出现乱码。驱动器盘符内插（如 `$letter:`）极易触发 Win32Error 161（`ERROR_BAD_PATHNAME`），或因换行符丢失导致语法粘连。
- **WMI 扩展分区断裂缺陷**：PowerShell 常用的 `Win32_LogicalDiskToPartition` 接口在面对“USB 桥接芯片 + MBR 扩展分区中的逻辑驱动器（如 E 为主分区，F/G/H 为逻辑驱动器）”时，因驱动程序抽象层缺失，查询直接返回空结果，导致脚本无法定位物理磁盘。
- **主板 USB 根集线器连带击穿（端口装死）**：传统脚本通过 `CM_Get_Parent` 递归向上寻找带有 `DN_REMOVABLE` (0x00004000) 属性的节点并执行弹出。然而主板上的 USB 根集线器端口同样包含可移除属性，导致递归逻辑直接注销了主板物理端口（PnP Problem 47 挂起状态），拔插任何设备均无响应，必须重启系统或重新扫描硬件。

#### 3. 为什么不能依赖 RemoveDrivex64 等第三方闭源工具？
- **内核通信安全与合规风险**：存储卸载需以 Administrator 权限执行。闭源二进制工具无法审查内存操作与执行逻辑，在受控或对安全性要求较高的环境中存在供应链安全隐患。
- **多分区并发处理竞争**：当单块物理磁盘被划分为多个分区时，若直接移除单盘符，其余分区的延迟写入或索引服务未完全关闭，PnP 管理器会发出 Veto（一票否决），造成卸载失败。
- **老旧桥接芯片协议兼容性缺失**：部分 2005\~2010 年的老式 IDE/SATA 转 USB 易驱线芯片（如 JMicron、Initio、Prolific 等）对标准 SCSI 停止单元指令响应不规范，通用工具处理不当容易造成物理磁头撞盘异响。

---

### 方案演进历史

#### v1.0：批处理与 PowerShell/C# 混编阶段 (`EjectDrive.bat`，已归档废弃)
- 采用批处理动态生成临时 PowerShell 脚本，利用 WMI 查询分区并调用 `CM_Request_Device_Eject`。
- **状态说明**：代码仓库根目录保留的 `EjectDrive.bat` 仅作为技术演进的历史参考，因脚本转义脆弱性及扩展分区识别缺陷，已停止维护。

#### v2.0：纯原生 Win32 存储子系统引擎 (`SafeEject.cs` / `SafeEject.exe`)
- 放弃上层 WMI 抽象与脚本混编，全面采用原生 C# 5.0 编写，直接由系统自带的 .NET Framework 编译器构建为单体独立二进制文件：
  - **物理层穿透定位**：利用 `IOCTL_STORAGE_GET_DEVICE_NUMBER` 直接穿透文件系统抽象，无论分区表为主分区还是 MBR 扩展逻辑驱动器，毫秒级锁定物理驱动器号。
  - **全卷并发强制落盘**：自动扫描同一物理磁盘上的全部挂载卷，并发下发 `FlushFileBuffers` 确保脏数据完全落盘。
  - **PnP 边界受控拦截**：在设备拓扑树向上回溯时，严格锚定 `USB\VID_` 与 `USBSTOR` 存储桥接器层级并立即截断，绝不触碰主板 USB 根集线器。

---

### 内核执行流程

```text
[输入盘符 (如 E:)]
       │
       ▼
[IOCTL_STORAGE_GET_DEVICE_NUMBER] ──> 获取物理驱动器号 (PhysicalDriveX)
       │
       ▼
[扫描 A 到 Z 驱动器] ──> 锁定属于同一物理磁盘的所有分区 (E:, F:, G:, H:)
       │
       ▼
[遍历卷句柄] ──> 并发调用 FlushFileBuffers (脏缓存完全写入物理磁道)
       │
       ▼
[PnP 设备树检索] ──> CM_Locate_DevNode (定位磁盘驱动器节点)
       │
       ▼
[精准向上回溯] ──> 命中 USB\VID_ 或 USBSTOR 节点后立即截断停止
       │
       ▼
[CM_Request_Device_Eject] ──> 下发安全卸载与停转指令 (保留物理端口响应)
```

---

### 构建与使用

#### 1. 本地一键构建
本工具依赖 Windows 10/11 自带的基础组件，无需安装 Visual Studio 或额外 SDK。

运行根目录下的 `build.bat`，或在命令行中执行：

```cmd
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /t:exe /out:SafeEject.exe /r:System.Management.dll SafeEject.cs
```

#### 2. 使用方法
向驱动栈下发 `IOCTL` 控制码与 `FlushFileBuffers` 需要内核通信权限，**必须以管理员身份运行**：

- **交互模式**：右键点击 `SafeEject.exe` 选择“以管理员身份运行”，根据提示输入任意一个目标盘符（如 `E`）并回车。
- **参数化静默调用**：
  ```cmd
  SafeEject.exe E
  ```
- **创建桌面快捷方式**：为 `SafeEject.exe` 创建桌面快捷方式，右键属性在“目标”末尾添加盘符（例如 `"C:\Tools\SafeEject.exe" E`），在“高级”属性中勾选“用管理员身份运行”。

---

### 故障排查

- **提示“找不到盘符”**：请确认是否已使用管理员身份运行程序，非提权环境下系统会限制对卷设备句柄的底层枚举。
- **提示“安全弹出被系统否决 (Veto)”**：说明有前台进程、杀毒软件、索引服务或终端正占用该磁盘上的文件。请关闭正在使用该盘文件的程序后重试。

---

## English Documentation

### Technical Boundaries & Pitfalls (Why Other Solutions Were Deprecated)

When interacting with the Windows storage subsystem and device drivers, conventional scripting and third-party tools expose fundamental flaws:

#### 1. Why Pure BAT Scripts Are Inadequate
- **Lack of Kernel I/O Control**: The native CMD interpreter cannot access Win32 device handles. `mountvol /p` only dismounts the volume mount point and cannot dispatch bus-level shutdown requests. Mechanical spindles will not park safely, leading to uncontrolled head parking upon power cut.
- **Persistent Offline State**: Executing `diskpart offline disk` persists the offline flag within the registry and disk metadata. When reconnected, the disk remains offline and will not be assigned a drive letter automatically, requiring manual intervention in Disk Management.

#### 2. Why BAT + PowerShell / Dynamic C# Scripts Fail
- **Encoding and Path Corruption**: Dynamically echoing PS1 code or passing Base64 commands from CMD causes code-page conflicts (e.g., CP936 vs UTF-8). Interpolated drive strings (`$letter:`) frequently trigger Win32Error 161 (`ERROR_BAD_PATHNAME`), while stripped newlines lead to syntax collapse.
- **WMI Breakage on Extended Partitions**: The commonly used WMI association `Win32_LogicalDiskToPartition` breaks when dealing with USB bridges paired with MBR extended partitions (e.g., primary partition E: alongside logical drives F:, G:, H:). The query yields empty results, failing to resolve the underlying physical drive.
- **Host USB Root Hub Destruction (Port Lockup)**: Scripts naively traversing up using `CM_Get_Parent` based on `DN_REMOVABLE` (0x00004000) inadvertently target the motherboard Root Hub. Ejecting the Root Hub puts the physical USB port into a suspended state (PnP Problem 47), rendering the port unresponsive to reconnections until the system is restarted.

#### 3. Why RemoveDrivex64 and Closed-Source Tools Are Not Preferred
- **Security and Compliance**: Storage ejectors must run with elevated administrative privileges. Closed-source binaries pose supply chain and security audit challenges.
- **Multi-Partition Concurrency Races**: On disks with multiple partitions, single-volume ejection tools often get vetoed by the OS if companion partitions have pending background writes or active index handles.
- **Legacy Controller Incompatibilities**: Older USB-to-IDE/SATA bridge controllers produced around 2005\~2010 (such as JMicron, Initio, and Prolific) handle standard SCSI STOP UNIT commands non-conventionally, risking abnormal physical head resets when handled by generic tools.

---

### Version History

#### v1.0: Hybrid Batch + PowerShell/C# (`EjectDrive.bat`, Deprecated)
- Utilized dynamic PowerShell generation and WMI queries to execute `CM_Request_Device_Eject`.
- **Status**: The legacy `EjectDrive.bat` in the repository root is preserved for historical and comparative purposes. It is no longer maintained.

#### v2.0: Native Win32 Storage Subsystem Engine (`SafeEject.cs` / `SafeEject.exe`)
- Rebuilt entirely in C# 5.0 without upper-level WMI dependencies, compiling cleanly via the built-in Windows .NET Framework compiler:
  - **Direct Hardware Indexing**: Employs `IOCTL_STORAGE_GET_DEVICE_NUMBER` to bypass file-system abstractions, instantly locating the physical disk index regardless of MBR extended/logical configurations.
  - **Full-Volume Cache Flushing**: Enumerates all volumes mapped to the physical drive and invokes kernel-level `FlushFileBuffers` concurrently.
  - **Bounded PnP Traversal**: Halts device tree traversal strictly at the `USB\VID_` and `USBSTOR` storage bridge layer, safeguarding the motherboard Root Hub.

---

### Architecture & Execution Flow

```text
[Input Drive Letter (e.g., E:)]
       │
       ▼
[IOCTL_STORAGE_GET_DEVICE_NUMBER] ──> Retrieve PhysicalDrive Index
       │
       ▼
[Enumerate Drives A-Z] ──> Identify All Volumes on Same Disk (E:, F:, G:, H:)
       │
       ▼
[Volume Handles] ──> Concurrently invoke FlushFileBuffers (Commit dirty cache)
       │
       ▼
[PnP Device Resolution] ──> CM_Locate_DevNode (Locate Disk Node)
       │
       ▼
[Bounded Upward Traversal] ──> Halt immediately upon matching USB\VID_ or USBSTOR
       │
       ▼
[CM_Request_Device_Eject] ──> Safe bus removal and spin-down (Port intact)
```

---

### Build & Usage

#### 1. Local Build
This utility uses native Windows components. No Visual Studio installation or external SDK is required.

Run `build.bat`, or execute the following in a command prompt:

```cmd
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /t:exe /out:SafeEject.exe /r:System.Management.dll SafeEject.cs
```

#### 2. Usage Instructions
Direct I/O control codes and cache flushing require elevated rights. **Must be run as Administrator**:

- **Interactive Mode**: Right-click `SafeEject.exe` and select "Run as administrator". Enter any drive letter belonging to the target drive (e.g., `E`) and press Enter.
- **CLI Mode**:
  ```cmd
  SafeEject.exe E
  ```
- **Desktop Shortcut**: Create a desktop shortcut for `SafeEject.exe`. Right-click the shortcut, go to Properties, append the target letter to the Target path (e.g., `"C:\Tools\SafeEject.exe" E`), and check "Run as administrator" under Advanced properties.

---

### Troubleshooting

- **Error: "Cannot find drive"**: Ensure the program is executed with administrative privileges. Non-elevated contexts cannot query raw volume handles.
- **Error: "Ejection Vetoed"**: A process, anti-virus scanner, or active terminal is accessing files on one of the partitions. Close conflicting programs and retry.

---

## 开源协议 / License

本项目采用 MIT 许可证授权。详见 `LICENSE` 文件。

This project is licensed under the MIT License. See the `LICENSE` file for details.
