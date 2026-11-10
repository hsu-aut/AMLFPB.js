// Which selection of the diagram goes on to the AML tree (016f E3, V26): not
// the one the tree itself just caused, but every other one.

using Aml.Editor.Plugin.FPB.Bridge;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class SelectionEchoTests
{
    [Fact]
    public void ASelectionOfTheDiagramGoesToTheTreeOnce()
    {
        var echo = new SelectionEcho();
        Assert.True(echo.ToTree("x"));
        Assert.False(echo.ToTree("x"));
        Assert.True(echo.ToTree("y"));
    }

    [Fact]
    public void TheAnswerOfThePageToASelectionOfTheTreeStaysThere()
    {
        var echo = new SelectionEcho();
        echo.FromTree("x");
        Assert.False(echo.ToTree("x"));
    }

    [Fact]
    public void AfterTheTreeSelectedSomethingElseTheDiagramGoesThereAgain()
    {
        // click X in the diagram, a library class in the tree, X again (final review)
        var echo = new SelectionEcho();
        Assert.True(echo.ToTree("x"));
        echo.FromTree(null);
        Assert.True(echo.ToTree("x"));
    }

    [Fact]
    public void IdsCompareWithoutCase()
    {
        var echo = new SelectionEcho();
        echo.FromTree("ABC");
        Assert.False(echo.ToTree("abc"));
    }
}
