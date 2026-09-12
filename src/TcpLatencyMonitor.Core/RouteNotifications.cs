using System.Runtime.InteropServices;

namespace TcpLatencyMonitor.Core;

// Callbacks only signal the coordinator. Never query routes or unregister inside
// the Windows callback; CancelMibChangeNotify2 may wait for that callback to finish.
internal sealed class RouteNotifications : IDisposable
{
    private readonly Callback _callback;
    private IntPtr _handle;
    public RouteNotifications(Action changed)
    {
        _callback=(_,_,_)=>changed();
        if(OperatingSystem.IsWindows()&&NotifyRouteChange2(0,_callback,IntPtr.Zero,false,out _handle)!=0)_handle=IntPtr.Zero;
    }
    public void Dispose(){if(_handle!=IntPtr.Zero){CancelMibChangeNotify2(_handle);_handle=IntPtr.Zero;}GC.KeepAlive(_callback);}
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate void Callback(IntPtr context,IntPtr row,int notification);
    [DllImport("iphlpapi.dll")]private static extern uint NotifyRouteChange2(ushort family,Callback callback,IntPtr context,[MarshalAs(UnmanagedType.U1)]bool initial,out IntPtr handle);
    [DllImport("iphlpapi.dll")]private static extern uint CancelMibChangeNotify2(IntPtr handle);
}
