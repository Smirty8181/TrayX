using TrayX.Modules.TrayManager;

namespace TrayX.IntegrationTests;

public sealed class TrayManagerMessagesIntegrationTests
{
    [Theory]
    [InlineData("ru", "Проводник перезапущен", "Настройки значков трея применены.")]
    [InlineData("en", "Explorer restarted", "Tray icon settings have been applied.")]
    public void Format_ReturnsTheSelectedLanguage(string language, string expectedTitle, string expectedMessage)
    {
        var result = TrayManagerMessages.Format(language, TrayManagerMessage.ExplorerRestarted);

        Assert.Equal(expectedTitle, result.Title);
        Assert.Equal(expectedMessage, result.Message);
    }

    [Theory]
    [InlineData("ru", "Восстановлено: 3. Осталось в журнале: 2.")]
    [InlineData("en", "Restored: 3. Remaining in recovery log: 2.")]
    public void Format_InsertsDynamicRecoveryCounts(string language, string expectedMessage)
    {
        var result = TrayManagerMessages.Format(language, TrayManagerMessage.RecoverySummary, 3, 2);

        Assert.Equal(expectedMessage, result.Message);
    }

    [Theory]
    [InlineData("ru", "Подробности записаны в журнал TrayX.")]
    [InlineData("en", "See the TrayX log for details.")]
    public void Format_ProvidesLocalizedErrorsWithoutLeakingExceptionText(
        string language,
        string expectedMessage)
    {
        var result = TrayManagerMessages.Format(language, TrayManagerMessage.AudioToggleFailed);

        Assert.Equal(expectedMessage, result.Message);
    }
}
