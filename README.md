```markdown
# Windows Safe Drive Eject & HDD Spin-Down Tool

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg)](https://microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Dependencies](https://img.shields.io/badge/Dependencies-Zero%20(Native)-brightgreen.svg)](#)

[English](#english) | [中文说明](#中文说明)

---

## English

A zero-dependency Windows utility that safely ejects external storage devices (USB enclosures, portable HDDs/SSDs, flash drives) and triggers a **true hardware spin-down (SCSI Stop Unit)** on mechanical hard drives before power disconnection.

### Why This Tool?

* **True Motor Spin-Down**: Calls native Windows Configuration Manager APIs (`cfgmgr32.dll` / `CM_Request_Device_EjectW`) to park heads and spin down the spindle motor safely.
* **No Persistent Offline State**: Unlike `diskpart offline`, it leaves no lingering state. The drive mounts automatically next time it is connected.
* **Interactive & CLI Modes**: Supports interactive input, double-click execution, and command-line parameters.
* **Handle Release**: Automatically closes File Explorer windows pointing to the target volume to release file locks.
* **System Protection**: Built-in guard logic prevents accidental eject operations on system or boot drives.
* **Zero Dependencies**: Pure Batch + PowerShell with in-memory C# P/Invoke. No external `.exe` or third-party binaries required.
* **Adaptive Bilingual UI**: Automatically switches display language between Chinese and English based on the OS UI language.

---

### How to Use

#### Method 1: Interactive Mode (Double-Click)
1. Double-click `EjectDrive.bat`.
2. Enter the target drive letter (e.g., `E` or `F`) and press `Enter`.
   * *If no letter is entered, it defaults to drive `E:`.*
3. When the green success banner appears, turn off the enclosure power button or unplug the USB cable.

#### Method 2: Command Line Mode (CLI / Script Automation)
Pass the drive letter directly as an argument:
```cmd
:: Eject drive E:
EjectDrive.bat E

:: Eject drive F: (colon is optional)
EjectDrive.bat F:

```

---

### Technical Principle

1. **Volume to PnP Resolution**: Resolves the logical drive letter to its partition and physical disk index (`Win32_DiskDrive`), extracting its `PNPDeviceID`.
2. **Device Node Tree Traversal**: Uses `CM_Locate_DevNode` and `CM_Get_Parent` to traverse from the disk device node up to the parent USB mass storage / composite device node.
3. **PnP Ejection Request**: Issues `CM_Request_Device_EjectW`. Windows flushes all pending file system caches, issues SCSI `START STOP UNIT` commands to park the drive heads and spin down the motor, and powers down the port logically.

---

### Troubleshooting & FAQ

* **"Drive is currently locked by a process / 弹出失败：设备正被占用"**:
* Ensure no media players, IDEs, code editors, or transfer tools (e.g., FastCopy) are accessing the drive.
* If large files were just written, wait 5–10 seconds for antivirus background scanning to complete, then retry.


* **Encoding Requirements**:
* If editing the script manually in Notepad, save the file with **`ANSI`** encoding to prevent Windows Command Prompt (`cmd.exe`) character misinterpretation.



---

### Return Codes

| Exit Code | Description |
| --- | --- |
| `0` | **Success**: Drive safely ejected, motor spun down. |
| `1` | **Not Found**: Target drive letter does not exist or is already disconnected. |
| `2` | **Aborted**: Target drive is a system/boot volume. Operation blocked. |
| `3` | **Busy**: Drive is locked by an active process or open file handles. |

---

## 中文说明

一个免安装、零依赖的 Windows 原生批处理工具。用于安全弹出各类外置存储（移动硬盘盒、外置机械/固态硬盘、U盘），并在切断连接前向机械硬盘发送底层马达停转（SCSI Spin-Down）与磁头归位指令。

### 核心特性

* **物理停转与磁头归位**：调用 Windows 原生配置管理器接口（`cfgmgr32.dll` 中的 `CM_Request_Device_EjectW`），强制马达平稳停转并复位磁头，彻底避免直接断电引发的物理划伤。
* **无残留脱机锁定**：区别于 `diskpart offline` 会永久锁定磁盘，本工具采用即插即用卸载逻辑，下次重新插入时系统自动识别并分配盘符。
* **交互式 / 命令行双模式**：支持直接双击输入盘符，也支持在终端中传参调用。
* **自动释放资源管理器占用**：弹出前自动关闭浏览目标盘符的文件资源管理器窗口，降低占用失败率。
* **系统盘安全防护**：内置安全校验，自动拦截针对 C 盘及系统引导盘的操作。
* **零外部依赖**：纯 Batch + PowerShell 内存动态调用 C# P/Invoke，无需下载任何第三方 `.exe`。
* **自适应双语界面**：根据 Windows 系统的语言环境自动显示中文或英文提示。

---

### 使用方法

#### 方式一：交互模式（直接双击）

1. 双击运行 `EjectDrive.bat`。
2. 在提示行输入需要弹出的盘符（如 `E` 或 `F`）后按回车。
* *直接按回车将默认选择 `E` 盘。*


3. 看到绿色成功提示且硬盘停止震动后，即可按下硬盘盒电源按键或拔出 USB 数据线。

#### 方式二：命令行模式（终端传参 / 脚本调用）

在终端或 CMD 中传入盘符参数：

```cmd
:: 弹出 E 盘
EjectDrive.bat E

:: 弹出 F 盘（带不带冒号均可）
EjectDrive.bat F:

```

---

### 技术原理

1. **逻辑盘符映射物理节点**：通过 WMI/CIM 将用户输入的盘符解析至物理磁盘索引（`Win32_DiskDrive`），提取对应的 `PNPDeviceID`。
2. **PnP 设备树层级遍历**：利用 `CM_Locate_DevNode` 与 `CM_Get_Parent` 向上递归定位 USB 根设备与主控节点。
3. **发送弹出与停机指令**：调用 `CM_Request_Device_EjectW` 触发原生弹出机制。Windows 会强制刷入 RAM 写入缓存，向机械硬盘发送 SCSI `START STOP UNIT` 停转信号，并安全切断 USB 数据通道。

---

### 常见问题与注意事项

* **提示“弹出失败：设备正被占用”**：
* 请确认已关闭所有打开该盘文件的程序（如看图软件、视频播放器、FastCopy 等）。
* 若刚刚完成海量小文件写入，Windows Defender 等杀毒软件可能正在后台进行实时扫描，请等待 5~10 秒后重新运行脚本。


* **文本编码说明**：
* 如使用记事本手动编辑该脚本，请务必在“另存为”时将编码选择为 **`ANSI`**，避免 Windows CMD 解析中文字符时产生语法错位。



---

### 退出代码（Exit Codes）

| 代码 | 说明 |
| --- | --- |
| `0` | **操作成功**：设备已安全弹出，机械硬盘马达已完全停转。 |
| `1` | **未找到设备**：目标盘符不存在或已被拔出。 |
| `2` | **安全拦截**：目标盘符为系统启动盘/引导盘，操作已强制终止。 |
| `3` | **设备繁忙**：目标盘符正被后台进程或未关闭的程序占用。 |

---

## License

This project is licensed under the [MIT License](https://www.google.com/search?q=LICENSE).

```

```
