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
    
        // ----- folder picker -------------------------------------------------------------

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] private class FileOpenDialogRcw { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show([In] IntPtr parent);
            void SetFileTypes(); void SetFileTypeIndex(); void GetFileTypeIndex();
            void Advise(); void Unadvise();
            void SetOptions([In] uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder([MarshalAs(UnmanagedType.Interface)] object psi);
            void SetFolder([MarshalAs(UnmanagedType.Interface)] object psi);
            void GetFolder([MarshalAs(UnmanagedType.Interface)] out object ppsi);
            void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out object ppsi);
            void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult([MarshalAs(UnmanagedType.Interface)] out object ppsi);
            void AddPlace([MarshalAs(UnmanagedType.Interface)] object psi, int alignment);
            void SetDefaultExtension([In, MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close([MarshalAs(UnmanagedType.Error)] int hr);
            void SetClientGuid(); void ClearClientData(); void SetFilter([MarshalAs(UnmanagedType.Interface)] object filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(); void GetParent();
            void GetDisplayName([In] uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(); void Compare();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [In][MarshalAs(UnmanagedType.LPWStr)] string path, [In] IntPtr bc,
            [In][MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [Out][MarshalAs(UnmanagedType.Interface)] out object item);

        /// <summary>
        /// Folder picker. Windows only offers this through the COM dialog - the classic
        /// comdlg32 file dialog cannot select a directory.
        /// </summary>
        public static string PickFolder(string title, string initialDir)
        {
            IFileDialog dialog = null;
            try
            {
                dialog = (IFileDialog)new FileOpenDialogRcw();
                dialog.SetTitle(title ?? "Choose a folder");
                dialog.SetOptions(0x00000020 /* FOS_PICKFOLDERS */ | 0x00000008 /* FOS_NOCHANGEDIR */
                                | 0x00001000 /* FOS_FILEMUSTEXIST */ | 0x00000800 /* FOS_PATHMUSTEXIST */);

                if (!string.IsNullOrEmpty(initialDir) && System.IO.Directory.Exists(initialDir))
                {
                    try
                    {
                        object folder;
                        SHCreateItemFromParsingName(initialDir, IntPtr.Zero,
                            new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), out folder);
                        dialog.SetFolder(folder);
                    }
                    catch { }
                }

                if (dialog.Show(IntPtr.Zero) != 0) return null;   // cancelled

                object result;
                dialog.GetResult(out result);
                string path;
                ((IShellItem)result).GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out path);
                return path;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WarudoImporter] Folder picker failed: " + e.Message);
                return null;
            }
            finally
            {
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }
}
}

