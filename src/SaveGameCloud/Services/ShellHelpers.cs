using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Core.Models;

namespace SaveGameCloud.Services;

public static class ShellHelpers
{
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Icône d'un exécutable (32 px), mise en cache.</summary>
    public static ImageSource? GetIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        path = Path.GetFullPath(path);
        if (IconCache.TryGetValue(path, out var cached)) return cached;

        ImageSource? image = null;
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), 0x100 /* SHGFI_ICON | LARGEICON */) != IntPtr.Zero
            && info.hIcon != IntPtr.Zero)
        {
            try
            {
                image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        return IconCache[path] = image;
    }

    /// <summary>Crée un raccourci sur le bureau qui lance le jeu via SaveGameCloud (avec synchro).</summary>
    public static string CreateDesktopShortcut(GameEntry game)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(desktop, AppPaths.SafeFileName(game.Name) + ".lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell indisponible");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = Environment.ProcessPath;
            shortcut.Arguments = $"--launch {game.Id}";
            shortcut.WorkingDirectory = AppContext.BaseDirectory;
            shortcut.Description = $"{game.Name} (sauvegardes synchronisées par SaveGameCloud)";
            if (File.Exists(game.ResolvedExePath)) shortcut.IconLocation = game.ResolvedExePath + ",0";
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
        return path;
    }

    public static void OpenInExplorer(string path)
    {
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
        {
            Directory.CreateDirectory(path);
            Process.Start("explorer.exe", $"\"{path}\"");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
