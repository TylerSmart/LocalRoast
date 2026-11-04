using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace App.Infrastructure.Ingestion;

public static partial class JsonTruncationRepair
{
    [GeneratedRegex(@":\s*$")]
    private static partial Regex TrailingColonRegex();

    [GeneratedRegex(@",?\s*""[^""]*""\s*:\s*$")]
    private static partial Regex DanglingKeyColonRegex();

    [GeneratedRegex(@",\s*""[^""]*""\s*$")]
    private static partial Regex DanglingKeyRegex();

    [GeneratedRegex(@"\{\s*""[^""]*""\s*$")]
    private static partial Regex DanglingKeyInsideObjectRegex();

    public static bool TryRepair(string truncatedJson, out string repairedJson)
    {
        repairedJson = truncatedJson;
        if (string.IsNullOrWhiteSpace(truncatedJson))
        {
            return false;
        }

        var trimmed = truncatedJson.Trim();

        // 1. If already valid JSON, nothing to repair
        if (IsValidJson(trimmed))
        {
            repairedJson = trimmed;
            return true;
        }

        // 2. Try minimal close repair
        if (TryCloseDelimiters(trimmed, out var candidate1) && IsValidJson(candidate1))
        {
            repairedJson = candidate1;
            return true;
        }

        // 3. Try trimming back to last closed object '}' (e.g. dropping incomplete trailing step/timer/ingredient)
        if (TryTrimToLastCompleteObject(trimmed, out var candidate2) && IsValidJson(candidate2))
        {
            repairedJson = candidate2;
            return true;
        }

        // 4. Try trimming back to last closed array ']'
        if (TryTrimToLastCompleteArray(trimmed, out var candidate3) && IsValidJson(candidate3))
        {
            repairedJson = candidate3;
            return true;
        }

        return false;
    }

    public static bool IsValidJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCloseDelimiters(string input, out string result)
    {
        result = input;
        var inString = false;
        var isEscaped = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (inString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
        }

        var sb = new StringBuilder(input);
        if (inString)
        {
            sb.Append('"');
        }

        var current = CleanDanglingEnd(sb.ToString());
        result = CloseRemainingStack(current);
        return true;
    }

    private static bool TryTrimToLastCompleteObject(string input, out string result)
    {
        result = input;
        var lastCloseBraceIndex = -1;
        var inString = false;
        var isEscaped = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (inString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '}')
            {
                lastCloseBraceIndex = i;
            }
        }

        if (lastCloseBraceIndex <= 0)
        {
            return false;
        }

        var trimmedToObject = input[..(lastCloseBraceIndex + 1)];
        var cleaned = CleanDanglingEnd(trimmedToObject);
        result = CloseRemainingStack(cleaned);
        return true;
    }

    private static bool TryTrimToLastCompleteArray(string input, out string result)
    {
        result = input;
        var lastCloseBracketIndex = -1;
        var inString = false;
        var isEscaped = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (inString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == ']')
            {
                lastCloseBracketIndex = i;
            }
        }

        if (lastCloseBracketIndex <= 0)
        {
            return false;
        }

        var trimmedToArray = input[..(lastCloseBracketIndex + 1)];
        var cleaned = CleanDanglingEnd(trimmedToArray);
        result = CloseRemainingStack(cleaned);
        return true;
    }

    private static string CleanDanglingEnd(string input)
    {
        var s = input.TrimEnd();
        var changed = true;

        while (changed)
        {
            changed = false;

            if (TrailingColonRegex().IsMatch(s))
            {
                s = DanglingKeyColonRegex().Replace(s, "");
                changed = true;
                s = s.TrimEnd();
                continue;
            }

            if (DanglingKeyRegex().IsMatch(s))
            {
                s = DanglingKeyRegex().Replace(s, "");
                changed = true;
                s = s.TrimEnd();
                continue;
            }

            if (DanglingKeyInsideObjectRegex().IsMatch(s))
            {
                s = DanglingKeyInsideObjectRegex().Replace(s, "{");
                changed = true;
                s = s.TrimEnd();
                continue;
            }

            if (s.EndsWith(','))
            {
                s = s[..^1].TrimEnd();
                changed = true;
                continue;
            }
        }

        return s;
    }

    private static string CloseRemainingStack(string input)
    {
        var stack = new Stack<char>();
        var inString = false;
        var isEscaped = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (inString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c is '{' or '[')
            {
                stack.Push(c);
            }
            else if (c == '}' && stack.Count > 0 && stack.Peek() == '{')
            {
                stack.Pop();
            }
            else if (c == ']' && stack.Count > 0 && stack.Peek() == '[')
            {
                stack.Pop();
            }
        }

        var sb = new StringBuilder(input);
        while (stack.Count > 0)
        {
            var open = stack.Pop();
            if (open == '{')
            {
                sb.Append('}');
            }
            else if (open == '[')
            {
                sb.Append(']');
            }
        }

        return sb.ToString();
    }
}
