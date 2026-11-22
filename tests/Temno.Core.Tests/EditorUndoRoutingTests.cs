using Temno.Core.Editing;
using Xunit;

namespace Temno.Core.Tests;

public sealed class EditorUndoRoutingTests
{
    [Theory]
    [InlineData('Z', false, EditorHistoryCommand.Undo)]
    [InlineData('Z', true, EditorHistoryCommand.Redo)]
    [InlineData('Y', false, EditorHistoryCommand.Redo)]
    [InlineData('Y', true, EditorHistoryCommand.Redo)]
    [InlineData('z', false, EditorHistoryCommand.Undo)]
    [InlineData('S', false, EditorHistoryCommand.None)]
    [InlineData('D', false, EditorHistoryCommand.None)]
    public void ControlKeysNameTheHistoryCommand(char key, bool shift, EditorHistoryCommand expected)
        => Assert.Equal(expected, EditorUndoRouting.CommandFor(key, ctrl: true, shift));

    [Theory]
    [InlineData('Z', false)]
    [InlineData('Z', true)]
    [InlineData('Y', false)]
    public void WithoutControlNothingIsAHistoryKey(char key, bool shift)
        => Assert.Equal(EditorHistoryCommand.None, EditorUndoRouting.CommandFor(key, ctrl: false, shift));

    [Theory]
    [InlineData(EditorKeySurface.None)]
    [InlineData(EditorKeySurface.NoTextUndo)]
    [InlineData(EditorKeySurface.CommittedField)]
    public void SurfacesWithNothingTypedRunTheProjectHistory(EditorKeySurface surface)
    {
        Assert.Equal(EditorHistoryCommand.Undo, EditorUndoRouting.Route(surface, false, 'Z', ctrl: true, shift: false));
        Assert.Equal(EditorHistoryCommand.Redo, EditorUndoRouting.Route(surface, false, 'Y', ctrl: true, shift: false));
        Assert.Equal(EditorHistoryCommand.Redo, EditorUndoRouting.Route(surface, false, 'Z', ctrl: true, shift: true));
    }

    [Theory]
    [InlineData(EditorKeySurface.PendingField)]
    [InlineData(EditorKeySurface.FreeText)]
    public void TypingKeepsItsTextUndo(EditorKeySurface surface)
    {
        Assert.Equal(EditorHistoryCommand.None, EditorUndoRouting.Route(surface, false, 'Z', ctrl: true, shift: false));
        Assert.Equal(EditorHistoryCommand.None, EditorUndoRouting.Route(surface, false, 'Y', ctrl: true, shift: false));
        Assert.Equal(EditorHistoryCommand.None, EditorUndoRouting.Route(surface, false, 'Z', ctrl: true, shift: true));
    }

    [Fact]
    public void AnOpenDropDownKeepsTheKeys()
    {
        Assert.Equal(EditorHistoryCommand.None, EditorUndoRouting.Route(EditorKeySurface.NoTextUndo, true, 'Z', ctrl: true, shift: false));
        Assert.Equal(EditorHistoryCommand.None, EditorUndoRouting.Route(EditorKeySurface.NoTextUndo, true, 'Y', ctrl: true, shift: false));
    }

    [Fact]
    public void ACommittedFieldTurnsIntoTextUndoOnceTheUserTypes()
    {
        Assert.Equal(EditorHistoryCommand.Undo, EditorUndoRouting.Route(EditorKeySurface.CommittedField, false, 'Z', true, false));
        Assert.Equal(EditorHistoryCommand.None, EditorUndoRouting.Route(EditorKeySurface.PendingField, false, 'Z', true, false));
    }

    [Theory]
    [InlineData(EditorKeySurface.None)]
    [InlineData(EditorKeySurface.NoTextUndo)]
    [InlineData(EditorKeySurface.CommittedField)]
    public void OtherControlKeysAreNeverRouted(EditorKeySurface surface)
    {
        Assert.Equal(EditorHistoryCommand.None, EditorUndoRouting.Route(surface, false, 'S', ctrl: true, shift: false));
        Assert.Equal(EditorHistoryCommand.None, EditorUndoRouting.Route(surface, false, 'Z', ctrl: false, shift: false));
    }
}
