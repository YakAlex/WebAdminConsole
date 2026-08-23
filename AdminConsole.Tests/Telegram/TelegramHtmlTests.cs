using AdminConsole.Infrastructure.Telegram;

namespace AdminConsole.Tests.Telegram;

public sealed class TelegramHtmlTests
{
    [Fact]
    public void Escape_PlainText_ReturnsUnchanged()
    {
        Assert.Equal("Server1", TelegramHtml.Escape("Server1"));
    }

    [Fact]
    public void Escape_LessThanAndGreaterThan_AreEscaped()
    {
        Assert.Equal("&lt;script&gt;", TelegramHtml.Escape("<script>"));
    }

    [Fact]
    public void Escape_Ampersand_IsEscapedFirst_SoItDoesNotDoubleEscapeOtherEntities()
    {
        // If '&' were escaped AFTER '<'/'>', "<" -> "&lt;" would then have its
        // own '&' re-escaped into "&amp;lt;" — wrong. Ampersand must go first.
        Assert.Equal("&lt;", TelegramHtml.Escape("<"));
        Assert.Equal("&amp;lt;stray", TelegramHtml.Escape("&lt;stray"));
    }

    [Fact]
    public void Escape_NullOrEmpty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, TelegramHtml.Escape(null));
        Assert.Equal(string.Empty, TelegramHtml.Escape(string.Empty));
    }

    [Fact]
    public void Escape_MaliciousTelegramUsername_CannotBreakOutOfTheTag()
    {
        // A hostile Telegram username is fully attacker-controlled — this is
        // the concrete injection scenario this helper exists to close.
        string username = "</b><script>alert(1)</script><b>";
        string result   = TelegramHtml.Escape(username);

        Assert.DoesNotContain("<script>", result);
        Assert.DoesNotContain("</b>", result);
    }
}
