@echo off
title Safe USB Drive Eject and Spin-Down Tool
color 0f

:: 1. 交互式输入目标盘符
set "TARGET=%~1"
if "%TARGET%"=="" (
    echo.
    set /p TARGET="请输入要弹出的盘符 (例如 E，直接按回车默认弹出 E) / Enter drive letter: "
)
if "%TARGET%"=="" set "TARGET=E"
:: 只截取输入的第一个字符（避免输入 E: 导致错误）
set "TARGET=%TARGET:~0,1%"

echo.
echo 正在安全弹出 %TARGET% 盘并发送硬件停转指令，请稍候...

:: 2. 生成并执行临时 PowerShell 脚本
set "ps1=%temp%\safe_eject_%TARGET%.ps1"

> "%ps1%" echo $targetDrive = '%TARGET%'
>>"%ps1%" echo try {
>>"%ps1%" echo     $shell = New-Object -ComObject Shell.Application
>>"%ps1%" echo     $shell.Windows() ^| Where-Object {
>>"%ps1%" echo         try { $_.Document.Folder.Self.Path -like "$targetDrive*" } catch { $false }
>>"%ps1%" echo     } ^| ForEach-Object { $_.Quit() }
>>"%ps1%" echo } catch {}
>>"%ps1%" echo.
>>"%ps1%" echo $code = @"
>>"%ps1%" echo using System;
>>"%ps1%" echo using System.Text;
>>"%ps1%" echo using System.Runtime.InteropServices;
>>"%ps1%" echo public class UsbEject{
>>"%ps1%" echo     [DllImport("cfgmgr32.dll", EntryPoint="CM_Locate_DevNodeW", CharSet=CharSet.Unicode)]
>>"%ps1%" echo     public static extern int CM_Locate_DevNode(out uint pdnDevInst, string pDeviceID, int ulFlags);
>>"%ps1%" echo     [DllImport("cfgmgr32.dll")]
>>"%ps1%" echo     public static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, int ulFlags);
>>"%ps1%" echo     [DllImport("cfgmgr32.dll", EntryPoint="CM_Request_Device_EjectW", CharSet=CharSet.Unicode)]
>>"%ps1%" echo     public static extern int CM_Request_Device_Eject(uint dnDevInst, out int pVetoType, StringBuilder pszVetoName, int ulNameLength, int ulFlags);
>>"%ps1%" echo     [DllImport("cfgmgr32.dll")]
>>"%ps1%" echo     public static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, int ulFlags);
>>"%ps1%" echo     public static int Eject(string pnpId){
>>"%ps1%" echo         uint devInst;
>>"%ps1%" echo         if(CM_Locate_DevNode(out devInst, pnpId, 0)!=0) return 1;
>>"%ps1%" echo         uint curr = devInst;
>>"%ps1%" echo         uint status, prob;
>>"%ps1%" echo         for(int i=0;i^<5;i++){
>>"%ps1%" echo             if(CM_Get_DevNode_Status(out status, out prob, curr, 0)==0){
>>"%ps1%" echo                 if((status ^& 0x00004000)!=0){
>>"%ps1%" echo                     int vetoType;
>>"%ps1%" echo                     StringBuilder vetoName=new StringBuilder(512);
>>"%ps1%" echo                     if(CM_Request_Device_Eject(curr, out vetoType, vetoName, 512, 0)==0 ^&^& vetoType==0){return 0;}
>>"%ps1%" echo                 }
>>"%ps1%" echo             }
>>"%ps1%" echo             uint parent;
>>"%ps1%" echo             if(CM_Get_Parent(out parent, curr, 0)!=0) break;
>>"%ps1%" echo             curr=parent;
>>"%ps1%" echo         }
>>"%ps1%" echo         uint p;
>>"%ps1%" echo         if(CM_Get_Parent(out p, devInst, 0)==0){
>>"%ps1%" echo             int v;
>>"%ps1%" echo             StringBuilder vn=new StringBuilder(512);
>>"%ps1%" echo             if(CM_Request_Device_Eject(p, out v, vn, 512, 0)==0 ^&^& v==0) return 0;
>>"%ps1%" echo         }
>>"%ps1%" echo         int v2;
>>"%ps1%" echo         StringBuilder vn2=new StringBuilder(512);
>>"%ps1%" echo         if(CM_Request_Device_Eject(devInst, out v2, vn2, 512, 0)==0 ^&^& v2==0) return 0;
>>"%ps1%" echo         return 3;
>>"%ps1%" echo     }
>>"%ps1%" echo }
>>"%ps1%" echo "@
>>"%ps1%" echo Add-Type -TypeDefinition $code -Language CSharp
>>"%ps1%" echo $p = Get-Partition -DriveLetter $targetDrive -ErrorAction SilentlyContinue
>>"%ps1%" echo if(!$p){ exit 1 }
>>"%ps1%" echo $d = Get-Disk -Number $p.DiskNumber -ErrorAction SilentlyContinue
>>"%ps1%" echo if($d.IsSystem -or $d.IsBoot){ exit 2 }
>>"%ps1%" echo $w = Get-CimInstance Win32_DiskDrive ^| Where-Object { $_.Index -eq $p.DiskNumber }
>>"%ps1%" echo if(!$w){ exit 1 }
>>"%ps1%" echo exit [UsbEject]::Eject($w.PNPDeviceID)

powershell -NoProfile -ExecutionPolicy Bypass -File "%ps1%"
set RES=%errorlevel%
del "%ps1%" >nul 2>&1

:: 3. 检查系统语言输出双语提示
for /f "tokens=*" %%a in ('powershell -Command "[System.Globalization.CultureInfo]::InstalledUICulture.Name.StartsWith('zh')"') do set IS_ZH=%%a

if "%IS_ZH%"=="True" (
    if %RES% EQU 0 (
        color 0a
        echo ======================================================
        echo 成功：%TARGET% 盘已安全弹出，机械硬盘马达已完全停转！
        echo.
        echo 下次插入数据线时系统会自动识别。
        echo 现在可以放心关闭硬盘盒电源或拔出数据线。
        echo ======================================================
    ) else if %RES% EQU 1 (
        color 0c
        echo 错误：未检测到挂载在 %TARGET% 盘的设备（可能已拔出）。
    ) else if %RES% EQU 2 (
        color 0c
        echo 拦截：%TARGET% 盘为系统启动盘，操作已强制终止！
    ) else if %RES% EQU 3 (
        color 0e
        echo 失败：%TARGET% 盘当前仍被其他程序占用。
        echo 请确认已关闭所有打开该盘的文件或软件，再重新运行。
    ) else (
        color 0c
        echo 异常：返回错误代码 %RES%
    )
    echo.
    echo 按任意键退出...
) else (
    if %RES% EQU 0 (
        color 0a
        echo ======================================================
        echo [SUCCESS] Drive %TARGET%: safely ejected and motor spun down!
        echo.
        echo Device will be automatically mounted next time it is connected.
        echo You can now safely turn off the enclosure power or unplug the cable.
        echo ======================================================
    ) else if %RES% EQU 1 (
        color 0c
        echo [NOT FOUND] No device detected on Drive %TARGET%:
    ) else if %RES% EQU 2 (
        color 0c
        echo [ABORTED] Drive %TARGET%: is a system/boot disk! Operation blocked.
    ) else if %RES% EQU 3 (
        color 0e
        echo [BUSY] Drive %TARGET%: is currently locked by a process.
        echo Please close applications accessing Drive %TARGET%: and try again.
    ) else (
        color 0c
        echo [ERROR] Unknown error code: %RES%
    )
    echo.
    echo Press any key to exit...
)

pause >nul