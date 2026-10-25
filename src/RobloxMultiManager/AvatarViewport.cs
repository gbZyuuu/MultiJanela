using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;

namespace RobloxMultiManager;

/// <summary>Bounded OBJ thumbnail viewer. Frozen geometry; renders only when the camera or model changes.</summary>
public sealed class AvatarViewport : System.Windows.Controls.Grid
{
    private static readonly HttpClient Cdn = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim ModelGate = new(1);
    private static readonly Dictionary<string, Model3D> Models = [];
    private readonly System.Windows.Controls.Viewport3D _view = new();
    private readonly ModelVisual3D _model = new();
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 35, NearPlaneDistance = .05, FarPlaneDistance = 500 };
    private Point? _drag;
    private double _yaw = 180, _pitch = 8, _radius = 12;
    private Point3D _center;
    public AvatarViewport()
    {
        Background = Brushes.Transparent; ClipToBounds = true; MinHeight = 180;
        _view.Camera = _camera; Children.Add(_view);
        var light = new Model3DGroup(); light.Children.Add(new AmbientLight(System.Windows.Media.Color.FromRgb(170,170,170))); light.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1,-2,-3))); light.Freeze();
        _view.Children.Add(new ModelVisual3D { Content = light }); _view.Children.Add(_model);
        MouseLeftButtonDown += (_, e) => { _drag = e.GetPosition(this); CaptureMouse(); e.Handled = true; };
        MouseMove += (_, e) => { if (_drag is not { } old) return; var next=e.GetPosition(this); _yaw+=(next.X-old.X)*.5; _pitch=Math.Clamp(_pitch+(next.Y-old.Y)*.3,-75,75); _drag=next; Camera(); };
        MouseLeftButtonUp += (_, _) => { _drag=null; ReleaseMouseCapture(); };
        LostMouseCapture += (_, _) => _drag=null;
        MouseWheel += (_, e) => { _radius=Math.Clamp(_radius*(e.Delta>0?.9:1.1),1,100); Camera(); e.Handled=true; };
    }
    private void Camera()
    { var a=_yaw*Math.PI/180; var p=_pitch*Math.PI/180; var offset=new Vector3D(Math.Sin(a)*Math.Cos(p)*_radius,Math.Sin(p)*_radius,Math.Cos(a)*Math.Cos(p)*_radius); _camera.Position=_center+offset; _camera.LookDirection=-offset; _camera.UpDirection=new Vector3D(0,1,0); }
    public void Show(Model3D model)
    { _model.Content=model; var b=model.Bounds; _center=new Point3D(b.X+b.SizeX/2,b.Y+b.SizeY/2,b.Z+b.SizeZ/2); _radius=Math.Max(3,Math.Max(b.SizeY,Math.Max(b.SizeX,b.SizeZ))*2.3); Camera(); }
    public static async Task<Model3D> LoadAsync(string url, CancellationToken token)
    {
        var uri = SafeUri(url);
        await ModelGate.WaitAsync(token);
        try
        {
            if (Models.TryGetValue(url, out var cached)) return cached;
            using var json = JsonDocument.Parse(await Fetch(uri, 1024 * 1024, token)); var root = json.RootElement;
            var keys = root.GetProperty("textures").EnumerateArray().Select(t => t.GetString()!).Take(25).Distinct().ToArray();
            if (keys.Length > 24) throw new InvalidDataException();
            using var lanes = new SemaphoreSlim(4);
            using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
            async Task<byte[]> Download(Uri target, int limit)
            {
                await lanes.WaitAsync(work.Token);
                try { return await Fetch(target, limit, work.Token); }
                catch { work.Cancel(); throw; }
                finally { lanes.Release(); }
            }
            var objTask = Download(Resolve(uri, root.GetProperty("obj").GetString()!), 12 * 1024 * 1024);
            var mtlTask = Download(Resolve(uri, root.GetProperty("mtl").GetString()!), 256 * 1024);
            var total = 0;
            var imageTasks = keys.Select(async key =>
            {
                var bytes = await Download(Resolve(uri, key), 4 * 1024 * 1024);
                if (Interlocked.Add(ref total, bytes.Length) > 32 * 1024 * 1024) { work.Cancel(); throw new InvalidDataException(); }
                return (key, bytes);
            }).ToArray();
            await Task.WhenAll(imageTasks.Cast<Task>().Append(objTask).Append(mtlTask));
            var textures = imageTasks.ToDictionary(t => t.Result.key, t => t.Result.bytes, StringComparer.Ordinal);
            var model = await Task.Run(() => Build(Encoding.UTF8.GetString(objTask.Result), Encoding.UTF8.GetString(mtlTask.Result), textures, token), token);
            if (Models.Count >= 3) Models.Remove(Models.Keys.First());
            Models[url] = model; return model;
        }
        finally { ModelGate.Release(); }
    }
    private static Uri SafeUri(string url)
    { var uri=new Uri(url); if(uri.Scheme!="https"||!uri.Host.EndsWith(".rbxcdn.com",StringComparison.OrdinalIgnoreCase)||uri.Port!=443||!string.IsNullOrEmpty(uri.UserInfo))throw new InvalidDataException();return uri; }
    private static Uri Resolve(Uri origin,string value)
    { if(Uri.TryCreate(value,UriKind.Absolute,out var absolute))return SafeUri(absolute.AbsoluteUri); if(!Regex.IsMatch(value,"^[a-zA-Z0-9_-]{16,150}$"))throw new InvalidDataException();var shard=31; foreach(var c in value) shard ^= c; return SafeUri($"https://t{shard % 8}.rbxcdn.com/{value}"); }
    private static async Task<byte[]> Fetch(Uri uri,int limit,CancellationToken token)
    {
        using var response=await Cdn.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>limit)throw new InvalidDataException();
        await using var input=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();var buffer=new byte[16384];int count;
        while((count=await input.ReadAsync(buffer,token))>0){if(output.Length+count>limit)throw new InvalidDataException();output.Write(buffer,0,count);}return output.ToArray();
    }
    internal static Model3D Build(string obj,string mtl,IReadOnlyDictionary<string,byte[]> textures,CancellationToken token)
    {
        var materials=new Dictionary<string,Material>();string current="";
        foreach(var line in mtl.Split('\n'))
        {
            var parts=line.Trim().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);if(parts.Length<2)continue;
            if(parts[0]=="newmtl"){current=parts[1];materials[current]=new DiffuseMaterial(Brushes.LightGray);}
            else if(parts[0]=="map_Kd"&&textures.TryGetValue(parts[^1],out var bytes))
            { using var stream=new MemoryStream(bytes);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=1024;image.StreamSource=stream;image.EndInit();image.Freeze();var brush=new ImageBrush(image) { ViewportUnits=BrushMappingMode.Absolute, Viewport=new Rect(0,0,1,1) };brush.Freeze();materials[current]=new DiffuseMaterial(brush); }
            else if(parts[0]=="Kd"&&parts.Length>=4)materials[current]=new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromScRgb(1,(float)Number(parts[1]),(float)Number(parts[2]),(float)Number(parts[3]))));
        }
        var positions=new List<Point3D>();var uv=new List<Point>();var meshes=new Dictionary<string,MeshGeometry3D>();current="";int vertices=0;
        foreach(var line in obj.Split('\n'))
        {
            token.ThrowIfCancellationRequested();var parts=line.Trim().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);if(parts.Length<2)continue;
            if(parts[0]=="v"&&parts.Length>=4){if(positions.Count>=200000)throw new InvalidDataException();positions.Add(new Point3D(Number(parts[1]),Number(parts[2]),Number(parts[3])));}
            else if(parts[0]=="vt"&&parts.Length>=3){if(uv.Count>=300000)throw new InvalidDataException();uv.Add(new Point(Number(parts[1]),1-Number(parts[2])));}
            else if(parts[0]=="usemtl")current=parts[1];
            else if(parts[0]=="f"&&parts.Length>=4)
            {
                if(parts.Length>10||meshes.Count>64)throw new InvalidDataException();
                if(!meshes.TryGetValue(current,out var mesh))meshes[current]=mesh=new MeshGeometry3D();
                for(var n=2;n<parts.Length-1;n++)foreach(var reference in new[]{parts[1],parts[n],parts[n+1]})
                { if(++vertices>600000)throw new InvalidDataException();var indices=reference.Split('/');var p=Index(indices[0],positions.Count);mesh.TriangleIndices.Add(mesh.Positions.Count);mesh.Positions.Add(positions[p]);mesh.TextureCoordinates.Add(indices.Length>1&&indices[1].Length>0?uv[Index(indices[1],uv.Count)]:new Point()); }
            }
        }
        if(vertices==0)throw new InvalidDataException();var model=new Model3DGroup();
        foreach(var pair in meshes){pair.Value.Freeze();var material=materials.GetValueOrDefault(pair.Key)??new DiffuseMaterial(Brushes.LightGray);material.Freeze();model.Children.Add(new GeometryModel3D(pair.Value,material){BackMaterial=material});}model.Freeze();return model;
    }
    private static int Index(string s,int count){var i=int.Parse(s,CultureInfo.InvariantCulture);i=i<0?count+i:i-1;if(i<0||i>=count)throw new InvalidDataException();return i;}
    private static double Number(string s){var value=double.Parse(s,CultureInfo.InvariantCulture);if(!double.IsFinite(value)||Math.Abs(value)>100000)throw new InvalidDataException();return value;}
}
