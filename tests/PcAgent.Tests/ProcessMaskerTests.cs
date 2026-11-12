using PcAgent.Privacy;

namespace PcAgent.Tests;

public class ProcessMaskerTests
{
    private static ProcessMasker Create(bool includePaths = true) =>
        new(["KakaoTalk*", "*torrent*", "steam.exe"], includePaths, salt: "test-salt");

    [Theory]
    [InlineData("kakaotalk*", "KakaoTalk", true)]
    [InlineData("*torrent*", "qBittorrent", true)]
    [InlineData("steam", "steamwebhelper", false)]
    [InlineData("steam*", "steamwebhelper", true)]
    [InlineData("a?c", "ABC", true)]
    [InlineData("*", "anything", true)]
    [InlineData("chrome", "chromium", false)]
    public void Glob_matching(string pattern, string text, bool expected) =>
        Assert.Equal(expected, ProcessMasker.GlobMatch(pattern, text));

    [Fact]
    public void Masked_process_hides_name_path_and_is_stable()
    {
        var masker = Create();
        var (name, path, masked) = masker.Apply("KakaoTalk", @"C:\Program Files (x86)\Kakao\KakaoTalk\KakaoTalk.exe");

        Assert.True(masked);
        Assert.Matches(@"^\[masked-[0-9a-f]{6}\]$", name);
        Assert.Null(path);
        Assert.Equal(name, masker.Apply("kakaotalk", null).Name);
        Assert.NotEqual(name, masker.Apply("qbittorrent", null).Name);
    }

    [Fact]
    public void Pattern_with_exe_suffix_matches_process_name()
    {
        Assert.True(Create().IsMasked("steam"));
        Assert.False(Create().IsMasked("steamwebhelper"));
    }

    [Fact]
    public void Different_salt_gives_different_token()
    {
        var a = new ProcessMasker(["x*"], true, "pc-a").MaskName("xapp");
        var b = new ProcessMasker(["x*"], true, "pc-b").MaskName("xapp");
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(@"C:\Users\hong.gildong\AppData\Local\App\app.exe", @"%USERPROFILE%\AppData\Local\App\app.exe")]
    [InlineData(@"d:\users\김철수\tools\x.exe", @"%USERPROFILE%\tools\x.exe")]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:\Program Files\App\app.exe")]
    public void User_profile_folder_is_always_masked(string input, string expected) =>
        Assert.Equal(expected, ProcessMasker.SanitizePath(input));

    [Fact]
    public void Paths_can_be_excluded_entirely()
    {
        var (_, path, masked) = Create(includePaths: false).Apply("notepad", @"C:\Windows\notepad.exe");
        Assert.False(masked);
        Assert.Null(path);
    }
}
