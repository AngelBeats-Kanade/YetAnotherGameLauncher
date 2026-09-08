using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Models;

public class GameCatalogValidationTests
{
    private static GameDefinition ValidGame(string id = "test-game") => new()
    {
        Id = id,
        DisplayName = "测试游戏",
        Channel = "kuro",
        InstallDir = "TestGame",
        Executable = "game.exe",
        Servers = [new GameServer { Id = "s1", Name = "服务器1" }],
    };

    private static List<string> Validate(Action<GameCatalog> configure)
    {
        var catalog = new GameCatalog
        {
            Settings = new AppSettings { InstallRoot = "~/Games" },
            Games = [ValidGame()],
        };
        configure(catalog);
        return [.. GameCatalogService.Validate(catalog)];
    }

    [Fact]
    public void Validate_ValidCatalog_ReturnsNoErrors()
    {
        var errors = Validate(_ => { });

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_MissingGameId_ReportsError()
    {
        var errors = Validate(c => c.Games[0].Id = "");

        Assert.Contains(errors, e => e.Contains("id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_WhitespaceGameId_ReportsError()
    {
        var errors = Validate(c => c.Games[0].Id = "  ");

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_GameIdWithPathSeparator_ReportsError()
    {
        var errors = Validate(c => c.Games[0].Id = "a/b");

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_DuplicateGameIds_ReportsError()
    {
        var errors = Validate(c => c.Games.Add(ValidGame("test-game")));

        Assert.Contains(errors, e => e.Contains("duplicated", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DuplicateGameIdsCaseInsensitive_ReportsError()
    {
        var errors = Validate(c => c.Games.Add(ValidGame("Test-Game")));

        Assert.Contains(errors, e => e.Contains("duplicated", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MissingDisplayName_ReportsError()
    {
        var errors = Validate(c => c.Games[0].DisplayName = "");

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_MissingChannel_ReportsError()
    {
        var errors = Validate(c => c.Games[0].Channel = "");

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_MissingInstallDir_ReportsError()
    {
        var errors = Validate(c => c.Games[0].InstallDir = "");

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_MissingExecutable_ReportsError()
    {
        var errors = Validate(c => c.Games[0].Executable = "");

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_NoServers_ReportsError()
    {
        var errors = Validate(c => c.Games[0].Servers.Clear());

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_DuplicateServerIds_ReportsError()
    {
        var errors = Validate(c => c.Games[0].Servers.Add(new GameServer { Id = "s1", Name = "服务器2" }));

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_MissingServerId_ReportsError()
    {
        var errors = Validate(c => c.Games[0].Servers.Add(new GameServer { Id = "", Name = "服务器2" }));

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_MissingServerName_ReportsError()
    {
        var errors = Validate(c => c.Games[0].Servers.Add(new GameServer { Id = "s2", Name = "" }));

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_EmptyCommandTemplate_ReportsError()
    {
        var errors = Validate(c => c.Games[0].Launch.CommandTemplate = "");

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_InstallRootMissing_ReportsError()
    {
        var errors = Validate(c => c.Settings.InstallRoot = "");

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_AccumulatesErrorsFromMultipleGames()
    {
        var errors = Validate(c =>
        {
            c.Games[0].Id = "";
            c.Games.Add(ValidGame("second"));
            c.Games[1].Channel = "";
        });

        Assert.True(errors.Count >= 2);
    }
}
