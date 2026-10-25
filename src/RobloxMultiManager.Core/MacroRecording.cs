using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
namespace RobloxMultiManager.Core;
public sealed record MacroStep(string Action,int Key=0,double X=0,double Y=0,int Milliseconds=0);
public sealed record MacroRecording(Guid Id,string Name,MacroStep[] Steps)
{
    public override string ToString()=>Name;
    public static MacroRecording Validate(MacroRecording value)
    {
        if(value.Id==Guid.Empty||string.IsNullOrWhiteSpace(value.Name)||value.Name.Length>80||value.Steps.Length is <1 or >10000)throw new InvalidDataException();
        long duration=0;var keys=new HashSet<int>();
        foreach(var s in value.Steps)
        {
            if(s.Action is not ("wait" or "keyDown" or "keyUp" or "mouseDown" or "mouseUp" or "mouseMove" or "wheel")||s.Milliseconds is <0 or >60000||!double.IsFinite(s.X)||!double.IsFinite(s.Y)||s.X is <0 or >1||s.Y is <0 or >1)throw new InvalidDataException();
            if(s.Action is "keyDown" or "keyUp")
            {
                if(s.Key is <8 or >254 or 91 or 92||s.Action=="keyDown"&&keys.Contains(18)&&s.Key is 9 or 115)throw new InvalidDataException();
                if(s.Action=="keyDown")keys.Add(s.Key);else keys.Remove(s.Key);
                if(keys.Overlaps([18,164,165])&&keys.Overlaps([9,115])||keys.Overlaps([17,162,163])&&keys.Contains(27))throw new InvalidDataException();
            }
            if(s.Action is "mouseDown" or "mouseUp" && s.Key is <0 or >2)throw new InvalidDataException();
            if(s.Action=="wheel" && (s.Key==0 || s.Key is < -1200 or >1200))throw new InvalidDataException();
            duration+=s.Milliseconds;if(duration>600000)throw new InvalidDataException();
        }
        return value;
    }
}
public static class TinyTaskImporter
{
    public static MacroRecording Import(ReadOnlySpan<byte> file,Rectangle source,string name)
    {
        if(file.Length==0||file.Length>8*1024*1024||source.Width<=0||source.Height<=0)throw new InvalidDataException();
        var size=ValidShape(file,20)?20:ValidShape(file,24)?24:throw new InvalidDataException(L.T("Arquivo TinyTask inválido ou não reconhecido."));
        var steps=new List<MacroStep>();uint? previous=null;long gap=0,total=0;
        for(int at=0;at<file.Length;at+=size)
        {
            var row=file.Slice(at,size);var message=BinaryPrimitives.ReadUInt32LittleEndian(row);var low=BinaryPrimitives.ReadInt32LittleEndian(row[4..]);var high=BinaryPrimitives.ReadInt32LittleEndian(row[8..]);var time=BinaryPrimitives.ReadUInt32LittleEndian(row[12..]);
            if(previous is {} last){var delta=unchecked(time-last);total+=delta;gap+=delta;if(total>600000)throw new InvalidDataException();}previous=time;
            MacroStep? step=null;
            if(message is 0x100 or 0x101 or 0x104 or 0x105)step=new(message is 0x100 or 0x104?"keyDown":"keyUp",low&255);
            if(message is >=0x200 and <=0x209)
            {if(!source.Contains(low,high))throw new InvalidDataException(L.T("Um clique está fora da janela de referência."));var action=message==0x200?"mouseMove":message is 0x202 or 0x205 or 0x208?"mouseUp":"mouseDown";step=new(action,Key:message>=0x207?2:message>=0x204?1:0,X:(low-source.Left)/(double)Math.Max(1,source.Width-1),Y:(high-source.Top)/(double)Math.Max(1,source.Height-1));}
            if(step is null)throw new InvalidDataException(L.T("Este evento TinyTask não é compatível. Edite a gravação antes de importar."));
            while(gap>0){var delay=(int)Math.Min(60000,gap);steps.Add(new("wait",Milliseconds:delay));gap-=delay;}
            steps.Add(step);if(steps.Count>10000)throw new InvalidDataException();
        }
        return MacroRecording.Validate(new(Guid.NewGuid(),name,steps.ToArray()));
    }
    private static bool ValidShape(ReadOnlySpan<byte> data,int size)
    {if(data.Length%size!=0)return false;for(var at=0;at<data.Length;at+=size){var msg=BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);if(msg is not (0x100 or 0x101 or 0x104 or 0x105 or >=0x200 and <=0x20E))return false;}return true;}
}
public interface IMacroTarget
{
    bool Focus(); bool IsCurrent(); void Send(MacroStep step); void Release();
}
public static class MacroPlayback
{
    public static async Task PlayAsync(MacroRecording recording,IMacroTarget target,CancellationToken token)
    {
        MacroRecording.Validate(recording);
        if(!target.Focus())throw new InvalidOperationException(L.T("Não foi possível focar a janela da gravação."));
        try
        {
            foreach(var step in recording.Steps)
            {
                token.ThrowIfCancellationRequested();if(!target.IsCurrent())throw new InvalidOperationException(L.T("A reprodução parou porque o foco mudou."));
                if(step.Action!="wait")target.Send(step);
                var remaining=step.Milliseconds;
                while(remaining>0){var slice=Math.Min(remaining,20);await Task.Delay(slice,token);remaining-=slice;if(!target.IsCurrent())throw new InvalidOperationException(L.T("A reprodução parou porque o foco mudou."));}
            }
        }
        finally{target.Release();}
    }
}
public sealed class NativeMacroTarget(RobloxInstanceInfo session):IMacroTarget
{
    [StructLayout(LayoutKind.Sequential)]private struct Keyboard{public ushort Key,Scan;public uint Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct Mouse{public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Explicit)]private struct Union{[FieldOffset(0)]public Keyboard Keyboard;[FieldOffset(0)]public Mouse Mouse;}
    [StructLayout(LayoutKind.Sequential)]private struct Input{public uint Type;public Union Data;}
    [DllImport("user32.dll")]private static extern uint SendInput(uint count,Input[] input,int size);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("user32.dll")]private static extern bool GetClientRect(nint window,out RectangleNative rect);
    [DllImport("user32.dll")]private static extern bool ClientToScreen(nint window,ref PointNative point);
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential)]private struct RectangleNative{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct PointNative{public int X,Y;}
    private readonly HashSet<int> _held=[];private readonly HashSet<int> _mouse=[];
    private bool Identity()
    {
        try{using var p=Process.GetProcessById(session.ProcessId);GetWindowThreadProcessId(session.WindowHandle,out var pid);return pid==p.Id&&!p.HasExited&&p.StartTime.ToUniversalTime()==session.StartTimeUtc&&p.ProcessName.Equals("RobloxPlayerBeta",StringComparison.OrdinalIgnoreCase);}catch{return false;}
    }
    public bool Focus()=>Identity()&&SetForegroundWindow(session.WindowHandle)&&IsCurrent();
    public bool IsCurrent()=>Identity()&&GetForegroundWindow()==session.WindowHandle;
    public Rectangle Bounds()
    {if(!Identity()||!GetClientRect(session.WindowHandle,out var r))throw new InvalidOperationException();var p=new PointNative();if(!ClientToScreen(session.WindowHandle,ref p))throw new InvalidOperationException();return new(p.X,p.Y,r.Right-r.Left,r.Bottom-r.Top);}
    private static void Submit(Input input){if(SendInput(1,[input],Marshal.SizeOf<Input>())!=1)throw new InvalidOperationException(L.T("O Windows recusou a entrada da gravação."));}
    public void Send(MacroStep step)
    {
        if(!IsCurrent())throw new InvalidOperationException();
        if(step.Action is "keyDown" or "keyUp")
        {Submit(new(){Type=1,Data=new(){Keyboard=new(){Key=(ushort)step.Key,Flags=step.Action=="keyUp"?2u:0u}}});if(step.Action=="keyDown")_held.Add(step.Key);else _held.Remove(step.Key);}
        else if(step.Action is "mouseDown" or "mouseUp" or "mouseMove" or "wheel")
        {
            var r=Bounds();var x=r.Left+(int)Math.Round(step.X*(r.Width-1));var y=r.Top+(int)Math.Round(step.Y*(r.Height-1));
            Submit(new(){Type=0,Data=new(){Mouse=new(){X=(int)((x-GetSystemMetrics(76))*65535d/Math.Max(1,GetSystemMetrics(78)-1)),Y=(int)((y-GetSystemMetrics(77))*65535d/Math.Max(1,GetSystemMetrics(79)-1)),Flags=0xC001}}});
            if(step.Action=="mouseMove")return;
            var flags=step.Action=="wheel"?0x800u:ButtonFlag(step.Key,step.Action=="mouseUp");
            Submit(new(){Type=0,Data=new(){Mouse=new(){Flags=flags,Data=step.Action=="wheel"?unchecked((uint)step.Key):0}}});
            if(step.Action=="mouseDown")_mouse.Add(step.Key);else if(step.Action=="mouseUp")_mouse.Remove(step.Key);
        }
    }
    private static uint ButtonFlag(int key,bool up)=>key switch{0=>up?4u:2u,1=>up?16u:8u,2=>up?64u:32u,_=>throw new ArgumentException()};
    public void Release()
    {
        foreach(var key in _held.ToArray()){try{Submit(new(){Type=1,Data=new(){Keyboard=new(){Key=(ushort)key,Flags=2}}});}catch{}}_held.Clear();
        foreach(var button in _mouse.ToArray()){try{Submit(new(){Type=0,Data=new(){Mouse=new(){Flags=ButtonFlag(button,true)}}});}catch{}}_mouse.Clear();
    }
}
