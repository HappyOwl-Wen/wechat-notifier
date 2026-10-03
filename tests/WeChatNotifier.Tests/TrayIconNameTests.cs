using WeChatNotifier.Listener.Profile;

namespace WeChatNotifier.Tests;

public class TrayIconNameTests
{
    [Theory]
    [InlineData(" 微信")]        // as the Windows 11 shell reports it (leading space)
    [InlineData("微信")]
    [InlineData("WeChat")]
    [InlineData("weixin")]
    public void WeChat_tray_icon_is_recognised(string name) => Assert.True(WeChatUiProfile.IsTrayIconName(name));

    [Theory]
    [InlineData("企业微信")]      // WeCom must never be clicked instead
    [InlineData("微信输入法")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_icons_are_not(string? name) => Assert.False(WeChatUiProfile.IsTrayIconName(name));
}
