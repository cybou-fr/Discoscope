using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Discoscope.Desktop.Services;

internal static class WindowsRecycleBin
{
    private const uint Silent = 0x0004;
    private const uint NoErrorUi = 0x0400;
    private const uint NoRecursion = 0x1000;
    private const uint NoConnectedElements = 0x2000;
    private const uint EarlyFailure = 0x00100000;
    private const uint RecycleOnDelete = 0x00080000;

    internal static void MoveFile(string path, Action verifyBeforeExecution)
    {
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            var kind = Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), throwOnError: true)!;
            operation = (IFileOperation)Activator.CreateInstance(kind)!;
            operation.SetOperationFlags(Silent | NoErrorUi | NoRecursion | NoConnectedElements | EarlyFailure | RecycleOnDelete);
            var itemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, in itemId, out item));
            var sink = new RecycleProgressSink(verifyBeforeExecution);
            operation.DeleteItem(item, sink);

            int result = operation.PerformOperations();
            int abortResult = operation.GetAnyOperationsAborted(out bool aborted);
            sink.VerificationFailure?.Throw();
            Marshal.ThrowExceptionForHR(result);
            Marshal.ThrowExceptionForHR(abortResult);
            if (aborted)
                throw new IOException("Windows n’a pas terminé le déplacement vers la Corbeille.");
            Marshal.ThrowExceptionForHR(sink.DeleteResult);
            if (!sink.Recycled)
                throw new IOException("Windows n’a pas confirmé la réception du fichier dans la Corbeille.");
        }
        catch (COMException ex)
        {
            throw new IOException($"Déplacement vers la Corbeille impossible (0x{ex.ErrorCode:X8}).", ex);
        }
        finally
        {
            if (operation is not null) Marshal.ReleaseComObject(operation);
            if (item is not null) Marshal.ReleaseComObject(item);
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext,
        in Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleProgressSink(Action verify) : IFileOperationProgressSink
    {
        private const uint RecycleIfPossible = 0x80;
        private const int Abort = unchecked((int)0x80004004);

        public ExceptionDispatchInfo? VerificationFailure { get; private set; }
        public int DeleteResult { get; private set; }
        public bool Recycled { get; private set; }

        public int PreDeleteItem(uint flags, IntPtr item)
        {
            try
            {
                if ((flags & RecycleIfPossible) == 0)
                    throw new IOException("Windows n’a pas demandé un déplacement vers la Corbeille.");
                verify();
                return 0;
            }
            catch (Exception ex)
            {
                VerificationFailure = ExceptionDispatchInfo.Capture(ex);
                return Abort;
            }
        }

        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr recycledItem)
        {
            DeleteResult = result;
            Recycled = result >= 0 && recycledItem != IntPtr.Zero;
            return 0;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IntPtr item, IntPtr name) => Abort;
        public int PostRenameItem(uint flags, IntPtr item, IntPtr name, int result, IntPtr created) => 0;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name) => Abort;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created) => 0;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name) => Abort;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created) => 0;
        public int PreNewItem(uint flags, IntPtr destination, IntPtr name) => Abort;
        public int PostNewItem(uint flags, IntPtr destination, IntPtr name, IntPtr template, uint attributes, int result, IntPtr created) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, IntPtr name);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item, IntPtr name, int result, IntPtr created);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr recycledItem);
        [PreserveSig] int PreNewItem(uint flags, IntPtr destination, IntPtr name);
        [PreserveSig] int PostNewItem(uint flags, IntPtr destination, IntPtr name, IntPtr template, uint attributes, int result, IntPtr created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    // Keep the native IFileOperation slot order, including members not used here.
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void MoveItems(IntPtr items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void CopyItems(IntPtr items, IShellItem destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        void DeleteItems(IntPtr items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string? template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
