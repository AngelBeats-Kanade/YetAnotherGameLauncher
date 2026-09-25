namespace YetAnotherGameLauncher.TestSupport;

/// <summary>
/// DAC 豁免前提探针：在调用方给定的可写目录内落一个 mode-000 临时文件并尝试读——
/// root/CAP_DAC_OVERRIDE 进程读得动，chmod 000 类"拒读/拒删"夹具形态即不可构造，
/// 测试会退化平凡通过（假绿）或假红，必须显式 Skip。勿用 Environment.UserName 判断：
/// CAP_DAC_OVERRIDE 的非 root 进程同样豁免，用户名判断有双向泄漏。
/// </summary>
public static class DacExemptionProbe
{
    /// <summary>返回 true = 当前进程可无视权限位，拒读/拒删夹具形态不可构造，调用方应 Skip。</summary>
    /// <param name="writableDir">探针临时文件的落盘目录（须存在且当前进程可写）。</param>
    public static bool Exempt(string writableDir)
    {
        if (OperatingSystem.IsWindows())
        {
            // 无 Unix 权限位：拒读/拒删形态不可构造（调用方测试本应先做平台门）
            return true;
        }

        var probePath = System.IO.Path.Combine(writableDir, $"dac-probe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(probePath, "x");
        File.SetUnixFileMode(probePath, UnixFileMode.None);
        try
        {
            _ = File.ReadAllText(probePath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            // mode-000 文件的 unlink 只需父目录写权限，必可删
            try
            {
                File.Delete(probePath);
            }
            catch (IOException)
            {
            }
        }
    }
}
