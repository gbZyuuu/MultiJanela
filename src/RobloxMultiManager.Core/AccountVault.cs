using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace RobloxMultiManager.Core;
public sealed record VaultConfiguration(int Version,string Salt,string Nonce,string Tag,string Key,int IdleMinutes=15,bool Migrating=false);
/// <summary>Password-wrapped encryption key for the manager's JSON data. Native browser storage remains Windows-protected.</summary>
public sealed class AccountVault(string root)
{
    private string Config=>Path.Combine(root,"vault.json");
    public bool Exists=>File.Exists(Config);
    public async Task<VaultConfiguration?> ReadAsync()
    {
        if(!Exists)return null;if(new FileInfo(Config).Length>8192)throw new InvalidDataException();
        var c=JsonSerializer.Deserialize<VaultConfiguration>(await File.ReadAllTextAsync(Config))??throw new InvalidDataException();
        if(c.Version!=1||c.IdleMinutes is <1 or >240||Convert.FromBase64String(c.Salt).Length!=16||Convert.FromBase64String(c.Nonce).Length!=12||Convert.FromBase64String(c.Tag).Length!=16||Convert.FromBase64String(c.Key).Length!=32)throw new InvalidDataException();return c;
    }
    public async Task CreateAsync(string password,int idleMinutes,CancellationToken token=default)
    {
        if(Exists)throw new InvalidOperationException();ValidatePassword(password);if(idleMinutes is <1 or >240)throw new ArgumentException();
        var key=RandomNumberGenerator.GetBytes(32);
        try{var config=Wrap(password,key,idleMinutes,true);await WriteConfigAsync(config,token);SecureJsonFiles.Unlock(root,key);await MigrateAsync(token);await WriteConfigAsync(Wrap(password,key,idleMinutes,false),token);}
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    public async Task UnlockAsync(string password,CancellationToken token=default)
    {
        var c=await ReadAsync()??throw new InvalidOperationException();var derived=Derive(password,Convert.FromBase64String(c.Salt));var key=new byte[32];
        try{using var aes=new AesGcm(derived,16);aes.Decrypt(Convert.FromBase64String(c.Nonce),Convert.FromBase64String(c.Key),Convert.FromBase64String(c.Tag),key,Encoding.UTF8.GetBytes($"1:{c.IdleMinutes}:{c.Migrating}"));SecureJsonFiles.Unlock(root,key);if(c.Migrating){await MigrateAsync(token);await WriteConfigAsync(Wrap(password,key,c.IdleMinutes,false),token);}}
        finally{CryptographicOperations.ZeroMemory(derived);CryptographicOperations.ZeroMemory(key);}
    }
    public async Task ChangePasswordAsync(string password,int idleMinutes,CancellationToken token=default)
    {ValidatePassword(password);if(idleMinutes is <1 or >240)throw new ArgumentException();var key=SecureJsonFiles.CopyKey(root);try{await WriteConfigAsync(Wrap(password,key,idleMinutes),token);}finally{CryptographicOperations.ZeroMemory(key);}}
    private async Task MigrateAsync(CancellationToken token)
    {
        foreach(var file in Directory.EnumerateFiles(root).Where(SecureJsonFiles.Managed).ToArray())
        {
            token.ThrowIfCancellationRequested();if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();var bytes=await File.ReadAllBytesAsync(file,token);
            if(SecureJsonFiles.Encrypted(bytes))continue;var temporary=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{await SecureJsonFiles.WriteAllBytesAsync(temporary,bytes,token);File.Move(temporary,file,true);}finally{CryptographicOperations.ZeroMemory(bytes);if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
    private async Task WriteConfigAsync(VaultConfiguration c,CancellationToken token)
    {Directory.CreateDirectory(root);ManagerUpdateService.RejectRedirects(root);var tmp=Config+"."+Guid.NewGuid().ToString("N")+".tmp";try{await File.WriteAllTextAsync(tmp,JsonSerializer.Serialize(c),token);File.Move(tmp,Config,true);}finally{if(File.Exists(tmp))File.Delete(tmp);}}
    private static void ValidatePassword(string value){if(value.Length is <12 or >256)throw new ArgumentException(L.T("Use uma senha de 12 a 256 caracteres."));}
    private static byte[] Derive(string password,byte[] salt)=>Rfc2898DeriveBytes.Pbkdf2(password,salt,600000,HashAlgorithmName.SHA256,32);
    private static VaultConfiguration Wrap(string password,byte[] key,int idle,bool migrating=false)
    {
        var salt=RandomNumberGenerator.GetBytes(16);var nonce=RandomNumberGenerator.GetBytes(12);var tag=new byte[16];var cipher=new byte[32];var derived=Derive(password,salt);
        try{using var aes=new AesGcm(derived,16);aes.Encrypt(nonce,key,cipher,tag,Encoding.UTF8.GetBytes($"1:{idle}:{migrating}"));return new(1,Convert.ToBase64String(salt),Convert.ToBase64String(nonce),Convert.ToBase64String(tag),Convert.ToBase64String(cipher),idle,migrating);}
        finally{CryptographicOperations.ZeroMemory(derived);}
    }
}
public static class SecureJsonFiles
{
    private static readonly byte[] Magic=Encoding.ASCII.GetBytes("MJDATA01");
    private static readonly object Gate=new();private static byte[]? _key;private static string? _root;
    public static bool IsUnlocked{get{lock(Gate)return _key is not null;}}
    public static void Unlock(string root,byte[] key){lock(Gate){if(_key is not null)CryptographicOperations.ZeroMemory(_key);_root=Path.GetFullPath(root);_key=key.ToArray();}}
    public static void Lock(){lock(Gate){if(_key is not null)CryptographicOperations.ZeroMemory(_key);_key=null;}}
    internal static byte[] CopyKey(string root){lock(Gate){if(_key is null||!string.Equals(_root,Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException(L.T("O cofre está bloqueado."));return _key.ToArray();}}
    public static bool Managed(string path)
    {
        lock(Gate){var name=Path.GetFileName(path);return _root is not null&&string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),_root,StringComparison.OrdinalIgnoreCase)&&name.Contains(".json",StringComparison.OrdinalIgnoreCase)&&!name.StartsWith("vault.",StringComparison.OrdinalIgnoreCase)&&!name.StartsWith("ui-preferences.",StringComparison.OrdinalIgnoreCase);}
    }
    public static bool Encrypted(ReadOnlySpan<byte> bytes)=>bytes.StartsWith(Magic);
    private static byte[] Purpose(string path)
    {var name=Path.GetFileName(path).ToLowerInvariant();var end=name.IndexOf(".json",StringComparison.Ordinal);return Encoding.UTF8.GetBytes(end<0?name:name[..(end+5)]);}
    private static byte[] Encode(string path,byte[] plain)
    {
        if(!Managed(path))return plain;var key=CopyKey(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try{var output=new byte[Magic.Length+28+plain.Length];Magic.CopyTo(output,0);RandomNumberGenerator.Fill(output.AsSpan(8,12));using var aes=new AesGcm(key,16);aes.Encrypt(output.AsSpan(8,12),plain,output.AsSpan(36),output.AsSpan(20,16),Purpose(path));return output;}
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    private static byte[] Decode(string path,byte[] cipher)
    {
        if(Managed(path)){var check=CopyKey(Path.GetDirectoryName(Path.GetFullPath(path))!);CryptographicOperations.ZeroMemory(check);}if(!Encrypted(cipher)){if(Managed(path))throw new CryptographicException();return cipher;}if(cipher.Length<36)throw new InvalidDataException();var key=CopyKey(Path.GetDirectoryName(Path.GetFullPath(path))!);var plain=new byte[cipher.Length-36];
        try{using var aes=new AesGcm(key,16);aes.Decrypt(cipher.AsSpan(8,12),cipher.AsSpan(36),cipher.AsSpan(20,16),plain,Purpose(path));return plain;}finally{CryptographicOperations.ZeroMemory(key);}
    }
    public static Stream OpenRead(string path)
    {
        var input=File.OpenRead(path);Span<byte> prefix=stackalloc byte[8];var count=input.Read(prefix);input.Position=0;
        if(count!=8||!Encrypted(prefix)){if(Managed(path)){input.Dispose();var check=CopyKey(Path.GetDirectoryName(Path.GetFullPath(path))!);CryptographicOperations.ZeroMemory(check);throw new CryptographicException();}return input;}
        try{using var buffer=new MemoryStream();input.CopyTo(buffer);return new MemoryStream(Decode(path,buffer.ToArray()),false);}finally{input.Dispose();}
    }
    public static async Task<byte[]> ReadAllBytesAsync(string path,CancellationToken token=default)=>Decode(path,await File.ReadAllBytesAsync(path,token));
    public static async Task<string> ReadAllTextAsync(string path,CancellationToken token=default)=>Encoding.UTF8.GetString(await ReadAllBytesAsync(path,token)).TrimStart('﻿');
    public static async Task WriteAllBytesAsync(string path,byte[] bytes,CancellationToken token=default)
    {var encoded=Encode(path,bytes);try{await File.WriteAllBytesAsync(path,encoded,token);}finally{if(!ReferenceEquals(encoded,bytes))CryptographicOperations.ZeroMemory(encoded);}}
    public static Task WriteAllTextAsync(string path,string text,Encoding encoding,CancellationToken token=default)=>WriteAllBytesAsync(path,encoding.GetBytes(text),token);
    public static Task WriteAllTextAsync(string path,string text,CancellationToken token=default)=>WriteAllBytesAsync(path,Encoding.UTF8.GetBytes(text),token);
}
public sealed record AccountImportEntry(string Alias,long? UserId,string? Username,string? SessionCookie)
{public override string ToString()=>Alias;}
public static class AccountImportPreview
{
    public static AccountImportEntry[] Parse(string json)
    {
        if(json.Length>8*1024*1024)throw new InvalidDataException();using var data=JsonDocument.Parse(json);var root=data.RootElement;
        if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("Accounts",out var accounts))root=accounts;
        if(root.ValueKind!=JsonValueKind.Array||root.GetArrayLength()>2000)throw new InvalidDataException();var result=new List<AccountImportEntry>();
        foreach(var row in root.EnumerateArray())
        {
            string? Text(params string[] names){foreach(var name in names)if(row.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String)return v.GetString();return null;}
            var user=Text("Username","RobloxUsername");var alias=Text("Alias","Name")??user;
            if(string.IsNullOrWhiteSpace(alias)||alias.Length>240)throw new InvalidDataException();
            long? id=null;foreach(var name in new[]{"UserID","RobloxUserId"})if(row.TryGetProperty(name,out var v)&&v.TryGetInt64(out var n)&&n>0)id=n;
            var cookie=Text("SecurityToken","SessionCookie");if(cookie?.Length>16384||cookie?.IndexOfAny(['\r','\n',';'])>=0)throw new InvalidDataException();
            result.Add(new(alias,id,user,cookie));
        }
        return result.ToArray();
    }
}
