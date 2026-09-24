using System.IO;
using Newtonsoft.Json;

namespace ValleytalkReborn.Services;

/// <summary>
/// 绝对路径 JSON IO 门面。SMAPI DataHelper 仅接受相对路径，
/// 凡落 <see cref="StorageLayout.LocalBaseDir"/> 的持久化一律经此门面，
/// 走 System.IO 原子写（.tmp + File.Move overwrite:true），失败删 .tmp 后原样上抛。
/// </summary>
internal static class StorageJson
{
    public static T? Read<T>(string absolutePath) where T : class
    {
        if (!File.Exists(absolutePath))
            return null;

        string json = File.ReadAllText(absolutePath);
        return JsonConvert.DeserializeObject<T>(json);
    }

    public static void Write<T>(string absolutePath, T model) where T : class
    {
        string? dir = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string tmp = absolutePath + ".tmp";
        string json = JsonConvert.SerializeObject(model, Formatting.Indented);
        File.WriteAllText(tmp, json);

        try
        {
            File.Move(tmp, absolutePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { }
            throw;
        }
    }
}
