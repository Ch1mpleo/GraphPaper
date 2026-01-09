namespace GraphPaper.Domain.Enums
{

    /// <summary>
    ///     It's not a usual Role, more like a label for AI to remember who said what.
    ///     System: Instructions for the AI
    ///     Assistant - the memory: AI-generated response content
    ///     User: The actual text typed by the human in the chat interface.
    /// </summary>
    /// 
    public enum ChatRole
    {
        User = 1,
        Assistant = 2,
        System = 3
    }
}
