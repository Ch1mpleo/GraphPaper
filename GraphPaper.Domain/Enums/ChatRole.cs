namespace GraphPaper.Domain.Enums
{

    /// <summary>
    ///     It's not a Role, it's more like a label for AI to remember who said what.
    ///     System - the rules: Instructions for the AI
    ///     Assistant - the memory: AI-generated response content
    ///     User - the Question: The actual text typed by the human in the chat interface.
    /// </summary>
    /// 
    public enum ChatRole
    {
        User = 1,
        Assistant = 2,
        System = 3
    }
}
