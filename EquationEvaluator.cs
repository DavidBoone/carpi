public static class EquationEvaluator
{
    public static double Evaluate(string equation, byte[] data)
    {
        var tokens = Tokenize(equation, data);
        var postfix = ToPostfix(tokens);
        return EvaluatePostfix(postfix);
    }

    private enum TokenType { Number, Operator, LeftParen, RightParen, Function }

    private readonly record struct Token(TokenType Type, double Value = 0, char Op = '\0', string? Func = null);

    private static List<Token> Tokenize(string eq, byte[] data)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < eq.Length)
        {
            char c = eq[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Check for SIGNED function (case-insensitive)
            if (i + 6 <= eq.Length && eq[i..(i + 6)].Equals("Signed", StringComparison.OrdinalIgnoreCase))
            {
                // Check if followed by '(' - it's a function call
                int j = i + 6;
                while (j < eq.Length && eq[j] == ' ') j++;
                if (j < eq.Length && eq[j] == '(')
                {
                    tokens.Add(new Token(TokenType.Function, Func: "SIGNED"));
                    i = j; // will hit '(' on next iteration
                    continue;
                }
            }

            // Byte references A-D (case-insensitive)
            if ((c is 'A' or 'a' or 'B' or 'b' or 'C' or 'c' or 'D' or 'd') &&
                !IsPartOfWord(eq, i))
            {
                int idx = char.ToUpper(c) - 'A';
                double val = idx < data.Length ? data[idx] : 0;
                tokens.Add(new Token(TokenType.Number, val));
                i++;
                continue;
            }

            // Numbers (int or float)
            if (char.IsDigit(c) || (c == '.' && i + 1 < eq.Length && char.IsDigit(eq[i + 1])))
            {
                int start = i;
                while (i < eq.Length && (char.IsDigit(eq[i]) || eq[i] == '.'))
                    i++;
                var numStr = eq[start..i];
                tokens.Add(new Token(TokenType.Number, double.Parse(numStr, System.Globalization.CultureInfo.InvariantCulture)));
                continue;
            }

            if (c == '(') { tokens.Add(new Token(TokenType.LeftParen)); i++; continue; }
            if (c == ')') { tokens.Add(new Token(TokenType.RightParen)); i++; continue; }

            if (c is '+' or '*' or '/' or '<' or '>')
            {
                tokens.Add(new Token(TokenType.Operator, Op: c));
                i++;
                continue;
            }

            if (c == '-')
            {
                // Unary minus if at start, after operator, or after '('
                bool isUnary = tokens.Count == 0 ||
                    tokens[^1].Type == TokenType.Operator ||
                    tokens[^1].Type == TokenType.LeftParen;

                if (isUnary)
                {
                    // Read the number that follows
                    i++;
                    while (i < eq.Length && eq[i] == ' ') i++;

                    if (i < eq.Length && (char.IsDigit(eq[i]) || eq[i] == '.'))
                    {
                        int start = i;
                        while (i < eq.Length && (char.IsDigit(eq[i]) || eq[i] == '.'))
                            i++;
                        var numStr = eq[start..i];
                        tokens.Add(new Token(TokenType.Number, -double.Parse(numStr, System.Globalization.CultureInfo.InvariantCulture)));
                    }
                    else if (i < eq.Length && char.ToUpper(eq[i]) is >= 'A' and <= 'D' && !IsPartOfWord(eq, i))
                    {
                        int idx = char.ToUpper(eq[i]) - 'A';
                        double val = idx < data.Length ? data[idx] : 0;
                        tokens.Add(new Token(TokenType.Number, -val));
                        i++;
                    }
                    else
                    {
                        // Treat as subtraction with implicit 0
                        tokens.Add(new Token(TokenType.Number, 0));
                        tokens.Add(new Token(TokenType.Operator, Op: '-'));
                    }
                    continue;
                }

                tokens.Add(new Token(TokenType.Operator, Op: '-'));
                i++;
                continue;
            }

            // Skip cross-reference brackets [XXXXXX] - not supported for direct evaluation
            if (c == '[')
            {
                while (i < eq.Length && eq[i] != ']') i++;
                if (i < eq.Length) i++;
                tokens.Add(new Token(TokenType.Number, 0)); // placeholder
                continue;
            }

            // Skip unknown characters
            i++;
        }

        return tokens;
    }

    private static bool IsPartOfWord(string eq, int pos)
    {
        // Check if this letter is part of a longer word (like "Signed")
        if (pos > 0 && char.IsLetter(eq[pos - 1])) return true;
        if (pos + 1 < eq.Length && char.IsLetter(eq[pos + 1])) return true;
        return false;
    }

    private static int Precedence(char op) => op switch
    {
        '<' or '>' => 1, // bit shift
        '+' or '-' => 2,
        '*' or '/' => 3,
        _ => 0
    };

    private static List<Token> ToPostfix(List<Token> tokens)
    {
        var output = new List<Token>();
        var ops = new Stack<Token>();

        foreach (var token in tokens)
        {
            switch (token.Type)
            {
                case TokenType.Number:
                    output.Add(token);
                    break;

                case TokenType.Function:
                    ops.Push(token);
                    break;

                case TokenType.Operator:
                    while (ops.Count > 0 && ops.Peek().Type == TokenType.Operator &&
                           Precedence(ops.Peek().Op) >= Precedence(token.Op))
                    {
                        output.Add(ops.Pop());
                    }
                    ops.Push(token);
                    break;

                case TokenType.LeftParen:
                    ops.Push(token);
                    break;

                case TokenType.RightParen:
                    while (ops.Count > 0 && ops.Peek().Type != TokenType.LeftParen)
                        output.Add(ops.Pop());
                    if (ops.Count > 0) ops.Pop(); // pop '('
                    if (ops.Count > 0 && ops.Peek().Type == TokenType.Function)
                        output.Add(ops.Pop());
                    break;
            }
        }

        while (ops.Count > 0)
            output.Add(ops.Pop());

        return output;
    }

    private static double EvaluatePostfix(List<Token> postfix)
    {
        var stack = new Stack<double>();

        foreach (var token in postfix)
        {
            if (token.Type == TokenType.Number)
            {
                stack.Push(token.Value);
            }
            else if (token.Type == TokenType.Operator)
            {
                if (stack.Count < 2) return 0;
                var b = stack.Pop();
                var a = stack.Pop();
                stack.Push(token.Op switch
                {
                    '+' => a + b,
                    '-' => a - b,
                    '*' => a * b,
                    '/' => b != 0 ? a / b : 0,
                    '<' => (int)a << (int)b,  // left shift
                    '>' => (int)a >> (int)b,  // right shift
                    _ => 0
                });
            }
            else if (token.Type == TokenType.Function && token.Func == "SIGNED")
            {
                if (stack.Count < 1) return 0;
                var val = (int)stack.Pop();
                stack.Push(val > 127 ? val - 256 : val);
            }
        }

        return stack.Count > 0 ? stack.Pop() : 0;
    }
}
