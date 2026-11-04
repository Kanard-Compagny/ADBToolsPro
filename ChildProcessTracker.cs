using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ADBToolsNew
{
    /// <summary>
    /// Gère les processus enfants via les Job Objects Windows pour s'assurer 
    /// qu'ils sont tués lorsque l'application principale se ferme (même en cas de crash).
    /// </summary>
    public static class ChildProcessTracker
    {
        private static readonly IntPtr s_jobHandle;

        /// <summary>Raison pour laquelle la protection est inactive (null si elle fonctionne). Le Job Object est
        /// un filet de sécurité : s'il est refusé (PC verrouillé par une stratégie de groupe, antivirus, process
        /// déjà dans un job non imbricable...), l'application doit continuer à fonctionner sans lui.</summary>
        public static string DisabledReason { get; private set; }

        static ChildProcessTracker()
        {
            // Un constructeur statique qui lève une exception rend la classe inutilisable pour toute la session
            // (TypeInitializationException à chaque appel) : auparavant, plus aucune commande adb ne partait.
            try
            {
                s_jobHandle = CreateJobFromSystem();
            }
            catch (Exception ex)
            {
                s_jobHandle = IntPtr.Zero;
                DisabledReason = ex.Message;
                Debug.WriteLine($"[ChildProcessTracker] Job Object indisponible, nettoyage automatique désactivé : {ex.Message}");
            }
        }

        private static IntPtr CreateJobFromSystem()
        {
            // Les Job Objects avec le flag KillOnJobClose sont bien supportés depuis Windows 8
            // (Windows 8 permet les jobs imbriqués, si l'app est lancée via Visual Studio par ex).
            IntPtr jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero) throw new Win32Exception();

            var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000
                // C'est le drapeau magique : la fermeture du handle (quand l'app meurt) tue le job
                LimitFlags = 0x2000
            };

            var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = info
            };

            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);

            try
            {
                Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);

                if (!SetInformationJobObject(jobHandle, JobObjectInfoType.ExtendedLimitInformation, extendedInfoPtr, (uint)length))
                {
                    var error = new Win32Exception(); // Erreur native exacte
                    CloseHandle(jobHandle);
                    throw error;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(extendedInfoPtr); // Prévention des fuites mémoire
            }

            return jobHandle;
        }

        /// <summary>
        /// Attache un processus au Job Object de l'application.
        /// </summary>
        public static void AddProcess(Process process)
        {
            if (s_jobHandle == IntPtr.Zero || process == null) return;

            // Jamais d'exception ici : un refus d'attachement (process déjà terminé, droits insuffisants) ne doit
            // pas faire échouer la commande adb/scrcpy/ffmpeg qui vient d'être lancée. Seul le nettoyage
            // automatique à la fermeture est perdu pour ce process.
            try
            {
                if (!AssignProcessToJobObject(s_jobHandle, process.Handle) && !process.HasExited)
                {
                    Debug.WriteLine($"[ChildProcessTracker] Attachement refusé : {new Win32Exception().Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChildProcessTracker] Attachement impossible : {ex.Message}");
            }
        }

        #region P/Invoke Signatures & Structs

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public Int64 PerProcessUserTimeLimit;
            public Int64 PerJobUserTimeLimit;
            public UInt32 LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public UInt32 ActiveProcessLimit;
            public IntPtr Affinity;
            public UInt32 PriorityClass;
            public UInt32 SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public UInt64 ReadOperationCount;
            public UInt64 WriteOperationCount;
            public UInt64 OtherOperationCount;
            public UInt64 ReadTransferCount;
            public UInt64 WriteTransferCount;
            public UInt64 OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        #endregion
    }
}