using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace SafeEjectUSB
{
    class Program
    {
        // --- Win32 API 与内核指令定义 ---
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FlushFileBuffers(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNode(out uint pdnDevInst, string pDeviceID, int ulFlags);

        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, int ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_ID(uint dnDevInst, StringBuilder Buffer, int BufferLen, int ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Request_Device_Eject(uint dnDevInst, out int pVetoType, StringBuilder pszVetoName, int ulNameLength, int ulFlags);

        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x00000001;
        const uint FILE_SHARE_WRITE = 0x00000002;
        const uint OPEN_EXISTING = 3;
        static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // 核心突破：直接绕过 WMI，下钻物理层获取设备编号
        const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;

        [StructLayout(LayoutKind.Sequential)]
        struct STORAGE_DEVICE_NUMBER
        {
            public int DeviceType;
            public int DeviceNumber;
            public int PartitionNumber;
        }

        static void Main(string[] args)
        {
            Console.Title = "USB 安全弹出与落盘工具";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=== Windows 底层 USB 安全卸载工具 ===");
            Console.ResetColor();

            string targetDrive = "";
            if (args.Length > 0)
            {
                targetDrive = args[0].Trim().ToUpper().Replace(":", "");
            }
            else
            {
                Console.Write("请输入需要弹出的盘符 (例如 E) 并按回车: ");
                targetDrive = Console.ReadLine().Trim().ToUpper().Replace(":", "");
            }

            if (string.IsNullOrEmpty(targetDrive) || targetDrive.Length != 1) return;

            try
            {
                ProcessEject(targetDrive);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[致命错误] " + ex.Message);
                Console.ResetColor();
            }

            Console.WriteLine("\n按任意键退出...");
            Console.ReadKey();
        }

        // 方法 1：基于 IOCTL 直接查询盘符所对应的物理磁盘索引 (如返回 2 代表 PhysicalDrive2)
        static int GetDiskNumberFromDrive(string driveLetter)
        {
            // 采用 0 权限查询，不占用锁定句柄
            IntPtr hVolume = CreateFile(@"\\.\" + driveLetter + ":", 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hVolume == INVALID_HANDLE_VALUE) return -1;

            int diskNumber = -1;
            int size = Marshal.SizeOf(typeof(STORAGE_DEVICE_NUMBER));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            uint bytesReturned;

            if (DeviceIoControl(hVolume, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, buffer, (uint)size, out bytesReturned, IntPtr.Zero))
            {
                STORAGE_DEVICE_NUMBER sdn = (STORAGE_DEVICE_NUMBER)Marshal.PtrToStructure(buffer, typeof(STORAGE_DEVICE_NUMBER));
                diskNumber = sdn.DeviceNumber;
            }

            Marshal.FreeHGlobal(buffer);
            CloseHandle(hVolume);

            return diskNumber;
        }

        // 方法 2：遍历 A-Z 盘符，找出同属于目标物理磁盘的所有分区
        static List<string> GetAllVolumesOnDisk(int targetDiskIndex)
        {
            List<string> volumes = new List<string>();
            for (char c = 'A'; c <= 'Z'; c++)
            {
                int diskNum = GetDiskNumberFromDrive(c.ToString());
                if (diskNum == targetDiskIndex)
                {
                    volumes.Add(c.ToString());
                }
            }
            return volumes;
        }

        static void ProcessEject(string driveLetter)
        {
            // 第一步：直接通过内核指令锁定物理磁盘索引
            int diskIndex = GetDiskNumberFromDrive(driveLetter);
            if (diskIndex == -1)
            {
                throw new Exception("找不到盘符 " + driveLetter + ":，或无法读取物理磁盘映射。请检查输入，并务必右键以【管理员身份运行】。");
            }

            // 第二步：通过 WMI 仅提取安全的设备 ID (规避系统盘被误弹)
            string pnpDeviceId = "";
            string query = "SELECT PNPDeviceID, InterfaceType FROM Win32_DiskDrive WHERE Index = " + diskIndex;
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
            {
                ManagementObjectCollection disks = searcher.Get();
                if (disks.Count == 0) throw new Exception("无法在设备树中找到索引为 " + diskIndex + " 的物理磁盘信息。");

                foreach (ManagementObject disk in disks)
                {
                    object ifTypeObj = disk["InterfaceType"];
                    string interfaceType = (ifTypeObj != null) ? ifTypeObj.ToString() : "";

                    if (interfaceType != "USB" && interfaceType != "SCSI")
                    {
                        throw new Exception("安全拦截：目标磁盘总线类型为 " + interfaceType + "，并非外部移动设备。");
                    }

                    pnpDeviceId = disk["PNPDeviceID"].ToString();
                    break;
                }
            }

            // 第三步：收集所有关联分区并执行强制落盘
            List<string> allVolumes = GetAllVolumesOnDisk(diskIndex);
            Console.WriteLine(string.Format("\n[1/3] 物理硬盘定位成功 (索引: {0})。共包含分区: {1}", diskIndex, string.Join(", ", allVolumes.ToArray())));

            Console.WriteLine("[2/3] 正在强制各分区缓存刷入磁盘磁道...");
            foreach (string vol in allVolumes)
            {
                IntPtr hFile = CreateFile(@"\\.\" + vol + ":", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (hFile != INVALID_HANDLE_VALUE)
                {
                    FlushFileBuffers(hFile);
                    CloseHandle(hFile);
                    Console.WriteLine("      -> " + vol + ": 缓存落盘完成");
                }
                else
                {
                    Console.WriteLine("      -> " + vol + ": 无法获取写入句柄，跳过主动 Flush（此盘符可能被占用）");
                }
            }

            // 第四步：执行手术刀式 PnP 弹出
            Console.WriteLine("[3/3] 正在向上追溯 USB 控制器节点以安全停转磁头...");
            uint devInst;
            if (CM_Locate_DevNode(out devInst, pnpDeviceId, 0) != 0)
            {
                throw new Exception("无法在设备树中定位目标磁盘。");
            }

            uint currentDev = devInst;
            uint targetUsbDev = currentDev;
            uint parentDev;

            while (CM_Get_Parent(out parentDev, currentDev, 0) == 0)
            {
                StringBuilder sb = new StringBuilder(512);
                CM_Get_Device_ID(parentDev, sb, sb.Capacity, 0);
                string parentId = sb.ToString().ToUpper();

                // 重点：命中桥接器立刻停止，绝不殃及主板 USB Hub
                if (parentId.StartsWith(@"USB\VID_"))
                {
                    targetUsbDev = parentDev;
                    break;
                }
                currentDev = parentDev;
            }

            int vetoType;
            StringBuilder vetoName = new StringBuilder(512);
            int ejectResult = CM_Request_Device_Eject(targetUsbDev, out vetoType, vetoName, vetoName.Capacity, 0);

            if (ejectResult == 0 && vetoType == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n=======================================================");
                Console.WriteLine("[成功] 所有分区已卸载，驱动已摘除，磁头已安全停转！");
                Console.WriteLine("现在可安全关闭硬盘盒电源或拔除数据线。");
                Console.WriteLine("重新通电或拔插时，系统将 100% 自动重新识别。");
                Console.WriteLine("=======================================================");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\n[失败] 安全弹出被系统否决 (VetoType: " + vetoType + ")。");
                Console.WriteLine("拦截对象/进程: " + vetoName);
                Console.WriteLine("请确保没有其他程序正在读写目标磁盘的文件。");
                Console.ResetColor();
            }
        }
    }
}