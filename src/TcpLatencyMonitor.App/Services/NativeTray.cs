using System.Runtime.InteropServices;

namespace TcpLatencyMonitor.App.Services;

// Shell notification area works on Windows 10 and 11 without a hidden AppWindow
// or the newer IsShownInSwitchers API used by the reference app's tray library.
internal sealed class NativeTray : IDisposable
{
    private const uint CallbackMessage=0x8001, IconId=0x544C;
    private readonly IntPtr _window;
    private readonly SubclassProc _procedure;
    private readonly Action _open,_toggle,_exit;
    private readonly Func<bool> _isRunning;
    private readonly uint _taskbarCreated;
    private NotifyIconData _data;
    private bool _disposed;
    public event Action<bool>? PowerChanged;
    public NativeTray(IntPtr window,string icon,Action open,Action toggle,Action exit,Func<bool> isRunning)
    {
        _window=window;_open=open;_toggle=toggle;_exit=exit;_isRunning=isRunning;_procedure=WindowProc;
        _data=new NotifyIconData {Size=(uint)Marshal.SizeOf<NotifyIconData>(),Window=window,Id=IconId,Flags=7,
            Callback=CallbackMessage,Icon=LoadImage(IntPtr.Zero,icon,1,0,0,0x50),Tip="IP 质量监控",Info="",InfoTitle=""};
        if(_data.Icon==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _taskbarCreated=RegisterWindowMessage("TaskbarCreated");
        if(!SetWindowSubclass(window,_procedure,IconId,0)) {DestroyIcon(_data.Icon);throw new InvalidOperationException("无法初始化托盘消息处理。");}
        if(!ShellNotifyIcon(0,ref _data)){Dispose();throw new InvalidOperationException("无法添加系统托盘图标。");}
    }
    private IntPtr WindowProc(IntPtr window,uint message,UIntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data)
    {
        if(message==0x218){if(wParam.ToUInt64()==4)PowerChanged?.Invoke(true);else if(wParam.ToUInt64()==18)PowerChanged?.Invoke(false);}
        if(message==_taskbarCreated&&!_disposed)ShellNotifyIcon(0,ref _data);
        if(message==CallbackMessage)
        {
            var action=(uint)lParam.ToInt64();
            if(action is 0x202 or 0x203)_open();
            else if(action==0x205)ShowMenu();
            return IntPtr.Zero;
        }
        return DefSubclassProc(window,message,wParam,lParam);
    }
    private void ShowMenu()
    {
        var menu=CreatePopupMenu();if(menu==IntPtr.Zero)return;
        try
        {
            AppendMenu(menu,0,1,"打开监控窗口");AppendMenu(menu,0,2,_isRunning()?"停止监控":"开始监控");
            AppendMenu(menu,0x800,0,null);AppendMenu(menu,0,3,"退出");
            GetCursorPos(out var point);SetForegroundWindow(_window);
            var result=TrackPopupMenu(menu,0x102,point.X,point.Y,0,_window,IntPtr.Zero);
            PostMessage(_window,0,UIntPtr.Zero,IntPtr.Zero);
            if(result==1)_open();else if(result==2)_toggle();else if(result==3)_exit();
        }
        finally{DestroyMenu(menu);}
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        ShellNotifyIcon(2,ref _data);RemoveWindowSubclass(_window,_procedure,IconId);DestroyIcon(_data.Icon);
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;public IntPtr Window;public uint Id,Flags,Callback;public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string InfoTitle;
        public uint InfoFlags;public Guid Guid;public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Point {public int X,Y;}
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr window,uint message,UIntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data);
    [DllImport("shell32.dll",EntryPoint="Shell_NotifyIconW",CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool ShellNotifyIcon(uint action,ref NotifyIconData data);
    [DllImport("comctl32.dll")] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetWindowSubclass(IntPtr window,SubclassProc procedure,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll")] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool RemoveWindowSubclass(IntPtr window,SubclassProc procedure,UIntPtr id);
    [DllImport("comctl32.dll")]private static extern IntPtr DefSubclassProc(IntPtr window,uint message,UIntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",EntryPoint="LoadImageW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr LoadImage(IntPtr instance,string name,uint type,int width,int height,uint flags);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll",EntryPoint="RegisterWindowMessageW",CharSet=CharSet.Unicode)]private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")]private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll",EntryPoint="AppendMenuW",CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool AppendMenu(IntPtr menu,uint flags,UIntPtr id,string? text);
    [DllImport("user32.dll")]private static extern uint TrackPopupMenu(IntPtr menu,uint flags,int x,int y,int reserved,IntPtr window,IntPtr rectangle);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll",EntryPoint="PostMessageW")] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool PostMessage(IntPtr window,uint message,UIntPtr wParam,IntPtr lParam);
}
