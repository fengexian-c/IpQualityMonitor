using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TcpLatencyMonitor.Core;

public sealed record NetworkContext(string Key,string Description)
{
    public static NetworkContext Read(string destination)
    {
        if(!OperatingSystem.IsWindows())return new("unknown","本地选路信息不可用");
        var ip=IPAddress.Parse(destination);var dst=new byte[28];WindowsHopProbe.SocketBytes(ip).CopyTo(dst,0);
        var row=Marshal.AllocHGlobal(256);var source=Marshal.AllocHGlobal(28);
        try
        {
            Marshal.Copy(new byte[256],0,row,256);Marshal.Copy(new byte[28],0,source,28);
            uint error=GetBestRoute2(IntPtr.Zero,0,IntPtr.Zero,dst,0,row,source);
            if(error!=0)return new($"unavailable:{error}",$"无法获取系统选路信息（{error}）");
            uint index=unchecked((uint)Marshal.ReadInt32(row,8));long luid=Marshal.ReadInt64(row);
            var sourceIp=ReadSocket(source);var next=ReadSocket(row+44);
            string name=$"接口 {index}";
            foreach(var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try{var props=nic.GetIPProperties();int? id=ip.AddressFamily==AddressFamily.InterNetwork?props.GetIPv4Properties()?.Index:props.GetIPv6Properties()?.Index;if(id==index){name=nic.Name;break;}}catch(NetworkInformationException){}
            }
            return new($"{luid}|{sourceIp}|{next}",$"系统选路：{name} · 源 {sourceIp} · 下一跳 {next}（可能受 VPN/TUN 影响）");
        }
        finally{Marshal.FreeHGlobal(row);Marshal.FreeHGlobal(source);}
    }
    private static string ReadSocket(IntPtr ptr)
    {
        int family=Marshal.ReadInt16(ptr);if(family==2){var b=new byte[4];Marshal.Copy(ptr+4,b,0,4);return new IPAddress(b).ToString();}
        if(family==23){var b=new byte[16];Marshal.Copy(ptr+8,b,0,16);return new IPAddress(b,unchecked((uint)Marshal.ReadInt32(ptr,24))).ToString();}return "未知";
    }
    [DllImport("iphlpapi.dll")]private static extern uint GetBestRoute2(IntPtr luid,uint index,IntPtr source,byte[] destination,uint options,IntPtr row,IntPtr bestSource);
}
