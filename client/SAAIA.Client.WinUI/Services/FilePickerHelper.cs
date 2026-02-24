using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Windows.Storage;
using Windows.Storage.Pickers;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Robust file picker for WinUI 3 unpackaged apps.
/// - Tries WinRT FileOpenPicker (preferred).
/// - Falls back to native shell IFileOpenDialog if the WinRT picker fails.
/// </summary>
internal static class FilePickerHelper
{
    public static async Task<string?> PickFileAsync(IntPtr parentHwnd, params string[] extensions)
    {
        // 1) Preferred: WinRT picker
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };

            var exts = (extensions ?? Array.Empty<string>())
                .Select(NormalizeExt)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var ext in exts)
                picker.FileTypeFilter.Add(ext);

            // IMPORTANT (WinUI 3 desktop): initialize with window handle
            WinRT.Interop.InitializeWithWindow.Initialize(picker, parentHwnd);

            StorageFile? file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch
        {
            // 2) Fallback: native shell dialog
            return NativeFileDialog.OpenFile(parentHwnd, extensions);
        }
    }

    private static string? NormalizeExt(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return null;
        var e = ext.Trim();
        if (e == "*") return null; // FileOpenPicker doesn't accept '*'
        if (!e.StartsWith('.')) e = "." + e;
        return e;
    }

    private static class NativeFileDialog
    {
        // IFileOpenDialog CLSID
        private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        // IShellItem IID
        private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

        public static string? OpenFile(IntPtr parentHwnd, params string[] extensions)
        {
            object? dialogObj = null;
            try
            {
                var dialogType = Type.GetTypeFromCLSID(ClsidFileOpenDialog, throwOnError: true);
                dialogObj = Activator.CreateInstance(dialogType!);
                var dialog = (IFileOpenDialog)dialogObj!;

                // Options
                dialog.GetOptions(out var options);
                options |= (uint)FOS.FOS_FORCEFILESYSTEM;
                options |= (uint)FOS.FOS_FILEMUSTEXIST;
                options |= (uint)FOS.FOS_PATHMUSTEXIST;
                dialog.SetOptions(options);

                // Filters (best-effort)
                var specs = BuildFilterSpec(extensions);
                if (specs.Length > 0)
                {
                    dialog.SetFileTypes((uint)specs.Length, specs);
                    dialog.SetFileTypeIndex(1);
                }

                var hr = dialog.Show(parentHwnd);
                if (hr != 0) return null; // cancelled or failed

                dialog.GetResult(out var shellItem);
                shellItem.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var psz);
                var path = Marshal.PtrToStringUni(psz);
                if (psz != IntPtr.Zero) Marshal.FreeCoTaskMem(psz);

                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (dialogObj is not null)
                {
                    try { Marshal.ReleaseComObject(dialogObj); } catch { }
                }
            }
        }

        private static COMDLG_FILTERSPEC[] BuildFilterSpec(string[]? extensions)
        {
            if (extensions is null || extensions.Length == 0) return Array.Empty<COMDLG_FILTERSPEC>();

            var exts = extensions
                .Select(e => (e ?? string.Empty).Trim())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.StartsWith('.') ? e : "." + e)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (exts.Length == 0) return Array.Empty<COMDLG_FILTERSPEC>();

            // single combined filter + "All files"
            var combinedSpec = string.Join(';', exts.Select(e => "*" + e));

            return new[]
            {
                new COMDLG_FILTERSPEC { pszName = string.Join(", ", exts) + " files", pszSpec = combinedSpec },
                new COMDLG_FILTERSPEC { pszName = "All files", pszSpec = "*.*" },
            };
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct COMDLG_FILTERSPEC
        {
            public string pszName;
            public string pszSpec;
        }

        [ComImport]
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            // IModalWindow
            [PreserveSig] int Show(IntPtr parent);

            // IFileDialog (partial)
            void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IntPtr psi);
            void SetFolder(IntPtr psi);
            void GetFolder(out IntPtr ppsi);
            void GetCurrentSelection(out IntPtr ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IntPtr psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);

            // IFileOpenDialog
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        private enum SIGDN : uint
        {
            SIGDN_FILESYSPATH = 0x80058000
        }

        [Flags]
        private enum FOS : uint
        {
            FOS_FORCEFILESYSTEM = 0x00000040,
            FOS_FILEMUSTEXIST = 0x00001000,
            FOS_PATHMUSTEXIST = 0x00000800,
        }
    }
}
