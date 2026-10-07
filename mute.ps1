param([switch]$Unmute, [switch]$Mute)
$src = @"
using System; using System.Runtime.InteropServices;
public static class MU {
  [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class E { }
  [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IE { int a(int f,int m,out IntPtr c); int b(int f,int r,out IntPtr d); int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out ID d); int c(IntPtr p); int d(IntPtr p); }
  [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface ID { int Activate(ref Guid i,int c,IntPtr p,[MarshalAs(UnmanagedType.IUnknown)] out object o); int g(int a, out IntPtr p); int h([MarshalAs(UnmanagedType.LPWStr)] out string s); int i(out int s); }
  [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IV { int r(IntPtr p); int u(IntPtr p); int gc(out int c); int s1(float v, ref Guid g); int s2(float v, ref Guid g); int g1(out float v); int g2(out float v);
    int s3(int c,float v, ref Guid g); int s4(int c,float v, ref Guid g); int g3(int c,out float v); int g4(int c,out float v);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid g); int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m); }
  public static string Set(bool render, string guid, bool mute) {
    var e = (IE)new E(); ID d; if (e.GetDevice((render?"{0.0.0.00000000}.":"{0.0.1.00000000}.")+guid, out d)!=0) return "no-dev";
    object o; Guid iid=new Guid("5CDF2C82-841E-4546-9722-0CF74078229A"); if (d.Activate(ref iid,23,IntPtr.Zero,out o)!=0) return "no-act";
    var v=(IV)o; Guid g=Guid.Empty; int hr=v.SetMute(mute, ref g); bool m; v.GetMute(out m); return hr==0 ? (m?"MUTED":"unmuted") : "err"; }
}
"@
Add-Type -TypeDefinition $src
$state = -not $Unmute
'render  : ' + [MU]::Set($true, '{d9de2a1b-0591-4df1-b07a-9117457c3778}', $state)
'capture : ' + [MU]::Set($false, '{a5b46a78-bb95-44ed-a8e5-4c42dd4ea58c}', $state)