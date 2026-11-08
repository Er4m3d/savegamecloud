using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SaveGameCloud.Core.Infrastructure;

namespace SaveGameCloud.Core.Launch;

/// <summary>
/// Lance le jeu et attend qu'il soit vraiment fermé. Le processus est placé dans un « job » Windows
/// pour suivre aussi les processus enfants (beaucoup de vieux jeux passent par un petit lanceur).
/// </summary>
public static class GameLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const int ErrorElevationRequired = 740;
    private const int JobObjectBasicAccountingInformation = 1;
    private const uint Infinite = 0xFFFFFFFF;

    public static async Task RunAndWaitAsync(string exePath, string arguments, string? workingDirectory, CancellationToken ct)
    {
        workingDirectory ??= Path.GetDirectoryName(exePath);

        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            await ShellExecuteAndWaitAsync(exePath, arguments, workingDirectory, elevated: false, ct).ConfigureAwait(false);
            return;
        }

        var job = CreateJobObjectW(IntPtr.Zero, null);
        try
        {
            var commandLine = new StringBuilder($"\"{exePath}\" {arguments}".TrimEnd());
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended,
                    IntPtr.Zero, workingDirectory, ref si, out var pi))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorElevationRequired)
                {
                    Log.Info("Le jeu demande les droits administrateur, lancement via UAC");
                    await ShellExecuteAndWaitAsync(exePath, arguments, workingDirectory, elevated: true, ct).ConfigureAwait(false);
                    return;
                }
                throw new Win32Exception(error, $"Impossible de lancer {exePath}");
            }

            try
            {
                var inJob = job != IntPtr.Zero && AssignProcessToJobObject(job, pi.hProcess);
                if (!inJob) Log.Warn($"Suivi des processus enfants indisponible (erreur {Marshal.GetLastWin32Error()})");
                ResumeThread(pi.hThread);
                Log.Info($"Jeu lancé : {exePath} (pid {pi.dwProcessId})");

                // On attend d'abord le processus principal…
                await Task.Run(() => WaitForSingleObject(pi.hProcess, Infinite), ct).ConfigureAwait(false);

                // …puis tous ses descendants encore en vie.
                if (inJob)
                {
                    while (ActiveProcessCount(job) > 0)
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                Log.Info($"Jeu terminé : {exePath}");
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            if (job != IntPtr.Zero) CloseHandle(job);
        }
    }

    private static async Task ShellExecuteAndWaitAsync(string path, string arguments, string? workingDirectory, bool elevated, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(path)
        {
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? "",
            UseShellExecute = true,
        };
        if (elevated) psi.Verb = "runas";

        using var process = Process.Start(psi);
        if (process is null)
        {
            Log.Warn("Aucun processus à surveiller, impossible de détecter la fermeture du jeu");
            return;
        }
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
    }

    private static uint ActiveProcessCount(IntPtr job)
    {
        return QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out var info,
            Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
            ? info.ActiveProcesses
            : 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info,
        int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
