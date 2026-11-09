using System.Runtime.InteropServices;
namespace WinStatusBar.Services;
internal static partial class ContextActions
{
    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")] private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache(); void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache(); void GetFocusedElementBuildCache(); void CreateTreeWalker();
        void get_ControlViewWalker(); void get_ContentViewWalker(); void get_RawViewWalker();
        void get_RawViewCondition(); void get_ControlViewCondition(); void get_ContentViewCondition();
        void CreateCacheRequest(); IUIAutomationCondition CreateTrueCondition(); void CreateFalseCondition();
        IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus(); [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] GetRuntimeId();
        IUIAutomationElement? FindFirst(int scope, IUIAutomationCondition condition);
        IUIAutomationElementArray? FindAll(int scope, IUIAutomationCondition condition);
        void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object? GetCurrentPropertyValue(int propertyId);
        void GetCurrentPropertyValueEx(); void GetCachedPropertyValue(); void GetCachedPropertyValueEx();
        void GetCurrentPatternAs(); void GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)] object? GetCurrentPattern(int patternId);
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int get_Length();
        IUIAutomationElement GetElement(int index);
    }

    [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextPattern
    {
        void RangeFromPoint(); void RangeFromChild();
        IUIAutomationTextRangeArray? GetSelection();
        void GetVisibleRanges();
        IUIAutomationTextRange get_DocumentRange();
    }

    [ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRangeArray
    {
        int get_Length();
        IUIAutomationTextRange GetElement(int index);
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRange
    {
        void Clone(); void Compare(); void CompareEndpoints(); void ExpandToEnclosingUnit(); void FindAttribute();
        void FindText(); void GetAttributeValue(); void GetBoundingRectangles(); void GetEnclosingElement();
        [return: MarshalAs(UnmanagedType.BStr)] string GetText(int maxLength);
    }

    [ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationValuePattern
    {
        void SetValue([MarshalAs(UnmanagedType.LPWStr)] string value);
        [return: MarshalAs(UnmanagedType.BStr)] string get_CurrentValue();
    }


    [ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInvoke { void Invoke(); }
    [ComImport, Guid("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToggle { void Toggle(); int get_CurrentToggleState(); }
    [ComImport, Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISelect { void Select(); }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder text, int count);
}
