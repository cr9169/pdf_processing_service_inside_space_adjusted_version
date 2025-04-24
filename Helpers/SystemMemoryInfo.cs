using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PdfProcessingService.Services;

namespace PdfProcessingService.Helpers
{
    public class SystemMemoryInfo
    {
        private readonly ILogger<SystemMemoryInfo> _logger;

        public SystemMemoryInfo(ILogger<SystemMemoryInfo> logger)
        {
            _logger = logger;
        }

        public long GetTotalMemoryInBytes()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return GetWindowsTotalMemory();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return GetLinuxTotalMemory();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falling back to default 8GB RAM value");
            }

            return 8L * 1024 * 1024 * 1024;
        }

        public double GetUsedHeapPercent()
        {
            try
            {
                long heapUsed = GC.GetTotalMemory(false);
                long totalMemory = GetTotalMemoryInBytes();

                return Math.Round((double)heapUsed / totalMemory * 100, 2);
            }
            catch
            {
                return -1;
            }
        }

        public long GetAvailableMemoryInBytes()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                const string memInfoPath = "/proc/meminfo";
                const string availablePrefix = "MemAvailable:";

                var lines = File.ReadAllLines(memInfoPath);
                var line = lines.FirstOrDefault(l => l.StartsWith(availablePrefix));
                if (line != null)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (long.TryParse(parts[1], out var kb))
                        return kb * 1024;
                }
            }

            return -1;
        }

        public long GetTotalPhysicalMemory()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return GetWindowsTotalMemory();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return GetLinuxTotalMemory();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get physical memory, falling back to default");
            }

            return 8L * 1024 * 1024 * 1024;
        }

        [DllImport("kernel32.dll")]
        private static extern void GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

        private long GetWindowsTotalMemory()
        {
            GetPhysicallyInstalledSystemMemory(out var kb);
            return kb * 1024;
        }

        private long GetLinuxTotalMemory()
        {
            const string memInfoPath = "/proc/meminfo";
            const string memTotalPrefix = "MemTotal:";

            var lines = File.ReadAllLines(memInfoPath);
            var line = lines.FirstOrDefault(l => l.StartsWith(memTotalPrefix));
            if (line != null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (long.TryParse(parts[1], out var kb))
                    return kb * 1024;
            }

            throw new Exception("Could not determine total memory from /proc/meminfo");
        }
    }

}
