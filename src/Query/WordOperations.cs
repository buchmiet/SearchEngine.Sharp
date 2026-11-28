namespace SearchEngine.Query;

internal enum WordOperations
{
    None,           // not an operator: a term
    Or,
    Not,
    And,
    OpenParenthesis,
    CloseParenthesis
}
