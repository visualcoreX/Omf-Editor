using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OMF_Editor
{
    // The folder picker Windows itself uses: a normal explorer window with a
    // path bar, places and search. WinForms only ever exposes the old tree, so
    // the shell dialog is asked for directly, with that tree left as a fallback
    // for the case it cannot be created.
    static class FolderPicker
    {
        public static string Select(IWin32Window owner, string title, string startPath)
        {
            try
            {
                return SelectShell(owner, title, startPath);
            }
            catch (Exception)
            {
                return SelectLegacy(title, startPath);
            }
        }

        private static string SelectLegacy(string title, string startPath)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = title;
                if (!string.IsNullOrEmpty(startPath))
                    dialog.SelectedPath = startPath;
                return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        private static string SelectShell(IWin32Window owner, string title, string startPath)
        {
            IFileOpenDialog dialog = (IFileOpenDialog)new FileOpenDialogRcw();
            try
            {
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                dialog.SetTitle(title);

                if (!string.IsNullOrEmpty(startPath))
                {
                    IShellItem start;
                    Guid itemId = IID_IShellItem;
                    if (SHCreateItemFromParsingName(startPath, IntPtr.Zero, ref itemId, out start) == 0)
                        dialog.SetFolder(start);
                }

                IntPtr handle = owner != null ? owner.Handle : IntPtr.Zero;
                if (dialog.Show(handle) != 0)		// cancelled
                    return null;

                IShellItem result;
                dialog.GetResult(out result);

                string path;
                result.GetDisplayName(SIGDN_FILESYSPATH, out path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        const uint FOS_PICKFOLDERS = 0x00000020;
        const uint FOS_FORCEFILESYSTEM = 0x00000040;
        const uint FOS_PATHMUSTEXIST = 0x00000800;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        static readonly Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext,
            ref Guid riid, out IShellItem item);

        [ComImport, ClassInterface(ClassInterfaceType.None)]
        [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        // The methods have to be listed in their exact order, that is what makes
        // up the vtable - even the ones never called from here.
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filterSpec);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem place, int order);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid client);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            // IFileOpenDialog adds these two on top of IFileDialog
            void GetResults(out IntPtr items);
            void GetSelectedItems(out IntPtr items);
        }
    }
}
