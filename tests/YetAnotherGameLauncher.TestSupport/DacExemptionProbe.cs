namespace YetAnotherGameLauncher.TestSupport;

/// <summary>
/// DAC 豁免前提探针：在调用方给定的可写目录内落一个 mode-000 临时文件并尝试读——
/// root/CAP_DAC_OVERRIDE 进程读得动，chmod 000 类"拒读"夹具形态即不可构造；"拒删/拒写"
/// 形态对仅持 CAP_DAC_READ_SEARCH 的进程仍可构造（它不豁免写检），读探针无法区分——
/// 凡检出读豁免即建议 Skip，保守方向（宁多 Skip 不假绿）。勿用 Environment.UserName
/// 判断：CAP_DAC_OVERRIDE 的非 root 进程同样豁免，用户名判断有双向泄漏。
/// 注意探针必须用"chmod-000 后读"形态——写+删探针在 DAC 豁免进程上永远成功，
/// 探不出豁免（2026-09-26 review 第 11 轮 P2）。
/// </summary>
public static class DacExemptionProbe
{
    /// <summary>返回 true = 读探针被拒，拒读/拒删夹具形态可构造（继续测试）；
    /// false = 读探针被豁免（root/CAP_DAC_OVERRIDE、仅持 CAP_DAC_READ_SEARCH，或 Windows
    /// 无 Unix 权限位语义——与 <see cref="TryMakeDirectoryUnwritable"/> 同款例外）——
    /// 拒读形态不可构造，拒删/拒写形态的可构造性无法保证，调用方应 Skip（保守）。</summary>
    /// <param name="writableDir">探针临时文件的落盘目录（须存在且当前进程可写）。</param>
    public static bool CanConstructDeniedFixture(string writableDir)
    {
        if (OperatingSystem.IsWindows())
        {
            // 无 Unix 权限位：拒读/拒删形态不可构造（调用方测试本应先做平台门）
            return false;
        }

        var probePath = System.IO.Path.Combine(writableDir, $"dac-probe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(probePath, "x");
        File.SetUnixFileMode(probePath, UnixFileMode.None);
        try
        {
            _ = File.ReadAllText(probePath);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
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

    /// <summary>
    /// 把目录置为不可写（mode-000）以注入"删除/写入被拒"形态。置位前以 <see cref="CanConstructDeniedFixture"/>
    /// 自检：检出读豁免（root/CAP_DAC_OVERRIDE 或仅持 CAP_DAC_READ_SEARCH）时注入的前提
    /// 不保证成立，返回 false，调用方应 Skip（Windows 为 no-op 返回 true，见下）。
    /// Windows 无 Unix 权限位语义：no-op 返回 true（Windows 腿的占用/只读形态由调用方自建）。
    /// </summary>
    /// <param name="dir">目标目录（须已存在、当前进程对其可写——探针要在其中落临时文件；且为本进程属主（或持 CAP_FOWNER）——置位的 chmod 需要）。</param>
    /// <returns>true = 目录已置为不可写；false = 注入无效或无法证明有效，调用方应 Skip。</returns>
    public static bool TryMakeDirectoryUnwritable(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        if (!CanConstructDeniedFixture(dir))
        {
            return false;
        }

        File.SetUnixFileMode(dir, UnixFileMode.None);
        return true;
    }
}
