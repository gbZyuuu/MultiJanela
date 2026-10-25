using System.Diagnostics;
using System.Runtime.InteropServices;
namespace RobloxMultiManager.Core;
public sealed record AccountVolume(Guid ProfileId,int Volume);
public sealed record AudioPreferences(bool Enabled=false,int GlobalVolume=100,bool MuteBackground=false,AccountVolume[]? Accounts=null)
{
    public int ForAccount(Guid? id)=>Accounts?.FirstOrDefault(a=>a.ProfileId==id)?.Volume??GlobalVolume;
    public static AudioPreferences Validate(AudioPreferences p)
    {var a=p.Accounts??[];if(p.GlobalVolume is <0 or >100||a.Length>2000||a.Any(x=>x.ProfileId==Guid.Empty||x.Volume is <0 or >100)||a.Select(x=>x.ProfileId).Distinct().Count()!=a.Length)throw new InvalidDataException();return p with{Accounts=a};}
}
public sealed record AudioTarget(int Pid,DateTime StartUtc,Guid? ProfileId);
public interface IProcessAudioSession:IDisposable { string Id{get;} int Pid{get;} float Volume{get;set;} bool Muted{get;set;} }
public interface IProcessAudioBackend { IEnumerable<IProcessAudioSession> Enumerate(IReadOnlySet<int> pids); }
public sealed class ProcessAudioService(IProcessAudioBackend? backend=null):IDisposable
{
    private sealed record Original(AudioTarget Target,float Volume,bool Muted,float AppliedVolume,bool AppliedMute);
    private readonly IProcessAudioBackend _backend=backend??new WindowsAudioBackend();
    private readonly Dictionary<string,Original> _original=[];
    public void Reconcile(AudioTarget[] targets,AudioPreferences preferences,int focusedPid,Func<AudioTarget,bool>? identity=null)
    {
        preferences=AudioPreferences.Validate(preferences);identity??=IsSame;
        foreach(var stale in _original.Where(p=>!identity(p.Value.Target)).Select(p=>p.Key).ToArray())_original.Remove(stale);
        var owned=targets.ToDictionary(t=>t.Pid);var pids=targets.Select(t=>t.Pid).Concat(_original.Values.Select(o=>o.Target.Pid)).ToHashSet();
        foreach(var audio in _backend.Enumerate(pids))using(audio)
        {
            try
            {
                if(_original.TryGetValue(audio.Id,out var old)&&(!preferences.Enabled||!owned.TryGetValue(audio.Pid,out var target)||target.StartUtc!=old.Target.StartUtc))
                {Restore(audio,old,identity);_original.Remove(audio.Id);continue;}
                if(!preferences.Enabled||!owned.TryGetValue(audio.Pid,out var current)||!identity(current))continue;
                if(!_original.TryGetValue(audio.Id,out old))old=new(current,audio.Volume,audio.Muted,audio.Volume,audio.Muted);
                var volume=preferences.ForAccount(current.ProfileId)/100f;var mute=old.Muted||preferences.MuteBackground&&focusedPid!=current.Pid;
                if(Math.Abs(audio.Volume-volume)>.001)audio.Volume=volume;if(audio.Muted!=mute)audio.Muted=mute;
                _original[audio.Id]=old with{AppliedVolume=volume,AppliedMute=mute};
            }
            catch(COMException){ }
        }
    }
    private static bool IsSame(AudioTarget t){try{using var p=Process.GetProcessById(t.Pid);return !p.HasExited&&p.StartTime.ToUniversalTime()==t.StartUtc&&p.ProcessName.Equals("RobloxPlayerBeta",StringComparison.OrdinalIgnoreCase);}catch{return false;}}
    private static void Restore(IProcessAudioSession audio,Original old,Func<AudioTarget,bool> identity)
    {if(!identity(old.Target))return;if(Math.Abs(audio.Volume-old.AppliedVolume)<.001)audio.Volume=old.Volume;if(audio.Muted==old.AppliedMute)audio.Muted=old.Muted;}
    public void Dispose(){Reconcile([],new(),0);_original.Clear();}
}
internal sealed class WindowsAudioBackend:IProcessAudioBackend
{
    [ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]private class EnumeratorClass { }
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface Enumerator
    {[PreserveSig]int EnumAudioEndpoints(int flow,uint state,out Collection collection);}
    [ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface Collection
    {[PreserveSig]int GetCount(out uint count);[PreserveSig]int Item(uint index,out Device device);}
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface Device
    {[PreserveSig]int Activate(ref Guid iid,uint context,nint parameters,[MarshalAs(UnmanagedType.IUnknown)]out object instance);}
    [ComImport,Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface Manager
    {[PreserveSig]int GetAudioSessionControl(nint id,uint flags,out nint control);[PreserveSig]int GetSimpleAudioVolume(nint id,uint flags,out nint volume);[PreserveSig]int GetAudioSessionEnumerator(out Sessions sessions);}
    [ComImport,Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface Sessions
    {[PreserveSig]int GetCount(out int count);[PreserveSig]int GetSession(int index,out Control control);}
    [ComImport,Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface Control
    {
        [PreserveSig]int GetState(out int state);[PreserveSig]int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)]out string name);[PreserveSig]int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)]string name,ref Guid context);
        [PreserveSig]int GetIconPath([MarshalAs(UnmanagedType.LPWStr)]out string path);[PreserveSig]int SetIconPath([MarshalAs(UnmanagedType.LPWStr)]string path,ref Guid context);[PreserveSig]int GetGroupingParam(out Guid group);[PreserveSig]int SetGroupingParam(ref Guid group,ref Guid context);
        [PreserveSig]int RegisterAudioSessionNotification(nint events);[PreserveSig]int UnregisterAudioSessionNotification(nint events);
        [PreserveSig]int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)]out string id);[PreserveSig]int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)]out string id);[PreserveSig]int GetProcessId(out uint id);
    }
    [ComImport,Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface Volume
    {[PreserveSig]int SetMasterVolume(float volume,ref Guid context);[PreserveSig]int GetMasterVolume(out float volume);[PreserveSig]int SetMute([MarshalAs(UnmanagedType.Bool)]bool muted,ref Guid context);[PreserveSig]int GetMute([MarshalAs(UnmanagedType.Bool)]out bool muted);}
    private sealed class Handle(Control control,string id,int pid):IProcessAudioSession
    {
        public string Id=>id;public int Pid=>pid;
        public float Volume{get{Marshal.ThrowExceptionForHR(((Volume)control).GetMasterVolume(out var value));return value;}set{var context=Guid.Empty;Marshal.ThrowExceptionForHR(((Volume)control).SetMasterVolume(value,ref context));}}
        public bool Muted{get{Marshal.ThrowExceptionForHR(((Volume)control).GetMute(out var value));return value;}set{var context=Guid.Empty;Marshal.ThrowExceptionForHR(((Volume)control).SetMute(value,ref context));}}
        public void Dispose()=>Marshal.ReleaseComObject(control);
    }
    public IEnumerable<IProcessAudioSession> Enumerate(IReadOnlySet<int> pids)
    {
        var result=new List<IProcessAudioSession>();if(pids.Count==0)return result;
        var enumerator=(Enumerator)new EnumeratorClass();Collection? collection=null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0,1,out collection));Marshal.ThrowExceptionForHR(collection.GetCount(out var devices));
            for(uint i=0;i<devices;i++)
            {
                Device? device=null;Manager? manager=null;Sessions? sessions=null;
                try
                {
                    Marshal.ThrowExceptionForHR(collection.Item(i,out device));var iid=typeof(Manager).GUID;Marshal.ThrowExceptionForHR(device.Activate(ref iid,23,0,out var instance));manager=(Manager)instance;
                    Marshal.ThrowExceptionForHR(manager.GetAudioSessionEnumerator(out sessions));Marshal.ThrowExceptionForHR(sessions.GetCount(out var count));
                    for(int n=0;n<count;n++)
                    {
                        Control? control=null;
                        try{Marshal.ThrowExceptionForHR(sessions.GetSession(n,out control));Marshal.ThrowExceptionForHR(control.GetProcessId(out var pid));if(!pids.Contains((int)pid))continue;Marshal.ThrowExceptionForHR(control.GetSessionInstanceIdentifier(out var id));result.Add(new Handle(control,id,(int)pid));control=null;}
                        finally{if(control is not null)Marshal.ReleaseComObject(control);}
                    }
                }
                catch(COMException){ }
                finally{if(sessions is not null)Marshal.ReleaseComObject(sessions);if(manager is not null)Marshal.ReleaseComObject(manager);if(device is not null)Marshal.ReleaseComObject(device);}
            }
        }
        catch(COMException){ }
        finally{if(collection is not null)Marshal.ReleaseComObject(collection);Marshal.ReleaseComObject(enumerator);}
        return result;
    }
}
