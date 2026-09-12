using System.Runtime.InteropServices;
using System.Text;

namespace TcpLatencyMonitor.App.Services;

// DPAPI binds the token to the current Windows user. Never included in settings or diagnostic exports.
internal static class GeoTokenStore
{
    private static string PathName(string provider)=>Path.Combine(AppPaths.DataDirectory,provider=="nexttrace"?"nexttrace-token.dpapi":"ipinfo-token.dpapi");
    [StructLayout(LayoutKind.Sequential)] private struct Blob{public int Size;public IntPtr Data;}
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input,string description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static byte[] Transform(byte[] bytes,bool protect)
    {
        var input=new Blob{Size=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};Blob output=default;
        try
        {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            bool ok=protect?CryptProtectData(ref input,"IpQualityMonitor IPinfo",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if(!ok)throw new IOException("无法读取或保存当前 Windows 用户的加密 Token。");
            var result=new byte[output.Size];Marshal.Copy(output.Data,result,0,result.Length);return result;
        }
        finally{Marshal.FreeHGlobal(input.Data);if(output.Data!=IntPtr.Zero)LocalFree(output.Data);Array.Clear(bytes);}
    }
    public static string Read(string provider="ipinfo-core")
    {
        string path=PathName(provider);
        if(!File.Exists(path))return "";
        if(new FileInfo(path).Length>16384)throw new IOException("Token 文件无效。");
        var plain=Transform(File.ReadAllBytes(path),false);try{return Encoding.UTF8.GetString(plain);}finally{Array.Clear(plain);}
    }
    public static void Save(string value,string provider="ipinfo-core")
    {
        string path=PathName(provider);
        Directory.CreateDirectory(AppPaths.DataDirectory);
        if(value.Length==0){if(File.Exists(path))File.Delete(path);return;}
        var encrypted=Transform(Encoding.UTF8.GetBytes(value),true);string temp=path+".tmp";
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){stream.Write(encrypted);stream.Flush(true);}File.Move(temp,path,true);
    }
}
