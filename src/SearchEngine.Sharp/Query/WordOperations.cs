namespace SearchEngine.Query;

// Operator precedence (highest binds tightest, evaluated first):
//   NOT (3) > AND (2) > OR (1)
// This matches standard boolean algebra: "a OR b AND NOT c" = "a OR (b AND (NOT c))".
// OpenParenthesis/CloseParenthesis are structural tokens, not operators; they have
// no precedence value and are handled specially in the shunting-yard algorithm.
internal enum WordOperations
{
    None,           // not an operator — marks a word token
    Or,
    Not,
    And,
    OpenParenthesis,
    CloseParenthesis
}
