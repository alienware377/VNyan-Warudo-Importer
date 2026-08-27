using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace WarudoImporter
{
    internal static class NativeFileDialog
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public IntPtr lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpstrInitialDir;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        const int OFN_FILEMUSTEXIST = 0x00001000;
        const int OFN_PATHMUSTEXIST = 0x00000800;
        const int OFN_NOCHANGEDIR   = 0x00000008;
        const int OFN_EXPLORER      = 0x00080000;

        public static string OpenFile(string title, string initialDir,
                                      string filterLabel, string filterPattern)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer &&
                Application.platform != RuntimePlatform.WindowsEditor)
                return null;

            const int bufChars = 4096;
            IntPtr fileBuf   = Marshal.AllocHGlobal(bufChars * 2);
            IntPtr titleBuf  = Marshal.AllocHGlobal(512 * 2);
            IntPtr filterBuf = Marshal.StringToHGlobalUni(
                (filterLabel ?? "Files") + "\0" + (filterPattern ?? "*.*") + "\0");
            try
            {
                Marshal.WriteInt16(fileBuf, 0, 0);
                Marshal.WriteInt16(titleBuf, 0, 0);

                OpenFileName ofn = new OpenFileName();
                ofn.lStructSize    = Marshal.SizeOf(typeof(OpenFileName));
                ofn.hwndOwner      = GetActiveWindow();
                ofn.lpstrFilter    = filterBuf;
                ofn.nFilterIndex   = 1;
                ofn.lpstrFile      = fileBuf;
                ofn.nMaxFile       = bufChars;
                ofn.lpstrFileTitle = titleBuf;
                ofn.nMaxFileTitle  = 512;
                ofn.lpstrInitialDir = initialDir;
                ofn.lpstrTitle     = title;
                ofn.Flags          = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER;

                bool ok = GetOpenFileNameW(ref ofn);
                if (!ok) return null;
                return Marshal.PtrToStringUni(fileBuf);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WarudoImporter] Open dialog unavailable: " + e.Message);
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(fileBuf);
                Marshal.FreeHGlobal(titleBuf);
                Marshal.FreeHGlobal(filterBuf);
            }
        }
    }
}

