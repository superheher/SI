using NUnit.Framework;
using SImulator.ViewModel.Model;

namespace SImulator.ViewModel.Tests;

public sealed class CommonTests
{
    private readonly TestPlatformManager _manager = new();

    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void SimpleRun()
    {
        var appSettings = new AppSettings();
        var main = new MainViewModel(appSettings)
        {
            PackageSource = new TestPackageSource()
        };

        main.Start.Execute(null);

        var game = main.Game;
        Assert.NotNull(game);

        game.Next.Execute(null);
        game.Next.Execute(null);
        game.Next.Execute(null);
        game.Next.Execute(null);
        game.Next.Execute(null);

        game.LocalInfo.SelectQuestion.Execute(game.LocalInfo.RoundInfo[0].Questions[0]);

        game.Next.Execute(null);

        Assert.AreEqual("� ���� �������� ������������� ������ ����� ��������� � ������������� ��������������",
            ((RemoteGameUI)game.UserInterface).TInfo.Text);
    }
}