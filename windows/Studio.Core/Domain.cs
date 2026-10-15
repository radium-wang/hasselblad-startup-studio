using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StartupStudio;

public sealed class StudioException(string message) : Exception(message);
public static class Check
{
    public static void That(bool condition, string message) { if (!condition) throw new StudioException(message); }
    public static bool Match(string value, string pattern) => Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant);
    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    public static bool IsHash(string value) => Match(value, "[0-9a-f]{64}");
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    public static uint Fnv(byte[] bytes) { uint n = 2166136261; foreach (var b in bytes) n = unchecked((n ^ b) * 16777619); return n; }
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static T Decode<T>(byte[] data) => JsonSerializer.Deserialize<T>(data, Json) ?? throw new StudioException("恢复记录为空。");
    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    public static void Atomic(string path, byte[] bytes, bool replace = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".next";
        try { using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(bytes); f.Flush(true); } File.Move(temp, path, replace); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed record StartupImage(int Width, int Height, byte[] Pixels, int Channels = 1)
{
    public void Validate() => Check.That(Width is >= 1 and <= 1024 && Height is >= 1 and <= 768 && Channels is 1 or 4 && Pixels.Length == Width * Height * Channels, "图片宽度不得超过 1024、高度不得超过 768 像素。");
    public byte[] Envelope()
    {
        Validate(); var data = new byte[32 + Pixels.Length]; Encoding.ASCII.GetBytes("X2DLOGO3").CopyTo(data, 0);
        uint[] fields = [(uint)(Channels == 4 ? 2 : 1), (uint)Width, (uint)Height, (uint)Pixels.Length, Check.Fnv(Pixels), 0];
        for (var i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8 + 4 * i), fields[i]);
        Pixels.CopyTo(data, 32); return data;
    }
    public static StartupImage Decode(byte[] data)
    {
        Check.That(data.Length >= 32 && data.AsSpan(0, 8).SequenceEqual("X2DLOGO3"u8), "图片资源格式错误。");
        uint Word(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        Check.That(Word(8) is 1 or 2 && Word(28) == 0 && Word(12) is >= 1 and <= 1024 && Word(16) is >= 1 and <= 768 && Word(20) == Word(12) * Word(16) * (Word(8) == 2 ? 4 : 1) && data.Length == 32 + Word(20), "图片资源尺寸或长度不符。");
        var pixels = data[32..]; Check.That(Check.Fnv(pixels) == Word(24), "图片资源校验失败。");
        return new((int)Word(12), (int)Word(16), pixels, Word(8) == 2 ? 4 : 1);
    }
    public StartupImage Grayscale()
    {
        Validate(); if (Channels == 1) return this;
        var gray = new byte[Width * Height];
        for (var i = 0; i < gray.Length; i++) gray[i] = (byte)((299 * Pixels[i * 4 + 2] + 587 * Pixels[i * 4 + 1] + 114 * Pixels[i * 4] + 500) / 1000);
        return new(Width, Height, gray);
    }
    public StartupImage Crop(int x, int y, int width, int height)
    {
        Validate(); x = Math.Clamp(x, 0, Width - 1); y = Math.Clamp(y, 0, Height - 1);
        width = Math.Clamp(width, 1, Width - x); height = Math.Clamp(height, 1, Height - y);
        var bytes = new byte[width * height * Channels];
        for (var row = 0; row < height; row++) Array.Copy(Pixels, ((y + row) * Width + x) * Channels, bytes, row * width * Channels, width * Channels);
        return new(width, height, bytes, Channels);
    }
}
public sealed record PatchPart(int Offset, int Size);
public sealed record Adapter(int Mode, string OriginalSHA256, string AdapterSHA256, int OriginalSize, PatchPart[] Patches)
{
    public const string StockHash = "a0ac02a51d87d08fa61fd0d1e79af15db248af4b5b32603b9483d09f9b7f6217";
    public void Validate(byte[] patch)
    {
        Check.That(Mode == 3 && OriginalSHA256 == StockHash && Check.IsHash(AdapterSHA256), "适配层清单不匹配。");
        Check.That(OriginalSize == 2051168 && Patches.Length == 4 && Patches.Select(p => p.Offset).SequenceEqual([152, 513444, 513748, 1939088]) && Patches.Take(3).Select(p => p.Size).SequenceEqual([16, 4, 4]) && Patches[^1].Size is > 0 and <= 10608 && Patches.All(p => p.Size > 0 && (long)p.Offset + p.Size <= OriginalSize) && Patches.Sum(p => (long)p.Size) == patch.Length, "适配层写入范围或长度错误。");
    }
    public byte[] Apply(byte[] stock, byte[] patch)
    {
        Validate(patch); Check.That(Check.Hash(stock) == OriginalSHA256, "原厂库不匹配。");
        var result = stock.ToArray(); var cursor = 0;
        foreach (var p in Patches) { Array.Copy(patch, cursor, result, p.Offset, p.Size); cursor += p.Size; }
        Check.That(Check.Hash(result) == AdapterSHA256, "适配层完整校验失败。"); return result;
    }
}
public static class CameraPaths
{
    public const string Library = "/system/lib64/weston/eagle-backend.so";
    public const string Backup = Library + ".logo3-original", Stage = Library + ".logo3-next";
    public const string Mode = "/system/lib64/weston/x2d-startup-mode3", Image = "/system/lib64/weston/x2d-startup-gray3.bin";
    public const string ImageStage = Image + ".next", Journal = "/system/lib64/weston/x2d-startup-owner3.json", JournalStage = Journal + ".next";
    public static readonly string[] Owned = [Backup, Stage, Mode, Image, ImageStage, Journal, JournalStage];
}
public static class Models
{
    public const string X2D = "X2D 100C", Cfv = "CFV 100C";
    public static string Detect(string platform, string pid, string product, ushort? transportPid)
    {
        Check.That(platform == "eagle2_ec1706_native", "当前相机平台不受支持。");
        var model = (pid.ToLowerInvariant(), product) switch { ("0x0009", X2D) => X2D, ("0x000a", Cfv) => Cfv, _ => throw new StudioException("USB 产品标识与机型名称不匹配，已停止操作。") };
        Check.That(transportPid == null || transportPid == (model == X2D ? 9 : 10), "USB 设备与机型标识不一致。"); return model;
    }
    public static void VerifyVariant(string model, int variant) => Check.That(model == X2D ? variant is 0 or 2 : model == Cfv && variant == 0, "当前机型的原厂版本不受支持。");
    public static void VerifyKey(string response, int variant)
    {
        var lines = response.Split('\n'); Check.That(lines.Length >= 2 && lines[0].StartsWith("KEY="), "原厂显示键响应无效。");
        byte[] raw; try { raw = Convert.FromBase64String(string.Concat(lines[..^1])[4..]); } catch (FormatException) { throw new StudioException("原厂显示键读取不完整。"); }
        var text = Encoding.UTF8.GetString(raw).Trim().Trim('\0');
        if (lines[^1] == "RC=255" && variant == 0 && (raw.Length == 0 || text == "unrd_get_length failed")) return;
        Check.That(lines[^1] == "RC=0" && (text == variant.ToString(System.Globalization.CultureInfo.InvariantCulture) || raw.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == variant), "原厂版本与开机图选择不一致，已停止操作。");
    }
}
public sealed record CameraSnapshot(string FactoryHash, int Variant, string DisplayKey, string Metadata, string LibraryHash, string? Model = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public string CameraModel => Model ?? Models.X2D;
    [System.Text.Json.Serialization.JsonIgnore] public string Edition => CameraModel == Models.Cfv ? "标准版" : Variant == 2 ? "大地版" : "普通版";
    public bool SameBaseline(CameraSnapshot other) => CameraModel == other.CameraModel && FactoryHash == other.FactoryHash && Variant == other.Variant && DisplayKey == other.DisplayKey && Metadata == other.Metadata && LibraryHash == other.LibraryHash;
}
public sealed record RecoveryRecord(int Schema, CameraSnapshot Baseline, string AdapterHash, string[] Roots)
{
    public void Validate(CameraSnapshot current, Adapter adapter)
    {
        Check.That(Schema == 1 && AdapterHash == adapter.AdapterSHA256 && Baseline.LibraryHash == adapter.OriginalSHA256, "恢复包版本或原厂库不匹配。");
        Check.That(Check.IsHash(Baseline.FactoryHash) && Baseline.FactoryHash == current.FactoryHash && Baseline.CameraModel == current.CameraModel, "这份恢复包不属于当前相机，禁止跨设备恢复。");
        Models.VerifyVariant(Baseline.CameraModel, Baseline.Variant); Models.VerifyKey(Baseline.DisplayKey, Baseline.Variant);
        Check.That(Baseline.Variant == current.Variant && Baseline.DisplayKey == current.DisplayKey, "原厂版本信息已变化，已停止操作。");
        Check.That(current.LibraryHash == adapter.OriginalSHA256 || current.LibraryHash == adapter.AdapterSHA256, "当前绘图库不是已知原厂库或本 App 适配层。");
        Check.That(Check.Match(Baseline.Metadata, "[0-7]{3,4} [0-9]+ [0-9]+ 2051168 [0-9]+\\nu:object_r:[A-Za-z0-9_]+:s0"), "原厂文件属性无效。");
        Check.That(Roots.Length is > 0 and <= 32 && Roots.All(p => Check.Match(p, "(/tmp/x2d-l3-|/blackbox/\\.x2d-l3-)[0-9a-f]{8}")), "恢复包路径清单无效。");
    }
}
public interface ICameraLink
{
    ushort? ProductId { get; }
    string Command(string text, uint timeout = 12000);
}
public interface IFileCameraLink : ICameraLink
{
    byte[] ReadFile(string path, int size);
    void WriteFile(byte[] bytes, string path);
}
public interface IManagedCameraLink : IFileCameraLink, IDisposable { void Finish(); }
public interface IUsbCameraLink : ICameraLink, IDisposable { }
