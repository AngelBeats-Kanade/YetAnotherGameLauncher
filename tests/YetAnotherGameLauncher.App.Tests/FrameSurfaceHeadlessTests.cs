using Avalonia.Controls;
using Avalonia.Media;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Controls;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// FrameSurface 的播放器帧通知订阅平衡回归：生产时序是 DataTemplate 实例化时先经绑定赋
/// Player（挂树前）再挂树——订阅必须恰一次；脱树后必须完全退订（播放器是 transient 且可
/// 暂停保活，残留在 FrameUpdated 上的订阅会把已脱树的渲染面滞留在内存里）。
/// </summary>
[Collection("sequential")]
public class FrameSurfaceHeadlessTests
{
    [Fact]
    public async Task PlayerSetBeforeAttach_SubscribesExactlyOnce_AndUnsubscribesFullyOnDetach()
    {
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var player = new SubscriptionCountingPlayer();
            // 未挂树即赋 Player（对应模板 DataContext 先于视觉树挂接的绑定推值）
            var surface = new FrameSurface { Player = player };

            var window = new Window { Content = surface };
            window.Show();
            window.UpdateLayout();

            // 挂树后恰一次订阅（现状：属性变更订阅 + OnAttached 订阅 = 2）
            Assert.Equal(1, player.SubscriberCount);

            window.Close();

            // 脱树后零残留（现状：双重订阅只退订其一，计数滞留 1）
            Assert.Equal(0, player.SubscriberCount);
        }, CancellationToken.None);
    }

    /// <summary>帧通知订阅计数可观测的最小播放器替身。</summary>
    private sealed class SubscriptionCountingPlayer : IVideoBackdropPlayer
    {
        public int SubscriberCount { get; private set; }

        public event EventHandler? FrameUpdated
        {
            add => SubscriberCount++;
            remove => SubscriberCount--;
        }

        public IImage? Frame => null;

        public IImage? FadeFrame => null;

        public double FadeOpacity => 0;

        public bool IsSessionActive => false;

        public Task<bool> PlayAsync(string videoPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public void Stop()
        {
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }
    }
}
