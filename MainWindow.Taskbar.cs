using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Shell;

namespace ADBToolsNew
{
    /// <summary>
    /// Icône de la barre des tâches Windows, comme les programmes qui travaillent en arrière-plan : barre de
    /// progression verte animée tant qu'une tâche tourne (logs en direct, mesure Wi-Fi, enregistrement vidéo,
    /// mise à jour OS…), progression réelle pendant une mise à jour OS, et clignotement orange à la fin d'une
    /// mise à jour si ADB Tools n'est pas au premier plan.
    /// </summary>
    public partial class MainWindow
    {
        private readonly HashSet<string> _taskbarBusy = new HashSet<string>();
        private double? _taskbarProgress;

        /// <summary>Signale qu'une tâche de fond démarre ou s'arrête (clé = nom de la tâche).</summary>
        private void SetTaskbarBusy(string key, bool busy)
        {
            if (string.IsNullOrEmpty(key)) return;
            bool changed = busy ? _taskbarBusy.Add(key) : _taskbarBusy.Remove(key);
            if (changed) UpdateTaskbar();
        }

        /// <summary>Progression réelle (0 à 1) d'une tâche longue, ou null pour revenir à l'état général.</summary>
        private void SetTaskbarProgress(double? value)
        {
            _taskbarProgress = value;
            UpdateTaskbar();
        }

        private void UpdateTaskbar()
        {
            try
            {
                if (TaskbarItemInfo == null) TaskbarItemInfo = new TaskbarItemInfo();
                if (_taskbarProgress.HasValue)
                {
                    TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
                    TaskbarItemInfo.ProgressValue = Math.Max(0.03, Math.Min(1, _taskbarProgress.Value));
                }
                else
                {
                    TaskbarItemInfo.ProgressState = _taskbarBusy.Count > 0 ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.None;
                }
            }
            catch { /* barre des tâches indisponible (session sans Explorateur) : sans conséquence */ }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

        /// <summary>Fait clignoter l'icône de la barre des tâches jusqu'à ce qu'ADB Tools revienne au premier plan.</summary>
        private void FlashTaskbarIfInBackground()
        {
            try
            {
                if (IsActive) return;
                var fi = new FLASHWINFO
                {
                    hwnd = new WindowInteropHelper(this).Handle,
                    dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                    uCount = uint.MaxValue,
                    dwTimeout = 0
                };
                fi.cbSize = (uint)Marshal.SizeOf(fi);
                FlashWindowEx(ref fi);
            }
            catch { }
        }
    }
}
